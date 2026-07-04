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

        public bool IsScript => !string.IsNullOrEmpty(ScriptPath);

        /// <summary>Peak is significantly higher than average (≥2×) → occasional spike rather than a sustained hotspot.</summary>
        public bool IsSpiky => PeakMsPerFrame >= SelfMsPerFrame * 2;

        public Hotspot(string marker, double selfMsPerFrame, double peakMsPerFrame, double sharePercent, string scriptPath)
        {
            Marker = marker;
            SelfMsPerFrame = selfMsPerFrame;
            PeakMsPerFrame = peakMsPerFrame;
            SharePercent = sharePercent;
            ScriptPath = scriptPath;
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
        public GcAllocSite(string scriptPath, string method, double bytesPerFrame)
        {
            ScriptPath = scriptPath; Method = method; BytesPerFrame = bytesPerFrame;
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
            IReadOnlyDictionary<string, MetricStats> categoryCounters = null)
        {
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
            MetricStats gpuOverride = null, GcAllocSite topGcSite = null) =>
            new RuntimeProfileResult(
                DurationSeconds, FrameCount, FrameTimeNs, GcPerFrameBytes, TotalMemoryBytes,
                TotalReservedBytes, GcUsedBytes, GfxUsedBytes,
                DrawCalls, SetPassCalls, Batches, Triangles, Vertices,
                (gpuOverride != null && gpuOverride.HasData) ? gpuOverride : GpuFrameTimeNs,
                hotspots, hotspotsAvailable, WasDeepProfile, SceneBatching, worstFrames, topGcSite, CategoryCounters);

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
