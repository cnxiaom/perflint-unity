using System;
using System.Collections.Generic;
using System.Reflection;
using PerfLint.L10n;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Runtime (Play Mode) performance sampler. Unlike the other "static scans" — this reads Profiler data
    /// while the game is actually running, to locate bottlenecks that only surface at runtime
    /// (stutters, per-frame GC, memory growth, excessive Draw Calls, CPU hotspots).
    ///
    /// Design:
    /// - **Local-first / zero telemetry**: all data flows through Unity's in-process ProfilerRecorder /
    ///   HierarchyFrameDataView; nothing is uploaded.
    /// - **Counter layer** (ProfilerRecorder, stable API): frame time, GC/frame, memory, Draw/SetPass/Batches/triangles.
    /// - **Hotspot layer** (HierarchyFrameDataView, Unity native side has already merged self-time by marker):
    ///   pins "what is slow" down to specific markers / scripts.
    ///   This layer is wrapped in a single try/catch — cross-version API risk is high; on failure, hotspots
    ///   are empty, but **the counter-layer diagnosis is unaffected**
    ///   (following ScanRunner's philosophy of "a single-point failure must not abort the whole run").
    ///
    /// Usage: Start() → call LastValue() for live readings during sampling → Stop() produces a RuntimeProfileResult.
    /// </summary>
    public sealed class RuntimeSampler : IDisposable
    {
        // Ring buffer capacity: ≈33 s at 60 fps. Sufficient to cover one manual sampling session; memory overhead is negligible (≈32 KB per counter).
        private const int Capacity = 2000;

        // Per-object-category counters (Memory category) for RUN.MEM003 — "which category of objects/assets is growing" (leak attribution without heap snapshots).
        // Availability varies by platform/Unity version; ProfilerRecorder.Valid degrades safely (that entry simply has no data and MEM003 skips it).
        private static readonly string[] CategoryCounterNames =
        {
            "GameObject Count", "Object Count", "Texture Count", "Texture Memory", "Mesh Count", "Mesh Memory", "Material Count",
        };

        private struct Channel
        {
            public string Key;
            public ProfilerRecorder Recorder;
            public bool Valid => Recorder.Valid;
        }

        /// <summary>
        /// The highest and second-highest single-frame self-time for a single marker across frames.
        /// The value reported externally uses **Max2 (second-highest)** rather than the outright maximum —
        /// a single extreme "everything is slow" frame (e.g., one full A* recompute) raises every marker's
        /// per-frame maximum simultaneously, causing false spike flags across the board.
        /// A genuinely periodic spike will recur across multiple frames, so Max2 remains high;
        /// one that was only inflated by that one frame will drop back on Max2.
        /// In other words: use the second-highest peak to eliminate contamination from a single extreme frame.
        /// </summary>
        private struct PeakPair
        {
            public double Max1; // Highest single frame
            public double Max2; // Second-highest single frame
            public void Add(double v)
            {
                if (v >= Max1) { Max2 = Max1; Max1 = v; }
                else if (v > Max2) { Max2 = v; }
            }
            // Reported value: if seen in ≥2 frames, use the second-highest (excludes the single extreme frame); if seen in only 1 frame there is nothing to exclude, so fall back to the highest.
            public double Reported => Max2 > 0 ? Max2 : Max1;
        }

        private readonly List<Channel> _channels = new List<Channel>();
        private bool _running;
        private double _startTime;
        private int _startFrameIndex;
        private bool _prevProfilerEnabled;
        private int _capturedStartFrame;
        private double _capturedAvgFrameNs;
        private bool _wasDeepProfile;
        private List<double> _capturedMainThread; // Per-frame main-thread times during the sampling window, used to include the slowest (stutter spike) frames in the merge
        private Action _cancelHotspots;
        private List<double> _gpuFrameMs;          // Per-frame GPU times measured by FrameTimingManager (ms); more reliable than the ProfilerRecorder counter
        private FrameTiming[] _frameTimingBuf;     // Reusable buffer for GetLatestTimings (capacity 1, fetches the most recent frame)

        // ── Live spike capture (root fix for FPS003 attribution) ──
        // The counter ring buffer (2000 frames) far outlives the Profiler backend's Hierarchy frame retention (only a few hundred frames), so a spike the
        // counter reports often can't be re-read from frame data at Stop() time — already evicted. The deferred worst-frame merge then lands on a normal
        // frame, producing a self-contradictory FPS003 ("370 ms frame mostly spent in 0.1 ms markers"). Fix: while sampling, watch each new frame's
        // main-thread time; the moment one spikes, snapshot its Hierarchy right then (the newest frame is guaranteed still retained). This live worst frame
        // wins over the deferred merge and is reconciled against the counter max in RuntimeAnalyzer (wfIsSpikeFrame).
        private int _lastSpikeFrameSeen;         // Highest ProfilerDriver frame index already evaluated for spikes this session (avoids re-processing)
        private double _spikeBaselineMs;          // EMA of main-thread ms — the "normal" baseline a candidate spike must exceed
        // Best-SCORING spike frame per distinct culprit (script+method). A level-gen freeze is a cluster of heavy frames across several phases, so we keep
        // one per culprit (deduped) rather than a single worst — RUN.FPS003 then reports each with its own Locate. Score (magnitude × attribution weight)
        // means an attributable spike beats a larger but unmappable one (e.g. an EditorLoop/Deep-Profile artifact frame).
        private readonly Dictionary<string, WorstFrameInfo> _liveSpikesByCulprit = new Dictionary<string, WorstFrameInfo>(16);
        private GcAllocSite _lastGcSite; // top steady-state per-frame GC allocator from the last hotspot merge (RUN.GC001 runtime attribution); null when none dominant
        /// <summary>Top steady-state per-frame GC allocator found by the last BeginHotspots merge (or null). Read by the window right after onComplete.</summary>
        public GcAllocSite LastGcSite => _lastGcSite;
        // A detected spike whose Hierarchy isn't replayable yet: the just-ended freeze frame's frame-data commonly lags 1-2 frames behind, so we
        // remember the frame hint + magnitude and retry the snapshot on later ticks until it becomes valid (or scrolls out of the retained window).
        // Detected spikes awaiting capture. A level-gen freeze produces SEVERAL spiking frames (different phases) in quick succession, so we track a small
        // queue — not one — and retry each until its Hierarchy becomes replayable (or it scrolls out), so no distinct culprit is dropped.
        private struct PendingSpike { public int Frame; public double Ms; public int Attempts; }
        private readonly List<PendingSpike> _pendingSpikes = new List<PendingSpike>(16);
        private const int MaxPendingSpikes = 12;  // cap concurrent pending spikes (drop the smallest to make room) to bound per-tick frame-tree builds
        private List<ProfilerRecorderSample> _spikeScanBuf; // reused buffer for scanning recent Main Thread samples (freeze-recovery may add several frames per tick)

        // GPU-time column index for HierarchyFrameDataView — the column constant was added only in newer Unity (2022+)
        // and is absent from the public API of 2021.3; referencing it directly would cause a compile error, and the
        // name may differ across versions. Therefore we reflect over all public static int column constants, pick the one
        // whose name contains "Gpu" (preferring one that also contains "Total"); if found (≥0) we read it during the
        // merge (same source as the Profiler "GPU ms" column), otherwise (-1) skip it and fall back to FrameTimingManager.
        // Resolved once and cached for the session.
        private static readonly int _gpuColumn = ResolveGpuColumn();
        private static int ResolveGpuColumn()
        {
            try
            {
                int best = -1; bool bestIsTotal = false;
                foreach (var f in typeof(HierarchyFrameDataView).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(int)) continue;
                    string n = f.Name;
                    if (n.IndexOf("column", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.IndexOf("Gpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.IndexOf("Self", StringComparison.OrdinalIgnoreCase) >= 0) continue; // Want the total column, not self
                    if (n.IndexOf("Percent", StringComparison.OrdinalIgnoreCase) >= 0) continue; // Want milliseconds, not percentage
                    bool isTotal = n.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (best < 0 || (isTotal && !bestIsTotal)) { best = (int)f.GetValue(null); bestIsTotal = isTotal; }
                }
                return best;
            }
            catch { return -1; }
        }

        // GC-allocated-memory column (bytes) for HierarchyFrameDataView — used to attribute a spike's allocations to the culprit method (e.g. "PlaceObstaclesAsync ~2.0 MB").
        // Resolved by reflection like the GPU column (constant name/availability varies by Unity version); -1 = unavailable → GC is simply omitted from the spike text.
        private static readonly int _gcColumn = ResolveGcColumn();
        private static int ResolveGcColumn()
        {
            try
            {
                foreach (var f in typeof(HierarchyFrameDataView).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(int)) continue;
                    string n = f.Name;
                    if (n.IndexOf("column", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.IndexOf("Gc", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Alloc", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (int)f.GetValue(null); // columnGcMemory
                }
            }
            catch { /* fall through */ }
            return -1;
        }

        // Reusable buffers for HierarchyFrameDataView traversal (main thread, avoids per-frame allocations). See AccumulateHierarchySelfTime.
        private static readonly Stack<int> _itemStack = new Stack<int>(2048);
        private static readonly List<int>  _childBuf  = new List<int>(64);
        private static readonly Dictionary<string, double> _frameDict = new Dictionary<string, double>(4096); // Per-marker self-time for a single frame

        public bool IsRunning => _running;

        public void Start()
        {
            if (_running) return;

            _channels.Clear();
            Add("Main Thread", ProfilerCategory.Internal, "Main Thread");
            Add("GC Allocated In Frame", ProfilerCategory.Memory, "GC Allocated In Frame");
            Add("Total Used Memory", ProfilerCategory.Memory, "Total Used Memory");
            // Memory breakdown: managed heap (GC) vs. graphics resources (Gfx) vs. total reserved.
            // Used to narrow down "memory grew" to "which side it grew on".
            // Individual counters may be unavailable on certain platforms/versions; .Valid degrades safely (does not affect other diagnostics).
            Add("Total Reserved Memory", ProfilerCategory.Memory, "Total Reserved Memory");
            Add("GC Used Memory", ProfilerCategory.Memory, "GC Used Memory");
            Add("Gfx Used Memory", ProfilerCategory.Memory, "Gfx Used Memory");
            // Object/asset category counters — feed RUN.MEM003 (which category of objects is accumulating). Invalid counters degrade to no-data.
            foreach (var n in CategoryCounterNames) Add(n, ProfilerCategory.Memory, n);
            Add("Draw Calls Count", ProfilerCategory.Render, "Draw Calls Count");
            Add("SetPass Calls Count", ProfilerCategory.Render, "SetPass Calls Count");
            Add("Batches Count", ProfilerCategory.Render, "Batches Count");
            Add("Triangles Count", ProfilerCategory.Render, "Triangles Count");
            Add("Vertices Count", ProfilerCategory.Render, "Vertices Count");
            // GPU frame time: available on platforms that support this counter (PC/Console), useful for detecting GPU-bound bottlenecks.
            // When unsupported, ProfilerRecorder.Valid == false; Stop() silently degrades to null when reading it.
            Add("GPU Frame Time", ProfilerCategory.Render, "GPU Frame Time");

            // Enable Profiler recording so that frame data can be replayed for hotspot merging at Stop() time.
            // The previous state is recorded and restored at Stop(), leaving the user's existing Profiler usage undisturbed.
            // In Play Mode the Profiler collects the Player (not the editor itself) by default, so no extra configuration is needed.
            _prevProfilerEnabled = ProfilerDriver.enabled;
            ProfilerDriver.enabled = true;

            // GPU frame time comes from FrameTimingManager (the same data source as the Profiler "GPU Usage" module), captured once per frame.
            // The "GPU Frame Time" ProfilerRecorder counter is unavailable on most platforms (even when the Profiler can display it),
            // so FrameTimingManager is the primary source and the counter is the fallback.
            _gpuFrameMs = new List<double>(Capacity);
            if (_frameTimingBuf == null) _frameTimingBuf = new FrameTiming[1];
            FrameTimingManager.CaptureFrameTimings();

            _lastSpikeFrameSeen = ProfilerDriver.lastFrameIndex; // only evaluate frames produced after Start (skip pre-sampling scene-load spikes)
            _spikeBaselineMs = 0;
            _liveSpikesByCulprit.Clear();
            _pendingSpikes.Clear();
            _lastGcSite = null;
            EditorApplication.update += EditorFrameTick;

            _startTime = EditorApplication.timeSinceStartup;
            _startFrameIndex = ProfilerDriver.lastFrameIndex;
            _running = true;
        }

        // Called once per editor frame (≈ once per game frame in Play Mode): capture GPU time (every frame, to advance the timing window) and,
        // when a frame has spiked, snapshot its call stack live before the Profiler evicts it.
        private void EditorFrameTick()
        {
            if (!_running) return;
            CaptureGpuTiming();
            try { CaptureSpikeFrame(); }
            catch { /* Live spike capture is best-effort; on failure BeginHotspots' deferred worst-frame merge still runs. */ }
        }

        // CaptureFrameTimings must be called every frame to advance the timing window.
        private void CaptureGpuTiming()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                uint n = FrameTimingManager.GetLatestTimings(1, _frameTimingBuf);
                if (n > 0)
                {
                    double gpuMs = _frameTimingBuf[0].gpuFrameTime; // milliseconds
                    if (gpuMs > 0 && _gpuFrameMs.Count < Capacity) _gpuFrameMs.Add(gpuMs);
                }
            }
            catch { /* Platform does not support FrameTimingManager → leave empty; Stop() falls back to the ProfilerRecorder counter */ }
        }

        // Absolute freeze floor (ms): any frame at/above this is a candidate spike regardless of baseline (matches FPS003's 100 ms perceptible-freeze floor).
        private const double SpikeFloorMs = 100.0;
        // Relative trigger: a frame must also be this many times the running baseline to count as a spike (separates an outlier from "generally slow").
        private const double SpikeBaselineMult = 3.0;

        /// <summary>
        /// While sampling, detect a spiked frame and snapshot its Hierarchy immediately (the newest frame is still retained by the Profiler backend).
        /// Cheap gate first: the "Main Thread" counter's last value drives an EMA baseline, so the expensive frame-tree build happens only on real spikes.
        /// On a trigger, snapshot the heavier of the two newest frames — this absorbs a possible 1-frame lag between the counter's LastValue and
        /// lastFrameIndex, and the newest frame may not yet be replayable (frame.valid == false), in which case the second-newest one covers it.
        /// The stored magnitude comes from the snapshotted frame's own self-time total, not the (possibly lagged) trigger reading.
        /// </summary>
        // Retry window (ticks) to wait for a detected spike's frame data to become replayable before giving up.
        private const int PendingSpikeMaxAttempts = 12;

        // Per-spike capture diagnostics are silent by default (would spam the Console). Set env PERFLINT_SPIKE_LOG=1 to re-enable them for troubleshooting.
        private static readonly bool SpikeDebugLog = Environment.GetEnvironmentVariable("PERFLINT_SPIKE_LOG") == "1";

        private void CaptureSpikeFrame()
        {
            // 1) Fulfil previously detected spikes whose frame data may only now have become replayable (the freeze frame typically lags 1-2 frames).
            ResolvePendingSpikes();

            int cur = ProfilerDriver.lastFrameIndex;
            if (cur < 0 || cur <= _lastSpikeFrameSeen) return; // no new finalized frame(s) since last tick
            int newFrames = cur - _lastSpikeFrameSeen;
            _lastSpikeFrameSeen = cur;

            // 2) Find the worst frame among ALL frames added since the last tick — NOT just LastValue. A long freeze runs on this very (editor-update)
            //    thread, so when the tick resumes lastFrameIndex has already advanced past the spike and LastValue reflects a later, normal frame.
            FindRecentSpikeFrame(cur, newFrames, out int spikeFrame, out double spikeMs);

            // Baseline EMA from the latest (typically normal) frame reading.
            double latest = LastValue("Main Thread") / 1_000_000.0;
            if (latest > 0)
            {
                if (_spikeBaselineMs <= 0) _spikeBaselineMs = latest;
                else _spikeBaselineMs = _spikeBaselineMs * 0.95 + latest * 0.05;
            }

            bool isSpike = spikeMs >= SpikeFloorMs || (spikeMs >= 33.0 && _spikeBaselineMs > 0 && spikeMs >= _spikeBaselineMs * SpikeBaselineMult);
            if (!isSpike) return;

            // Pursue EVERY genuine spike (no relative-to-max gate — that would drop smaller-but-real culprits, e.g. a 358ms phase, once a big spike is seen).
            // Per-culprit aggregation keeps only the worst frame per cause, so tracking many spikes doesn't bloat the result.
            foreach (var p in _pendingSpikes)
                if (Math.Abs(p.Frame - spikeFrame) <= 2) return; // already tracking this spike (freeze recovery reports it across a couple ticks)

            if (_pendingSpikes.Count >= MaxPendingSpikes)
            {
                int minIdx = 0;
                for (int i = 1; i < _pendingSpikes.Count; i++) if (_pendingSpikes[i].Ms < _pendingSpikes[minIdx].Ms) minIdx = i;
                if (_pendingSpikes[minIdx].Ms >= spikeMs) return; // queue full and this isn't bigger than the smallest → skip
                _pendingSpikes.RemoveAt(minIdx);
            }
            _pendingSpikes.Add(new PendingSpike { Frame = spikeFrame, Ms = spikeMs, Attempts = PendingSpikeMaxAttempts });
            if (SpikeDebugLog) Debug.Log("[PerfLint] " + L.Tr($"Runtime spike detected (~{spikeMs:0} ms at frame {spikeFrame}); will snapshot its call stack once the Profiler makes it replayable.", $"检测到运行时尖刺（约 {spikeMs:0} ms，帧 {spikeFrame}），将在 Profiler 使其可回放后抓取其调用栈。"));
            ResolvePendingSpikes(); // try immediately in case it's already replayable
        }

        // Scan the Main Thread counter samples added since the last tick to find the worst frame (value + approximate frame index, end-aligned to cur).
        private void FindRecentSpikeFrame(int cur, int newFrames, out int spikeFrame, out double spikeMs)
        {
            spikeMs = LastValue("Main Thread") / 1_000_000.0;
            spikeFrame = cur;
            if (newFrames <= 1) return; // common case: only one new frame → LastValue already is it

            foreach (var c in _channels)
            {
                if (c.Key != "Main Thread" || !c.Valid || c.Recorder.Count == 0) continue;
                if (_spikeScanBuf == null) _spikeScanBuf = new List<ProfilerRecorderSample>(Capacity);
                _spikeScanBuf.Clear();
                c.Recorder.CopyTo(_spikeScanBuf); // old→new
                int count = _spikeScanBuf.Count;
                int scan = Math.Min(newFrames, count);
                for (int i = count - scan; i < count; i++)
                {
                    double m = _spikeScanBuf[i].Value / 1_000_000.0;
                    if (m > spikeMs)
                    {
                        spikeMs = m;
                        spikeFrame = cur - (count - 1 - i); // end-align: newest sample ↔ cur
                    }
                }
                break;
            }
        }

        // Retry snapshotting each pending spike frame. The just-ended frame's Hierarchy is often not yet replayable, so we search a small window around
        // the hint (absorbing counter/frame-index misalignment), confirm by matching the captured self-total against the detected magnitude, then
        // aggregate it into _liveSpikesByCulprit (one entry per distinct culprit). Runs on every tick after the freeze — the game has resumed, so building
        // a few frame trees here doesn't stall play.
        private void ResolvePendingSpikes()
        {
            if (_pendingSpikes.Count == 0) return;
            int first = ProfilerDriver.firstFrameIndex;
            int last  = ProfilerDriver.lastFrameIndex;

            for (int idx = _pendingSpikes.Count - 1; idx >= 0; idx--)
            {
                var p = _pendingSpikes[idx];
                if (p.Frame < first || p.Attempts <= 0)
                {
                    if (SpikeDebugLog) Debug.Log("[PerfLint] " + L.Tr($"Gave up capturing the ~{p.Ms:0} ms spike's call stack (its frame data was evicted or never became replayable in time).", $"放弃抓取约 {p.Ms:0} ms 尖刺的调用栈（其帧数据已被淘汰或始终未变为可回放）。"));
                    _pendingSpikes.RemoveAt(idx);
                    continue;
                }
                p.Attempts--;
                _pendingSpikes[idx] = p; // persist the decremented attempt count

                double bestSelf = 0; WorstFrameInfo bestSnap = null; string bestRawTop = "";
                for (int fi = p.Frame - 2; fi <= p.Frame + 2; fi++)
                {
                    if (fi < first || fi > last) continue;
                    var frame = ProfilerDriver.GetHierarchyFrameDataView(
                        fi, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                        HierarchyFrameDataView.columnSelfTime, false);
                    try
                    {
                        if (frame == null || !frame.valid) continue;
                        double selfTotal = FillFrameDict(frame); // fills _frameDict, returns this frame's total self-time (ms)
                        if (selfTotal > bestSelf)
                        {
                            bestSnap = SnapshotWorstFrame(frame, selfTotal); // reads _frameDict, which still holds this frame
                            bestRawTop = SpikeDebugLog ? RawTopSelfLeaders(5) : ""; // diagnostic only: raw self incl. wait/GC/engine (which SnapshotWorstFrame filters out)
                            bestSelf = selfTotal;
                        }
                    }
                    finally { frame?.Dispose(); }
                }

                // Confirm we found the actual spike frame (self-total within range of the detected magnitude), not just a normal neighbour that's ready first.
                if (bestSnap != null && bestSelf >= p.Ms * 0.5)
                {
                    // Aggregate by culprit: keep the highest-SCORING spike frame PER distinct culprit (script+method) so each level-gen phase is its own finding.
                    string culpritKey = CulpritKey(bestSnap);
                    if (!_liveSpikesByCulprit.TryGetValue(culpritKey, out var existing) ||
                        SpikeFrameScore(bestSnap) > SpikeFrameScore(existing))
                        _liveSpikesByCulprit[culpritKey] = bestSnap;
                    if (SpikeDebugLog)
                    {
                        string culpritDbg = (bestSnap.UserCallPath != null && bestSnap.UserCallPath.Count > 0)
                            ? bestSnap.UserCallPath[bestSnap.UserCallPath.Count - 1].Marker
                            : "(no script mapped on heaviest path)";
                        Debug.Log("[PerfLint] " + L.Tr($"Captured the spike frame's call stack: {bestSelf:0} ms self-time total (frame ~{p.Frame}); heaviest-path script: {culpritDbg}; raw top self (incl. wait/GC/engine): {bestRawTop}.", $"已抓取尖刺帧调用栈：self-time 合计 {bestSelf:0} ms（帧约 {p.Frame}）；最重路径脚本：{culpritDbg}；原始 top self（含 wait/GC/引擎）：{bestRawTop}。"));
                    }
                    _pendingSpikes.RemoveAt(idx);
                }
                // else: not replayable yet (or only normal neighbours ready) → keep pending and retry next tick.
            }
        }

        private void Add(string key, ProfilerCategory category, string statName)
        {
            // capacity>1 → retains ring buffer history, enabling both LastValue reads and CopyTo over the full window.
            var rec = ProfilerRecorder.StartNew(category, statName, Capacity);
            _channels.Add(new Channel { Key = key, Recorder = rec });
        }

        /// <summary>Live reading (value from the most recent frame). Returns 0 if the counter is invalid or has no samples. Intended for UI refresh every frame.</summary>
        public double LastValue(string key)
        {
            foreach (var c in _channels)
            {
                if (c.Key != key) continue;
                if (!c.Valid || c.Recorder.Count == 0) return 0;
                return c.Recorder.LastValue;
            }
            return 0;
        }

        public double CurrentDurationSeconds =>
            _running ? EditorApplication.timeSinceStartup - _startTime : 0;

        /// <summary>
        /// Stops sampling and produces the counter-layer result (synchronous, very fast). Hotspot merging is a heavy
        /// operation and must be completed asynchronously by calling BeginHotspots afterwards.
        /// RuntimeProfileResult.HotspotsAvailable == false on the returned result is the normal state; hotspots are filled in via the callback.
        /// </summary>
        public RuntimeProfileResult Stop()
        {
            if (!_running) return null;
            _running = false;
            EditorApplication.update -= EditorFrameTick;

            double duration = EditorApplication.timeSinceStartup - _startTime;

            var series = new Dictionary<string, List<double>>();
            foreach (var c in _channels)
                series[c.Key] = c.Valid ? ReadSamples(c.Recorder) : new List<double>();

            int frameCount = 0;
            foreach (var s in series.Values)
                if (s.Count > frameCount) frameCount = s.Count;

            MetricStats Stat(string key) =>
                series.TryGetValue(key, out var s) ? new MetricStats(key, s) : new MetricStats(key, null);

            var frameTime = Stat("Main Thread");

            // GPU frame time: prefer the measured value from FrameTimingManager (consistent with the Profiler GPU module, unit ms→ns); fall back to the counter if empty.
            MetricStats gpuStat;
            if (_gpuFrameMs != null && _gpuFrameMs.Count > 0)
            {
                var gpuNs = new List<double>(_gpuFrameMs.Count);
                foreach (var ms in _gpuFrameMs) gpuNs.Add(ms * 1_000_000.0);
                gpuStat = new MetricStats("GPU Frame Time", gpuNs);
            }
            else gpuStat = Stat("GPU Frame Time");

            // Save the context needed for hotspot merging; BeginHotspots will use it.
            _capturedStartFrame = _startFrameIndex;
            _capturedAvgFrameNs = frameTime.HasData ? frameTime.Avg : 0;
            _wasDeepProfile = ProfilerDriver.deepProfiling;
            _capturedMainThread = series.TryGetValue("Main Thread", out var mt) ? mt : null;

            // The scene batching snapshot must be captured while the scene is still loaded (Play Mode) — once it exits the Renderers are destroyed and unreachable.
            // O(number of Renderers), millisecond-scale, safe to capture synchronously.
            var sceneBatching = SceneBatchingSnapshot.Capture();

            // Release the counters and restore the Profiler state (HierarchyFrameDataView remains usable; no need for the Profiler to keep recording).
            DisposeChannels();
            ProfilerDriver.enabled = _prevProfilerEnabled;

            var categoryCounters = new Dictionary<string, MetricStats>(CategoryCounterNames.Length);
            foreach (var n in CategoryCounterNames) categoryCounters[n] = Stat(n);

            return new RuntimeProfileResult(
                durationSeconds: duration,
                frameCount: frameCount,
                frameTimeNs: frameTime,
                gcPerFrameBytes: Stat("GC Allocated In Frame"),
                totalMemoryBytes: Stat("Total Used Memory"),
                totalReservedBytes: Stat("Total Reserved Memory"),
                gcUsedBytes: Stat("GC Used Memory"),
                gfxUsedBytes: Stat("Gfx Used Memory"),
                drawCalls: Stat("Draw Calls Count"),
                setPassCalls: Stat("SetPass Calls Count"),
                batches: Stat("Batches Count"),
                triangles: Stat("Triangles Count"),
                vertices: Stat("Vertices Count"),
                gpuFrameTimeNs: gpuStat,
                hotspots: null,
                hotspotsAvailable: false,
                wasDeepProfile: _wasDeepProfile,
                sceneBatching: sceneBatching,
                categoryCounters: categoryCounters);
        }

        /// <summary>
        /// Asynchronously merge hotspots (batched on the main thread, running at most 30ms per tick to keep the editor responsive).
        /// onProgress(done, total): can be used to update progress UI.
        /// onComplete(hotspots, worstFrame, gpuFrameTimeNs, ok): invoked on the main thread when merging completes. worstFrame is the slowest-single-frame attribution (may be null);
        /// gpuFrameTimeNs is the GPU frame-time statistic read from frame data (same source as the Profiler "GPU ms" column, may be null — falls back to the sampling-period source when there is no GPU data).
        /// </summary>
        public void BeginHotspots(
            Action<List<Hotspot>, IReadOnlyList<WorstFrameInfo>, MetricStats, bool> onComplete,
            Action<int, int> onProgress = null)
        {
            // Cancel any previous merge that has not yet finished (should not exist in theory; defensive handling).
            _cancelHotspots?.Invoke();

            int first = Math.Max(_capturedStartFrame, ProfilerDriver.firstFrameIndex);
            int last  = ProfilerDriver.lastFrameIndex;

            if (last < first)
            {
                // No replayable frames, but spikes may still have been captured live during sampling.
                onComplete(new List<Hotspot>(), BuildWorstFrames(null), null, true);
                return;
            }

            var acc = new Dictionary<string, double>();
            var peak = new Dictionary<string, PeakPair>();
            var gpuMsList = new List<double>(); // GPU time per sampled frame (ms, read from columnTotalGpuTime of the HierarchyFrameDataView root node)
            var gcByScript = new Dictionary<string, double>();    // steady-state GC-alloc bytes accumulated per user script (RUN.GC001 runtime attribution)
            var gcMethodByScript = new Dictionary<string, string>();
            _lastGcSite = null;
            _scriptPathCache.Clear(); // Reset the script-resolution cache each sampling run (avoids stale mappings from scripts added/removed/renamed across sessions)
            int framesSeen        = 0; // Total sampled frames (uniform + spike) — feeds peaks, used for logging
            int uniformFramesSeen = 0; // Uniform (representative) frames only — the denominator for per-frame average/share, prevents spike frames from contaminating the average
            bool cancelled  = false;

            // Slowest-single-frame attribution: track the frame with the highest total self-time among all sampled frames and snapshot its top markers (for RUN.FPS003).
            // Independent of acc/peak — spike frames are already in the sampling set (SelectFramesForHotspots includes the slowest few); this just additionally picks out the single most severe frame.
            double worstFrameTotalMs = 0;
            WorstFrameInfo worstFrame = null;

            // Frame count is the only effective lever: in practice the cost of processing a single frame of Deep Profile data is inherently high
            // (GetHierarchyFrameDataView must scan a frame's massive sample set when building the tree on the native side, regardless of merge approach).
            // So we pick a small number of frames: uniform sampling (representing persistent hotspots that run every frame)
            // + the slowest few frames (to capture hotspots in occasional stutter-spike frames, which uniform sampling might otherwise skip).
            // Key: split uniform frames from spike frames — only uniform frames count toward "per-frame average/share" (steady state); spike frames only feed peaks. Otherwise
            // dividing the self-time of a load/JIT super-frame by a small sample fabricates garbage like "297ms per frame" with a share exceeding 100%.
            var framesToProcess = SelectFramesForHotspots(first, last, out var uniformFrames);
            int plannedFrames = framesToProcess.Count;
            int idx = 0;

            var mergeWatch = System.Diagnostics.Stopwatch.StartNew();
            _cancelHotspots = () => cancelled = true;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (cancelled)
                {
                    EditorApplication.update -= tick;
                    return;
                }

                // Run at most 30ms per tick, balancing throughput and responsiveness.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (idx < framesToProcess.Count && sw.ElapsedMilliseconds < 30)
                {
                    var frame = ProfilerDriver.GetHierarchyFrameDataView(
                        framesToProcess[idx], 0,
                        HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                        HierarchyFrameDataView.columnSelfTime, false);
                    try
                    {
                        if (frame != null && frame.valid)
                        {
                            framesSeen++;
                            bool isUniform = uniformFrames.Contains(framesToProcess[idx]);
                            if (isUniform) uniformFramesSeen++;
                            AccumulateGcByScript(frame, gcByScript, gcMethodByScript); // GC attribution across ALL frames (steady + spike) → RUN.GC001's runtime allocator
                            // GPU frame time: read the root node's GPU time column (same source as the Profiler Hierarchy "GPU ms" column).
                            // The column constant exists only in 2022+; on older versions _gpuColumn==-1 is skipped and FrameTimingManager serves as the fallback.
                            if (_gpuColumn >= 0)
                            {
                                double gpuMs = frame.GetItemColumnDataAsFloat(frame.GetRootItemID(), _gpuColumn);
                                if (gpuMs > 0) gpuMsList.Add(gpuMs);
                            }
                            double frameTotalMs = AccumulateHierarchySelfTime(frame, acc, peak, isUniform);
                            // This frame's total self-time hit a new high → snapshot its top markers (_frameDict still holds this frame's data at this point; it is only cleared on the next call).
                            if (frameTotalMs > worstFrameTotalMs)
                            {
                                worstFrameTotalMs = frameTotalMs;
                                worstFrame = SnapshotWorstFrame(frame, frameTotalMs);
                            }
                        }
                    }
                    finally
                    {
                        frame?.Dispose();
                    }
                    idx++;
                }

                onProgress?.Invoke(idx, plannedFrames);

                if (idx >= framesToProcess.Count)
                {
                    EditorApplication.update -= tick;
                    _cancelHotspots = null;
                    try
                    {
                        var hotspots = BuildHotspots(acc, peak, uniformFramesSeen, _capturedAvgFrameNs);
                        // GPU time read from frame data (ms→ns). null if empty, in which case the caller falls back to the sampling-period GPU source.
                        MetricStats gpuFromFrames = null;
                        if (gpuMsList.Count > 0)
                        {
                            var gpuNs = new List<double>(gpuMsList.Count);
                            foreach (var ms in gpuMsList) gpuNs.Add(ms * 1_000_000.0);
                            gpuFromFrames = new MetricStats("GPU Frame Time", gpuNs);
                        }
                        if (framesSeen > 0)
                            Debug.Log("[PerfLint] " + L.Tr($"CPU hotspot merge complete: sampled {framesSeen} frames ({uniformFramesSeen} representative frames counted into the average), {acc.Count} markers, took {mergeWatch.ElapsedMilliseconds} ms.", $"CPU 热点归并完成：抽样 {framesSeen} 帧（其中 {uniformFramesSeen} 代表帧计入均值）、{acc.Count} 个 marker、用时 {mergeWatch.ElapsedMilliseconds} ms。"));
                        _lastGcSite = BuildTopGcSite(gcByScript, gcMethodByScript); // heaviest per-frame GC allocator measured (RUN.GC001 Locate target)
                        // The live-captured spikes (per culprit) plus the deferred merge's worst frame, deduped and ranked — RUN.FPS003 reports one per culprit.
                        onComplete(hotspots, BuildWorstFrames(worstFrame), gpuFromFrames, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[PerfLint] " + L.Tr($"Hotspot merge failed: {ex.Message}", $"热点归并失败：{ex.Message}"));
                        onComplete(new List<Hotspot>(), BuildWorstFrames(null), null, false);
                    }
                }
            };

            EditorApplication.update += tick;
        }

        /// <summary>Cancel an in-progress BeginHotspots async merge (called when the window closes or sampling restarts).</summary>
        public void CancelHotspots() => _cancelHotspots?.Invoke();

        // Frame selection for merging: uniform sampling + slowest frames. The former represents persistent hotspots; the latter ensures occasional stutter-spike frames get attributed (which uniform sampling would otherwise miss).
        private const int UniformHotspotFrames = 8;
        private const int SpikeHotspotFrames   = 6;

        /// <summary>
        /// Select the set of frame indices used for hotspot merging: UniformHotspotFrames uniformly sampled frames (persistent hotspots) + the
        /// SpikeHotspotFrames frames with the highest main-thread time (stutter spikes). The latter are located using the per-frame Main Thread time over the sampling period — sequence index i approximately corresponds
        /// to frame index first+i (ProfilerRecorder produces one sample per frame, aligned to the sampling window). Returns a deduplicated, ascending list of frame indices.
        /// The uniformFrames out parameter returns the "uniform frames" subset: the caller uses it to count only uniform frames toward per-frame average/share (steady state), with spike frames only feeding peaks.
        /// </summary>
        private List<int> SelectFramesForHotspots(int first, int last, out HashSet<int> uniformFrames)
        {
            int totalFrames = last - first + 1;
            var selected = new SortedSet<int>();
            uniformFrames = new HashSet<int>();

            // Uniform sampling (taken within the [first,last] range the Profiler actually retains, not relying on sequence alignment). These are "representative frames" and count toward the average.
            int step = Math.Max(1, totalFrames / UniformHotspotFrames);
            for (int f = first; f <= last; f += step) { selected.Add(f); uniformFrames.Add(f); }

            // The slowest few frames. Key: the counter ring buffer and the frame range retained by the Profiler backend start at different points (the latter is usually shorter),
            // so align from the end of the sequence — the latest sample series[L-1] must correspond to the latest frame last, and we derive frame indices backward from there to avoid a start-point mismatch.
            var series = _capturedMainThread;
            string spikeDiag = "";
            if (series != null && series.Count > 0)
            {
                int L = series.Count;
                var order = new List<int>(L);
                for (int i = 0; i < L; i++) order.Add(i);
                order.Sort((a, b) => series[b].CompareTo(series[a])); // Descending by frame time
                for (int k = 0; k < SpikeHotspotFrames && k < order.Count; k++)
                {
                    int i = order[k];
                    int fi = last - (L - 1 - i); // End-aligned: series[L-1]→last
                    if (fi >= first && fi <= last)
                    {
                        selected.Add(fi); // Note: not added to uniformFrames — spike frames only contribute to peaks, never contaminate the average
                        spikeDiag += $" {fi}({series[i] / 1e6:0.0}ms)";
                    }
                }
            }

            if (!string.IsNullOrEmpty(spikeDiag))
                Debug.Log("[PerfLint] " + L.Tr($"Hotspot sampling included the slowest (stutter spike) frames: {spikeDiag.Trim()}", $"热点抽样纳入最慢（卡顿尖刺）帧：{spikeDiag.Trim()}"));

            return new List<int>(selected);
        }

        /// <summary>Culprit identity of a spike frame: deepest USER method on the heaviest path; else deepest PACKAGE method; else "unmapped". Aggregates spike frames by cause.</summary>
        private static string CulpritKey(WorstFrameInfo wf)
        {
            var path = wf?.UserCallPath;
            if (path != null)
            {
                for (int i = path.Count - 1; i >= 0; i--)
                    if (!IsPackagePath(path[i].ScriptPath)) return "U:" + path[i].Marker;
                if (path.Count > 0) return "P:" + path[path.Count - 1].Marker;
            }
            return "unmapped";
        }

        /// <summary>Ranked per-culprit worst-frame list for the result: the live-captured spikes (already one-per-culprit) plus the deferred merge's worst frame, deduped by culprit and sorted by score (desc).</summary>
        private List<WorstFrameInfo> BuildWorstFrames(WorstFrameInfo deferred)
        {
            var byKey = new Dictionary<string, WorstFrameInfo>(_liveSpikesByCulprit);
            if (deferred != null && deferred.HasData)
            {
                string k = CulpritKey(deferred);
                if (!byKey.TryGetValue(k, out var e) || SpikeFrameScore(deferred) > SpikeFrameScore(e))
                    byKey[k] = deferred;
            }
            var list = new List<WorstFrameInfo>(byKey.Values);
            list.Sort((a, b) => SpikeFrameScore(b).CompareTo(SpikeFrameScore(a)));
            return list;
        }

        /// <summary>
        /// Ranking score for choosing the "worst" spike frame to report. Magnitude alone picks the largest frame — but the largest is often an
        /// unmappable artifact (main-thread self dominated by EditorLoop / Deep-Profile overhead, or work in compiler-generated closures with no
        /// resolvable type). Weighting by whether the heaviest path maps to your code (×4) or a third-party package (×3) lets a slightly-smaller but
        /// actionable spike win, so FPS003 can name a real culprit instead of falling back to "couldn't be captured".
        /// </summary>
        private static double SpikeFrameScore(WorstFrameInfo wf)
        {
            if (wf == null) return 0;
            var path = wf.UserCallPath;
            bool anyMapped = path != null && path.Count > 0;
            bool anyUser = false;
            if (path != null)
                foreach (var p in path)
                    if (!IsPackagePath(p.ScriptPath)) { anyUser = true; break; }
            double weight = anyUser ? 4.0 : anyMapped ? 3.0 : 1.0;
            // Rank by the culprit's own inclusive Total (game-relevant), NOT the raw frame self-total — the latter is inflated by EditorLoop/Deep-Profile
            // overhead, which would let a tiny real culprit on a heavy-overhead frame (e.g. a 50ms MLog.Info) outrank a genuine 363ms phase.
            return CulpritMagnitude(wf) * weight;
        }

        /// <summary>The culprit's game-relevant magnitude (ms): deepest USER method's inclusive Total; else deepest PACKAGE method's Total; else the frame self-total. Mirrors RuntimeAnalyzer.SpikeDisplayMs.</summary>
        private static double CulpritMagnitude(WorstFrameInfo wf)
        {
            if (wf == null) return 0;
            var path = wf.UserCallPath;
            if (path != null)
            {
                for (int i = path.Count - 1; i >= 0; i--)
                    if (!IsPackagePath(path[i].ScriptPath)) return path[i].TotalMs > 0 ? path[i].TotalMs : wf.TotalSelfMs;
                if (path.Count > 0) return path[path.Count - 1].TotalMs > 0 ? path[path.Count - 1].TotalMs : wf.TotalSelfMs;
            }
            return wf.TotalSelfMs;
        }

        private static bool IsPackagePath(string p) =>
            !string.IsNullOrEmpty(p) && p.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal);

        private static List<double> ReadSamples(ProfilerRecorder rec)
        {
            var list = new List<double>(rec.Count);
            var buf = new List<ProfilerRecorderSample>(rec.Count);
            rec.CopyTo(buf);
            foreach (var s in buf) list.Add(s.Value);
            return list;
        }

        /// <summary>
        /// Convert the merged marker→accumulated-self-time dictionary into a sorted Top N Hotspot list.
        /// Key: sort + filter noise + truncate to Top N first, then do script mapping only for the N selected entries —
        /// ResolveScriptPath contains AssetDatabase.FindAssets (a project-wide query, a few ms each);
        /// mapping every unique marker (thousands under Deep Profile) would take tens of seconds, whereas mapping only the final Top N reduces it to N calls.
        /// </summary>
        private static List<Hotspot> BuildHotspots(
            Dictionary<string, double> acc, Dictionary<string, PeakPair> peak,
            int uniformFramesSeen, double avgFrameTimeNs)
        {
            // acc comes only from uniform (representative) frames, so the denominator for per-frame average/share must be uniformFramesSeen. With no representative frames there is no way to compute an average.
            if (uniformFramesSeen == 0) return new List<Hotspot>();

            double avgFrameMs = avgFrameTimeNs > 0 ? avgFrameTimeNs / 1_000_000.0 : 0;

            // 1) Sort by self-time descending (without touching AssetDatabase). Markers that appear only in spike frames (load/JIT noise) are not in acc,
            //    so they naturally do not make the list — a denoising side effect of splitting by frame source.
            var ranked = new List<KeyValuePair<string, double>>(acc);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

            // 2) Filter framework noise, truncate to Top N, and do the expensive script mapping only for selected entries.
            var filtered = new List<Hotspot>();
            foreach (var kv in ranked)
            {
                string display = CleanMarkerName(kv.Key);
                if (IsFrameworkNoise(display)) continue;

                double perFrame = kv.Value / uniformFramesSeen;
                double share    = avgFrameMs > 0 ? 100.0 * perFrame / avgFrameMs : 0;
                if (share > 100.0) share = 100.0; // Defensive cap: a single marker's self-time cannot exceed the whole frame (rarely triggered after splitting)
                double peakMs   = peak.TryGetValue(kv.Key, out var pk) ? pk.Reported : perFrame;
                string script   = ResolveScriptPath(kv.Key); // Called only for the ≤12 selected markers
                filtered.Add(new Hotspot(display, perFrame, peakMs, share, script));
                if (filtered.Count >= 12) break;
            }
            return filtered;
        }

        /// <summary>
        /// Traverse the HierarchyFrameDataView (Unity has already merged samples of the same marker on the native side) and accumulate each marker's
        /// self-time. The key benefit: the managed-side traversal volume is the merged item count (thousands), not raw samples (millions) — bringing merging down
        /// from ~12s/20 frames (per-raw-sample native calls + GetSampleName string marshaling) to millisecond scale.
        /// Walk the entire hierarchy tree with an explicit stack, accumulating self-time per marker name across call stacks.
        /// </summary>
        /// <summary>Merge this frame's self-time into the global acc/peak and return this frame's total main-thread self-time (ms, for slowest-frame localization).
        /// After returning, _frameDict still holds this frame's per-marker self (cleared only on the next call), so the caller can snapshot the slowest frame from it.</summary>
        private static double AccumulateHierarchySelfTime(
            HierarchyFrameDataView frame, Dictionary<string, double> acc, Dictionary<string, PeakPair> peak,
            bool contributeToAverage)
        {
            // First aggregate each marker's self-time for "this frame" into _frameDict (via FillFrameDict), then merge into the global:
            // - peak always maintains the highest + second-highest across frames (the second-highest excludes a single extreme frame, see PeakPair) — spike frames are also counted.
            // - acc accumulates only when contributeToAverage (uniform/representative frame) — spike frames are excluded, to avoid load/JIT super-frames contaminating the per-frame average.
            double frameTotal = FillFrameDict(frame);
            foreach (var kv in _frameDict)
            {
                if (contributeToAverage)
                {
                    acc.TryGetValue(kv.Key, out var a);
                    acc[kv.Key] = a + kv.Value;
                }
                peak.TryGetValue(kv.Key, out var p);
                p.Add(kv.Value);
                peak[kv.Key] = p;
            }
            return frameTotal;
        }

        /// <summary>
        /// Walk the whole Hierarchy tree with an explicit stack and aggregate each marker's self-time for THIS frame into the shared _frameDict
        /// (the same marker is scattered across call stacks). Returns the frame's total self-time (ms): since every ms of the frame is "self" to exactly
        /// one node, this sum equals the whole main-thread frame time. Leaves _frameDict populated so the caller can snapshot the frame from it
        /// (shared with the live spike capture, which needs the per-marker self map without touching the merge's acc/peak).
        /// </summary>
        private static double FillFrameDict(HierarchyFrameDataView frame)
        {
            var frameDict = _frameDict; frameDict.Clear();

            int root = frame.GetRootItemID();
            var stack = _itemStack; stack.Clear();
            var childBuf = _childBuf;

            stack.Push(root);
            while (stack.Count > 0)
            {
                int id = stack.Pop();

                double self = frame.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime); // ms
                if (self > 0)
                {
                    string name = frame.GetItemName(id);
                    if (!string.IsNullOrEmpty(name))
                    {
                        frameDict.TryGetValue(name, out var prev);
                        frameDict[name] = prev + self;
                    }
                }

                if (frame.HasItemChildren(id))
                {
                    childBuf.Clear();
                    frame.GetItemChildren(id, childBuf);
                    for (int i = 0; i < childBuf.Count; i++)
                        stack.Push(childBuf[i]);
                }
            }

            double frameTotal = 0;
            foreach (var kv in frameDict) frameTotal += kv.Value;
            return frameTotal;
        }

        /// <summary>
        /// Snapshot the attribution of the slowest frame, with two complementary clues:
        ///  1) self-time heavy hitters (from _frameDict): which low-level functions the time ultimately went into — often engine/third-party library leaves.
        ///  2) the script entry point on the heaviest call path (drilling down the frame tree by Total): "which piece of your code" triggered this spike.
        ///     self-time always lands on leaves; a user script entry point (e.g. a one-time batch pathfinding in level generation) has near-zero self and only a high Total,
        ///     so looking at self alone would attribute the spike to library internals and never point to user code. This step automates the Profiler's "Hierarchy sorted by Total".
        /// totalSelfMs is this frame's total self-time.
        /// </summary>
        // Diagnostic: top-N self-time markers of the current _frameDict WITHOUT the framework-noise filter — reveals whether a spike's main-thread
        // time is actually in wait/GC/engine markers (which SnapshotWorstFrame drops), explaining "no user hotspot" attributions.
        private static string RawTopSelfLeaders(int n)
        {
            var ranked = new List<KeyValuePair<string, double>>(_frameDict);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < ranked.Count && i < n; i++)
                parts.Add($"{CleanMarkerName(ranked[i].Key)} {ranked[i].Value:0.0}ms");
            return string.Join(" · ", parts);
        }

        private static WorstFrameInfo SnapshotWorstFrame(HierarchyFrameDataView frame, double totalSelfMs)
        {
            const int TopN = 4;
            var ranked = new List<KeyValuePair<string, double>>(_frameDict);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

            var top = new List<MarkerCost>(TopN);
            foreach (var kv in ranked)
            {
                string display = CleanMarkerName(kv.Key);
                if (IsFrameworkNoise(display)) continue; // Don't let PlayerLoop/Loading/Mono.JIT etc. take up slots
                top.Add(new MarkerCost(display, kv.Value));
                if (top.Count >= TopN) break;
            }

            var callPath = BuildHeaviestScriptPath(frame, totalSelfMs);
            return new WorstFrameInfo(totalSelfMs, top, callPath);
        }

        // Resolution cache for marker→script path (""=confirmed no mapping). ResolveScriptPath contains AssetDatabase.FindAssets (a few ms each),
        // and the heaviest-path drill-down repeatedly hits the same marker name (multiple frames share the same call chain); the cache resolves each unique name only once.
        private static readonly Dictionary<string, string> _scriptPathCache = new Dictionary<string, string>(256);

        /// <summary>
        /// Drill from root to leaf along the slowest frame's "heaviest child", collecting the methods on this main call path that map to project scripts (in order of appearance, outer→inner).
        /// Path selection and importance are judged by Total (including children) — this is exactly the Profiler Hierarchy's default sort, which surfaces "the user script entry point that triggered the spike".
        /// Only collect nodes with Total ≥ 20% of the frame total (filtering out insignificant side branches), and drop engine/load noise; resolution goes through the cache to control cost.
        /// </summary>
        private static List<CallPathFrame> BuildHeaviestScriptPath(HierarchyFrameDataView frame, double frameTotalMs)
        {
            var path = new List<CallPathFrame>();
            if (frame == null || !frame.valid) return path;

            double threshold = frameTotalMs * 0.10; // Follow the trunk that carries the bulk of this frame — 10% (not 20%) so the drill reaches deeper mappable methods (e.g. A* / user helpers) when a spike's work splits across children instead of dead-ending at an async/BCL node
            var childBuf = new List<int>(64);
            int node = frame.GetRootItemID();
            int guard = 0;

            while (node != -1 && guard++ < 1024)
            {
                // Record this node if it's a heavy, mappable method. Crucially, do NOT break on the CURRENT node's total: the synthetic
                // root (GetRootItemID) and container nodes (PlayerLoop / thread group) commonly report Total == 0, so a "break when
                // total < threshold" here aborted the drill on the very first iteration — which is why attribution never surfaced a
                // script. We descend regardless and only stop when the heaviest CHILD falls below the threshold (the real trunk end).
                string name = frame.GetItemName(node);
                if (!string.IsNullOrEmpty(name))
                {
                    double total = frame.GetItemColumnDataAsFloat(node, HierarchyFrameDataView.columnTotalTime);
                    if (total >= threshold)
                    {
                        string display = CleanMarkerName(name);
                        if (!IsFrameworkNoise(display))
                        {
                            string script = ResolveScriptPathCached(name);
                            if (!string.IsNullOrEmpty(script))
                            {
                                double gc = _gcColumn >= 0 ? frame.GetItemColumnDataAsFloat(node, _gcColumn) : 0; // bytes allocated in this method's subtree
                                path.Add(new CallPathFrame(display, total, script, gc));
                            }
                        }
                    }
                }

                // Drill down to the child with the largest Total; stop once the trunk thins below the threshold.
                if (!frame.HasItemChildren(node)) break;
                childBuf.Clear();
                frame.GetItemChildren(node, childBuf);
                int best = -1; double bestTotal = -1;
                for (int i = 0; i < childBuf.Count; i++)
                {
                    // EditorLoop is the editor's own per-frame overhead (huge under Deep Profile) — never game code. Skipping it as a drill target keeps the
                    // trunk on PlayerLoop (the game); otherwise, on a spike frame whose EditorLoop dwarfs PlayerLoop, the drill dead-ends in editor internals.
                    if (frame.GetItemName(childBuf[i]) == "EditorLoop") continue;
                    double t = frame.GetItemColumnDataAsFloat(childBuf[i], HierarchyFrameDataView.columnTotalTime);
                    if (t > bestTotal) { bestTotal = t; best = childBuf[i]; }
                }
                if (best == -1 || bestTotal < threshold) break;
                node = best;
            }
            return path;
        }

        // Attribute this frame's GC allocation to a user script: drill the heaviest-GC trunk (skip EditorLoop) and record the deepest USER (non-package)
        // method whose subtree GC carries the bulk of the frame's allocation. Called on ALL merged frames (steady + spike) — RUN.GC001 then points to the
        // heaviest per-frame allocator the session actually measured (typically the level-gen methods that allocate MBs), which is what users want to open.
        private const double GcAttributeFloor = 4096; // ignore trivial per-frame allocations when attributing; also the drill's stop threshold
        private static void AccumulateGcByScript(HierarchyFrameDataView frame, Dictionary<string, double> gcByScript, Dictionary<string, string> gcMethodByScript)
        {
            if (_gcColumn < 0) return;
            // Drill by heaviest-GC child (skip EditorLoop), recording the deepest USER (non-package) method with meaningful GC. NOTE: do NOT gate on the
            // root node's GC — the synthetic root (like its Total time) reports 0, which previously made this return immediately and left GC001 unattributed.
            int node = frame.GetRootItemID();
            var childBuf = new List<int>(64);
            int guard = 0;
            string deepScript = null, deepMethod = null; double deepGc = 0;
            while (node != -1 && guard++ < 1024)
            {
                string name = frame.GetItemName(node);
                if (!string.IsNullOrEmpty(name))
                {
                    double gc = frame.GetItemColumnDataAsFloat(node, _gcColumn);
                    if (gc >= GcAttributeFloor)
                    {
                        string display = CleanMarkerName(name);
                        if (!IsFrameworkNoise(display))
                        {
                            string script = ResolveScriptPathCached(name);
                            if (!string.IsNullOrEmpty(script) && !IsPackagePath(script)) { deepScript = script; deepMethod = display; deepGc = gc; }
                        }
                    }
                }
                if (!frame.HasItemChildren(node)) break;
                childBuf.Clear();
                frame.GetItemChildren(node, childBuf);
                int best = -1; double bestGc = -1;
                for (int i = 0; i < childBuf.Count; i++)
                {
                    if (frame.GetItemName(childBuf[i]) == "EditorLoop") continue; // editor/Deep-Profile overhead, not game allocation
                    double g = frame.GetItemColumnDataAsFloat(childBuf[i], _gcColumn);
                    if (g > bestGc) { bestGc = g; best = childBuf[i]; }
                }
                if (best == -1 || bestGc < GcAttributeFloor) break;
                node = best;
            }
            if (deepScript != null)
            {
                // Keep the PEAK single-frame allocation per script (not a sum) — reporting "up to ~X in a frame" is honest whether the allocation is steady or bursty.
                gcByScript.TryGetValue(deepScript, out var prevPeak);
                if (deepGc > prevPeak) { gcByScript[deepScript] = deepGc; gcMethodByScript[deepScript] = deepMethod; }
            }
        }

        /// <summary>Pick the heaviest per-frame GC allocator measured (peak single-frame bytes). Returns null if none reaches a meaningful amount / GC column unavailable.</summary>
        private static GcAllocSite BuildTopGcSite(Dictionary<string, double> gcByScript, Dictionary<string, string> gcMethodByScript)
        {
            if (gcByScript.Count == 0) return null;
            string topScript = null; double topPeak = 0;
            foreach (var kv in gcByScript) if (kv.Value > topPeak) { topPeak = kv.Value; topScript = kv.Key; }
            if (topScript == null || topPeak < 4096) return null; // not a meaningful allocator → GC001 falls back to the static Script GC panel
            gcMethodByScript.TryGetValue(topScript, out var method);
            return new GcAllocSite(topScript, method, topPeak); // BytesPerFrame carries the PEAK single-frame allocation
        }

        private static string ResolveScriptPathCached(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return null;
            if (_scriptPathCache.TryGetValue(marker, out var cached)) return string.IsNullOrEmpty(cached) ? null : cached;
            string resolved = ResolveScriptPath(marker);
            _scriptPathCache[marker] = resolved ?? "";
            return resolved;
        }

        // Engine/framework-level markers: time-consuming but parts the developer cannot change (rendering, physics, player loop skeleton); excluded to focus on actionable items.
        private static bool IsFrameworkNoise(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return true;
            switch (marker)
            {
                case "PlayerLoop":
                case "Profiler.FinalizeAndSendFrame":
                case "GUI.Repaint":
                case "WaitForTargetFPS":
                case "Gfx.WaitForPresentOnGfxThread":
                case "Gfx.PresentFrame":
                case "Semaphore.WaitForSignal":
                case "EditorLoop":
                // Engine load/deserialize/JIT class: one-time costs that appear only during scene load/first frame, not steady-state actionable hotspots.
                // Frame-source splitting already drops most of these automatically (they don't appear in uniform frames); this is a fallback for the occasional one that slips through.
                case "ReadObjectFromSerializedFile":
                case "AwakeFromLoad":
                case "PersistentManager.Remapper":
                    return true;
            }
            // Prefix fallback: Loading.* (scene-load internals), Mono.JIT* (JIT compilation on first call).
            if (marker.StartsWith("Loading.", StringComparison.Ordinal)) return true;
            if (marker.StartsWith("Mono.JIT", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Attempt to map a marker name to a project script. Supports the following common Deep Profile formats:
        ///   With module prefix : "Assembly-CSharp.dll::PerfLintTest.Update()"  → first strip the "&lt;Module&gt;::" prefix
        ///   Plain method       : "PlayerController.Update()"
        ///   With namespace     : "MyGame.PlayerController.Update()"
        ///   Coroutine/async    : "PlayerController+&lt;RunLoop&gt;d__3.MoveNext()"  → take the outer class name before '+'
        ///   Closure/LINQ       : "PlayerController+&lt;&gt;c.&lt;DoWork&gt;b__0()"  → same as above
        /// Returns null when no mapping exists (pure engine/third-party code).
        /// </summary>
        /// <summary>Strip the marker's module prefix ("Assembly-CSharp.dll::Foo" → "Foo") for a clean display name.</summary>
        private static string CleanMarkerName(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return marker;
            int sep = marker.IndexOf("::");
            return sep >= 0 ? marker.Substring(sep + 2) : marker;
        }

        private static string ResolveScriptPath(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return null;

            // Deep Profile method markers often carry a module prefix, e.g. "Assembly-CSharp.dll::PerfLintTest.Update()".
            // The module name itself contains '.' (.dll), which would pollute the subsequent class-name extraction by '.' — the "<Module>::" prefix must be stripped first.
            int moduleSep = marker.IndexOf("::");
            if (moduleSep >= 0) marker = marker.Substring(moduleSep + 2);

            int paren = marker.IndexOf('(');
            string head = paren >= 0 ? marker.Substring(0, paren) : marker;
            int dot = head.LastIndexOf('.');
            if (dot <= 0) return null;

            string ownerPath = head.Substring(0, dot);   // "Namespace.Class" / "Outer+<Inner>d__N"
            int dot2 = ownerPath.LastIndexOf('.');
            string typeName = dot2 >= 0 ? ownerPath.Substring(dot2 + 1) : ownerPath;

            // Deep Profile generates nested types for coroutines/async/closures, with markers like "Outer+<Method>d__N.MoveNext()".
            // What follows '+' is a compiler-synthesized name; taking the outer class name before '+' is enough to locate the user script file.
            int plus = typeName.IndexOf('+');
            if (plus > 0)
                typeName = typeName.Substring(0, plus);

            if (string.IsNullOrEmpty(typeName) || !IsIdentifier(typeName)) return null;

            var guids = AssetDatabase.FindAssets($"{typeName} t:MonoScript");
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.EndsWith(".cs")) continue;
                if (path.Replace('\\', '/').Contains("/Editor/")) continue;
                // The file name must match the type name, to avoid FindAssets' fuzzy matches.
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (string.Equals(file, typeName, StringComparison.Ordinal))
                    return path;
            }
            return null;
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_') return false;
            for (int i = 1; i < s.Length; i++)
                if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
            return true;
        }

        private void DisposeChannels()
        {
            foreach (var c in _channels)
                if (c.Recorder.Valid) c.Recorder.Dispose();
            _channels.Clear();
        }

        public void Dispose()
        {
            EditorApplication.update -= EditorFrameTick; // Defensive: the callback must also be removed when Dispose is called directly without Stop
            DisposeChannels();
            if (_running)
            {
                ProfilerDriver.enabled = _prevProfilerEnabled;
                _running = false;
            }
        }
    }
}
