using System;
using System.Collections.Generic;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Statistical summary of a single runtime counter (aggregated over a sampling window).
    /// Values retain their raw units (nanoseconds / bytes / counts); formatting is delegated to the UI — the result itself stays pure data.
    /// </summary>
    public sealed class MetricStats
    {
        public string Key { get; }     // Counter name, e.g. "Main Thread"
        public int SampleCount { get; }
        public double Avg { get; }
        public double Min { get; }
        public double Max { get; }
        public double P95 { get; }
        public double Median { get; }  // P50 — the robust "sustained" value; a one-off freeze inflates Avg/P95 but barely moves the median
        public double First { get; }   // Value at the first frame of the window (used for trend/leak detection)
        public double Last { get; }    // Value at the last frame of the window
        public double FirstHalfAvg { get; }  // Average of the first half
        public double SecondHalfAvg { get; } // Average of the second half

        public bool HasData => SampleCount > 0;

        /// <summary>
        /// Robust net trend delta: SecondHalfAvg − FirstHalfAvg. More resilient to single-frame spikes and endpoint noise than Last−First;
        /// used for trend detection such as memory leaks. Positive = growing, negative = shrinking.
        /// </summary>
        public double TrendDelta => SecondHalfAvg - FirstHalfAvg;

        public MetricStats(string key, IReadOnlyList<double> samples)
        {
            Key = key;
            if (samples == null || samples.Count == 0)
            {
                SampleCount = 0;
                return;
            }

            SampleCount = samples.Count;
            First = samples[0];
            Last = samples[samples.Count - 1];

            double sum = 0, min = double.MaxValue, max = double.MinValue;
            foreach (var v in samples)
            {
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            Avg = sum / samples.Count;
            Min = min;
            Max = max;

            // First/second half averages (robust trend). Sample order is ProfilerRecorder's old→new (CopyTo semantics).
            int half = samples.Count / 2;
            if (half > 0)
            {
                double firstSum = 0, secondSum = 0;
                for (int i = 0; i < half; i++) firstSum += samples[i];
                for (int i = samples.Count - half; i < samples.Count; i++) secondSum += samples[i];
                FirstHalfAvg = firstSum / half;
                SecondHalfAvg = secondSum / half;
            }
            else
            {
                FirstHalfAvg = First;
                SecondHalfAvg = Last;
            }

            // p95: sort a copy (sample count is in the thousands, overhead is negligible).
            var sorted = new List<double>(samples);
            sorted.Sort();
            int idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
            idx = Math.Min(Math.Max(idx, 0), sorted.Count - 1);
            P95 = sorted[idx];
            Median = sorted[sorted.Count / 2];
        }
    }

    /// <summary>
    /// A CPU hotspot: main-thread self time aggregated by marker name.
    /// If the marker name can be mapped to a project script, ScriptPath is non-empty — this is the anchor for "pinpointing specific code".
    /// </summary>
    public sealed class Hotspot
    {
        public string Marker { get; }
        public double SelfMsPerFrame { get; }   // Average per-frame self time over the sampling window (ms)
        public double PeakMsPerFrame { get; }   // Second-highest single-frame self time (ms, with the one extreme frame excluded) — distinguishes "consistently slow" from "occasional spike"
        public double SharePercent { get; }     // Fraction of main-thread frame time
        public string ScriptPath { get; }       // May be null/empty: the .cs asset path this marker maps to

        /// <summary>
        /// How many of the representative (uniformly sampled) frames this marker did main-thread work in.
        /// Together with <see cref="SampledFrames"/> this is the **sampled-frame hit rate** — the reason a hotspot conclusion
        /// is allowed to be stated at all: "the same user code path occupies the main thread in the great majority of sampled
        /// frames" is a claim about the code path, not about this machine's frame rate, so it survives machine drift (which
        /// contaminates whole-frame time by ±13%) essentially untouched. 0 when the sampler did not track it.
        /// </summary>
        public int HitFrames { get; }

        /// <summary>
        /// The denominator: representative frames actually examined during the merge. Ships with the rate on purpose —
        /// a hit rate is only as strong as the number of frames behind it, and the merge budget makes that number vary.
        /// </summary>
        public int SampledFrames { get; }

        public bool IsScript => !string.IsNullOrEmpty(ScriptPath);

        /// <summary>Peak is significantly higher than average (≥2×) → occasional spike rather than a sustained hotspot.</summary>
        public bool IsSpiky => PeakMsPerFrame >= SelfMsPerFrame * 2;

        /// <summary>False when the sampler didn't track presence (legacy/degraded paths) — callers must then say nothing about persistence.</summary>
        public bool HasHitRate => SampledFrames > 0;

        /// <summary>Share of representative frames this marker appeared in (0–100). Quote it with <see cref="HitFrames"/>/<see cref="SampledFrames"/>, never alone.</summary>
        public double HitRatePercent => SampledFrames > 0 ? 100.0 * HitFrames / SampledFrames : 0;

        // 95% Wilson score interval. The point estimate alone would let 1-of-1 read as "100% of frames" — the bound is what
        // keeps a small denominator from being spent as confidence. 8/8 → lower bound 68%; 24/24 → 86%; 1/1 → 21%.
        private const double WilsonZ = 1.96;

        /// <summary>Lower bound of the 95% confidence interval on the hit rate (0–100). This, not the raw rate, is what may be treated as confidence.</summary>
        public double HitRateLowerBoundPercent => WilsonBound(HitFrames, SampledFrames, -1);

        /// <summary>Upper bound of the 95% confidence interval on the hit rate (0–100).</summary>
        public double HitRateUpperBoundPercent => WilsonBound(HitFrames, SampledFrames, +1);

        /// <summary>Confident (95%) that the marker runs in more than 60% of frames → a sustained per-frame cost, safe to describe as "every frame".</summary>
        public const double SustainedLowerBoundPercent = 60.0;
        /// <summary>Confident (95%) that the marker runs in fewer than half the frames → periodic/occasional, not a fixed per-frame cost.</summary>
        public const double IntermittentUpperBoundPercent = 50.0;

        public bool IsSustained    => HasHitRate && HitRateLowerBoundPercent >= SustainedLowerBoundPercent;
        public bool IsIntermittent => HasHitRate && HitRateUpperBoundPercent < IntermittentUpperBoundPercent;

        /// <summary>Wilson score bound for hits/samples, sign −1 = lower, +1 = upper. Returns a percentage clamped to [0,100]; 0 when there is no denominator.</summary>
        public static double WilsonBound(int hits, int samples, int sign)
        {
            if (samples <= 0) return 0;
            if (hits < 0) hits = 0;
            if (hits > samples) hits = samples;

            double n = samples;
            double p = hits / n;
            double z2 = WilsonZ * WilsonZ;
            double denom = 1.0 + z2 / n;
            double center = (p + z2 / (2 * n)) / denom;
            double margin = (WilsonZ / denom) * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n));
            double v = center + sign * margin;
            return Math.Max(0.0, Math.Min(1.0, v)) * 100.0;
        }

        public Hotspot(string marker, double selfMsPerFrame, double peakMsPerFrame, double sharePercent, string scriptPath,
            int hitFrames = 0, int sampledFrames = 0)
        {
            Marker = marker;
            SelfMsPerFrame = selfMsPerFrame;
            PeakMsPerFrame = peakMsPerFrame;
            SharePercent = sharePercent;
            ScriptPath = scriptPath;
            HitFrames = hitFrames;
            SampledFrames = sampledFrames;
        }
    }

    /// <summary>A marker and its self time in the single slowest frame (used by RUN.FPS003 to attribute single-frame spikes).</summary>
    public readonly struct MarkerCost
    {
        public readonly string Marker;
        public readonly double SelfMs;
        public MarkerCost(string marker, double selfMs) { Marker = marker; SelfMs = selfMs; }
    }

    /// <summary>
    /// A script-mapped method on the "heaviest call path" of the single slowest frame, together with its **Total (inclusive of children)** cost.
    /// Self time lands only at the leaves (typically inside the engine or third-party libraries), whereas the user-script entry point that actually *triggered* the spike is only visible via Total —
    /// which is exactly why Unity Profiler Hierarchy sorts by Total by default. This chain is collected by drilling down along the "heaviest child" to attribute the spike to user code.
    /// </summary>
    public readonly struct CallPathFrame
    {
        public readonly string Marker;     // Clean display name (module prefix already stripped)
        public readonly double TotalMs;    // Total cost including children (ms)
        public readonly string ScriptPath; // The .cs this maps to (may be a user script or a third-party package script)
        public readonly double GcBytes;    // GC allocated within this node's subtree (bytes) — attributes a spike's allocations to the method; 0 if unavailable
        public CallPathFrame(string marker, double totalMs, string scriptPath, double gcBytes = 0)
        {
            Marker = marker; TotalMs = totalMs; ScriptPath = scriptPath; GcBytes = gcBytes;
        }
    }

    /// <summary>
    /// Attribution snapshot of the single slowest frame during the sampling period: total main-thread self time for that frame + the top markers by share.
    /// **Independent** of the steady-state hotspot list (Hotspots, derived from uniform frames) — dedicated to locating the root cause of a one-off stutter spike (RUN.FPS003);
    /// not included in averages/percentages, so it will not resurrect the loading noise that was filtered out by frame-source splitting (0.13.8).
    /// </summary>
    public sealed class WorstFrameInfo
    {
        public double TotalSelfMs { get; }                  // Total main-thread self time for that frame (ms, ≈ freeze duration for that frame)
        public IReadOnlyList<MarkerCost> TopMarkers { get; } // Top markers in descending self-time order (engine/loading noise already filtered)

        /// <summary>
        /// Chain of script-mapped methods on that frame's "heaviest call path" (outer→inner, ordered by Total). Used to attribute the spike to the **user-script entry point** —
        /// the self-time leaves (TopMarkers) only tell you which low-level function consumed the time; this chain tells you "which part of your code triggered it".
        /// May include third-party package scripts (e.g. UniTask/A* methods); callers can distinguish user code from libraries via the Packages/ path prefix.
        /// </summary>
        public IReadOnlyList<CallPathFrame> UserCallPath { get; }

        public bool HasData => TopMarkers != null && TopMarkers.Count > 0;

        public WorstFrameInfo(double totalSelfMs, IReadOnlyList<MarkerCost> topMarkers,
            IReadOnlyList<CallPathFrame> userCallPath = null)
        {
            TotalSelfMs = totalSelfMs;
            TopMarkers = topMarkers ?? Array.Empty<MarkerCost>();
            UserCallPath = userCallPath ?? Array.Empty<CallPathFrame>();
        }
    }

    /// <summary>The runtime script that allocated the most managed memory per steady-state frame — attributes RUN.GC001 to a real runtime function (e.g. its Locate target), instead of the static "Script GC" panel. May be null.</summary>
    public sealed class GcAllocSite
    {
        public string ScriptPath { get; }
        public string Method { get; }
        public double BytesPerFrame { get; }

        /// <summary>
        /// 1-based line the allocation was recorded at, or 0 when unknown.
        ///
        /// Non-zero only for callstack attribution: a Deep Profile marker names a method and nothing finer, whereas a
        /// stack frame carries the exact line. Locate uses it when present and falls back to finding the method
        /// declaration when not.
        /// </summary>
        public int Line { get; }

        /// <summary>
        /// The allocation is inside a package rather than the user's own scripts.
        ///
        /// Recorded rather than filtered. Package allocations used to be dropped during attribution, on the reasoning
        /// that we only report what the user can change — but dropping the only answer produces silence, not
        /// restraint: urp3dsample's entire per-frame GC is one Visual Effect Graph binder, and GC001 responded with
        /// "this sample couldn't pin it to a method" and a suggestion to enable Deep Profile, which could not have
        /// helped either because the filter runs after resolution. "Your allocation is in this package method" is
        /// actionable — the binder can be removed, replaced, or accepted — and it is the truth.
        /// </summary>
        public bool IsPackage { get; }

        /// <summary>
        /// What ALL the allocation samples in this session added up to, per frame — the denominator for how much of
        /// the measured allocation this attribution explains. 0 when unknown (marker-name attribution, or a restored
        /// session).
        ///
        /// Carried because the two figures a GC finding shows come from different instruments and can disagree by an
        /// order of magnitude. The headline is the "GC Allocated In Frame" counter; the attribution is built from
        /// GC.Alloc samples in the frame hierarchy. Measured on urp3dsample: counter 96 KB/frame, samples in those
        /// same frames 2.8 KB — 2.9% coverage. Printing "heaviest allocator X (up to ~2.2 KB)" under a 55 KB headline
        /// with no ratio invites the reader to take 3% for the cause, which is the kind of confident wrongness this
        /// project treats as worse than saying nothing.
        ///
        /// Why the remaining allocation carries no sample is UNRESOLVED. EditorLoop's subtree holds no GC.Alloc
        /// samples either, so "it is the editor's" is unproven; and the editor records no profiler frames at all
        /// outside Play Mode, so there is no idle baseline to compare against. Until that is settled the honest move
        /// is to state the coverage, not to explain it.
        /// </summary>
        public double SampledBytesPerFrame { get; }

        public GcAllocSite(string scriptPath, string method, double bytesPerFrame, int line = 0, bool isPackage = false,
            double sampledBytesPerFrame = 0)
        {
            ScriptPath = scriptPath; Method = method; BytesPerFrame = bytesPerFrame;
            Line = line; IsPackage = isPackage; SampledBytesPerFrame = sampledBytesPerFrame;
        }
    }

    /// <summary>
    /// Complete result of a single runtime sampling session. RuntimeAnalyzer uses this to produce RUN.* findings.
    /// </summary>
    public sealed class RuntimeProfileResult
    {
        public double DurationSeconds { get; }
        public int FrameCount { get; }

        // Counter statistics (any may be null/no-data — when the platform or Unity version does not support that counter).
        public MetricStats FrameTimeNs { get; }     // Main-thread frame time, nanoseconds
        public MetricStats GcPerFrameBytes { get; } // Per-frame GC allocation, bytes
        public MetricStats TotalMemoryBytes { get; }
        public MetricStats TotalReservedBytes { get; } // Total reserved memory (used for fragmentation assessment)
        public MetricStats GcUsedBytes { get; }        // Managed heap in use (C#-side leak indicator)
        public MetricStats GfxUsedBytes { get; }       // Graphics resources in use (texture/RT/mesh VRAM side)
        public MetricStats DrawCalls { get; }
        public MetricStats SetPassCalls { get; }
        public MetricStats Batches { get; }
        public MetricStats Triangles { get; }
        public MetricStats Vertices { get; }

        public IReadOnlyList<Hotspot> Hotspots { get; }

        /// <summary>Whether hotspot collection succeeded (RawFrameDataView may be unavailable on certain Unity versions/platforms; on failure it degrades to empty).</summary>
        public bool HotspotsAvailable { get; }

        /// <summary>Whether Unity Profiler's Deep Profile was enabled during the sampling session. Affects the HOT003 hint text.</summary>
        public bool WasDeepProfile { get; }

        /// <summary>GPU frame time, nanoseconds. HasData == false when the platform does not support GPU counters.</summary>
        public MetricStats GpuFrameTimeNs { get; }

        /// <summary>Batching snapshot of the active scene at sampling time (material topology / runtime instantiation). Used for root-cause analysis of batching issues.</summary>
        public SceneBatchingSnapshot SceneBatching { get; }

        /// <summary>
        /// Attribution for the worst spike frames — ONE per distinct culprit (script+method), ranked by cost. A level-generation freeze is a cluster of
        /// heavy frames across several phases (PlaceObstaclesAsync / AllVehiclesHavePaths / …), not a single frame; RUN.FPS003 emits one finding per entry.
        /// May be null/empty (computed asynchronously alongside Hotspots).
        /// </summary>
        public IReadOnlyList<WorstFrameInfo> WorstFrames { get; }

        /// <summary>The single worst spike frame (highest-ranked culprit), or null. Convenience over WorstFrames[0].</summary>
        public WorstFrameInfo WorstFrame => WorstFrames != null && WorstFrames.Count > 0 ? WorstFrames[0] : null;

        /// <summary>Top steady-state per-frame GC allocator (runtime attribution for RUN.GC001), or null when none dominant / GC column unavailable.</summary>
        public GcAllocSite TopGcSite { get; }

        /// <summary>
        /// Per-frame allocation inside PlayerLoop — the GAME's allocation, as opposed to
        /// <see cref="GcPerFrameBytes"/>, which is the whole editor process. Null when the merge could not read it
        /// (no hotspot merge, or an older session).
        ///
        /// The two are not variations on one number. Measured inside a single Play Mode session on urp3dsample: the
        /// counter read 55 KB, 308 KB and 456 KB per frame at different moments — running an eval moved it eightfold
        /// — while PlayerLoop's subtree held a flat 2778 B the whole time, and that subtree total equalled the sum of
        /// the GC.Alloc leaves beneath it. So the counter is dominated by whatever the editor is doing, and a finding
        /// or a before/after comparison built on it is measuring the wrong process.
        ///
        /// A lower bound rather than the truth — but a narrow one, and the gap has been measured rather than assumed.
        /// The GC column is a SUBTREE total, so an allocation needs no marker of its own, only some marker above it
        /// inside PlayerLoop, which every Update/LateUpdate/coroutine has; PlayerLoop's column equalled the sum of
        /// the GC.Alloc leaves beneath it exactly (2778 = 2778). And scanning all 32 thread views for top-level GC
        /// columns — not merely for the leaves, which depend on callstack recording — found allocation on the main
        /// thread only. That matches how Unity works: Job System and Burst code uses unmanaged containers and does
        /// not allocate managed memory at all.
        ///
        /// What genuinely stays invisible is managed allocation on a Task or Thread the user started themselves.
        /// </summary>
        public MetricStats GameGcPerFrameBytes { get; }

        /// <summary>
        /// Main-thread nanoseconds spent inside PlayerLoop — the GAME's work — where <see cref="FrameTimeNs"/> is the
        /// whole editor frame. Null when the merge could not read it.
        ///
        /// Measured on urp3dsample, twice, from clean windows: 8.42 ms total = 5.41 game + 2.75 editor; 8.95 = 5.82 +
        /// 2.88. **About a third of a measured frame is the editor drawing its own windows**, and nothing on screen
        /// said so.
        ///
        /// Reported, not judged on. Unlike the GC pair — where the counter was dominated by allocation with no
        /// relation to the game and swapping was clearly right — the frame-time counter is a real measurement of a
        /// real frame; the editor's share of it genuinely happens, it just would not happen on a device. And
        /// PlayerLoop's total is not a drop-in replacement: it is main-thread work inside the player loop, not a
        /// frame's wall clock (VSync waits and render-thread sync sit outside it). Moving the FPS verdicts onto it
        /// would change every threshold and invalidate every baseline on an unverified equivalence.
        /// </summary>
        public MetricStats GameFrameTimeNs { get; }

        /// <summary>Per-object-category counters over the sampling window (e.g. "GameObject Count", "Texture Memory") — used by RUN.MEM003 to name which category of objects/assets grew (leak-suspect: not destroyed). May be null; individual entries may have no data on unsupported platforms.</summary>
        public IReadOnlyDictionary<string, MetricStats> CategoryCounters { get; }

        public RuntimeProfileResult(
            double durationSeconds,
            int frameCount,
            MetricStats frameTimeNs,
            MetricStats gcPerFrameBytes,
            MetricStats totalMemoryBytes,
            MetricStats totalReservedBytes,
            MetricStats gcUsedBytes,
            MetricStats gfxUsedBytes,
            MetricStats drawCalls,
            MetricStats setPassCalls,
            MetricStats batches,
            MetricStats triangles,
            MetricStats vertices,
            MetricStats gpuFrameTimeNs,
            IReadOnlyList<Hotspot> hotspots,
            bool hotspotsAvailable,
            bool wasDeepProfile = false,
            SceneBatchingSnapshot sceneBatching = null,
            IReadOnlyList<WorstFrameInfo> worstFrames = null,
            GcAllocSite topGcSite = null,
            IReadOnlyDictionary<string, MetricStats> categoryCounters = null,
            MetricStats gameGcPerFrameBytes = null,
            MetricStats gameFrameTimeNs = null)
        {
            GameGcPerFrameBytes = gameGcPerFrameBytes;
            GameFrameTimeNs = gameFrameTimeNs;
            DurationSeconds = durationSeconds;
            FrameCount = frameCount;
            FrameTimeNs = frameTimeNs;
            GcPerFrameBytes = gcPerFrameBytes;
            TotalMemoryBytes = totalMemoryBytes;
            TotalReservedBytes = totalReservedBytes;
            GcUsedBytes = gcUsedBytes;
            GfxUsedBytes = gfxUsedBytes;
            DrawCalls = drawCalls;
            SetPassCalls = setPassCalls;
            Batches = batches;
            Triangles = triangles;
            Vertices = vertices;
            GpuFrameTimeNs = gpuFrameTimeNs;
            Hotspots = hotspots ?? Array.Empty<Hotspot>();
            HotspotsAvailable = hotspotsAvailable;
            WasDeepProfile = wasDeepProfile;
            SceneBatching = sceneBatching ?? SceneBatchingSnapshot.Empty;
            WorstFrames = worstFrames;
            TopGcSite = topGcSite;
            CategoryCounters = categoryCounters;
        }

        /// <summary>
        /// Produces a new result object with the asynchronously merged hotspot list and worst-frame attribution (all other fields unchanged).
        /// When gpuOverride is non-null and has data it replaces GpuFrameTimeNs — the GPU time read from frame data during the merge phase
        /// (same source as the Profiler "GPU ms" column) is more reliable than what ProfilerRecorder/FrameTimingManager captured during sampling, so it takes priority.
        /// </summary>
        public RuntimeProfileResult WithHotspots(
            IReadOnlyList<Hotspot> hotspots, bool hotspotsAvailable, IReadOnlyList<WorstFrameInfo> worstFrames = null,
            MetricStats gpuOverride = null, GcAllocSite topGcSite = null, MetricStats gameGcPerFrame = null,
            MetricStats gameFrameTime = null) =>
            new RuntimeProfileResult(
                DurationSeconds, FrameCount, FrameTimeNs, GcPerFrameBytes, TotalMemoryBytes,
                TotalReservedBytes, GcUsedBytes, GfxUsedBytes,
                DrawCalls, SetPassCalls, Batches, Triangles, Vertices,
                (gpuOverride != null && gpuOverride.HasData) ? gpuOverride : GpuFrameTimeNs,
                hotspots, hotspotsAvailable, WasDeepProfile, SceneBatching, worstFrames, topGcSite, CategoryCounters,
                // The merge is where this can be read at all, so a merge that produced one wins; otherwise keep what
                // this result already had rather than dropping it.
                gameGcPerFrame ?? GameGcPerFrameBytes,
                gameFrameTime ?? GameFrameTimeNs);

        /// <summary>Convenience overload: a single worst frame → a one-item list. Used by tests and simple callers.</summary>
        public RuntimeProfileResult WithHotspots(
            IReadOnlyList<Hotspot> hotspots, bool hotspotsAvailable, WorstFrameInfo worstFrame,
            MetricStats gpuOverride = null) =>
            WithHotspots(hotspots, hotspotsAvailable, worstFrame != null ? new[] { worstFrame } : null, gpuOverride);

        /// <summary>Average FPS derived from main-thread frame time; returns 0 when no data is available.</summary>
        public double AverageFps =>
            FrameTimeNs != null && FrameTimeNs.HasData && FrameTimeNs.Avg > 0
                ? 1_000_000_000.0 / FrameTimeNs.Avg
                : 0;
    }
}
