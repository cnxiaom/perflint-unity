using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace PerfLint.Core
{
    /// <summary>
    /// Defense-in-depth memory watchdog for scans. Wired into <see cref="ScanContext.ReportProgress"/> by
    /// <see cref="ScanRunner"/>, so it samples memory once per asset across EVERY scanner — current and future,
    /// with no per-scanner cooperation required. When Unity's graphics-driver or total reserved memory crosses a
    /// danger threshold it forces a synchronous sweep (<c>EditorUtility.UnloadUnusedAssetsImmediate</c>) and logs a
    /// warning naming the scanner that drove it.
    ///
    /// Why this exists on top of <c>ScannerUtil.ThrottleReclaim</c>: reading a Texture2D/Material/Mesh property with
    /// <c>AssetDatabase.LoadAssetAtPath</c> materializes the full engine object, which uploads its pixel/vertex data to
    /// the GPU as a side effect. In a synchronous, non-yielding scan those uploads never get reclaimed between frames,
    /// so on a very large project VRAM climbs until the driver can't allocate and the editor hard-crashes (observed
    /// 2026-07-05 on an ~12k-texture project). The per-scanner throttle keeps normal runs two orders of magnitude below
    /// the threshold here; this watchdog only fires when something slips through — e.g. a newly added load-heavy scanner
    /// that forgot to throttle — turning a would-be crash into a logged, self-healing sweep.
    ///
    /// Safety note: the sweep can unload any asset with no live engine reference, so scanners MUST report progress at a
    /// point where no per-iteration loaded asset needs to survive (all current scanners call ReportProgress at the top of
    /// their loop, before loading). Firing only near a crisis, an occasional invalidated reference (guarded by the null
    /// checks every scanner already has) is strictly preferable to the editor crashing.
    /// </summary>
    public sealed class ScanMemoryWatchdog
    {
        // After a sweep, wait at least this many samples (≈ assets) before sweeping again, so hovering near the line
        // doesn't trigger an expensive sweep on every single asset.
        internal const int SweepCooldownTicks = 128;

        private readonly long _gfxThresholdBytes;
        private readonly long _reservedThresholdBytes;
        private int _ticksSinceSweep = SweepCooldownTicks; // allow the first over-threshold sample to fire immediately
        private int _sweeps;
        private string _lastScanner;

        /// <summary>How many times the watchdog has forced a sweep this scan (0 on a healthy run).</summary>
        public int SweepCount => _sweeps;
        /// <summary>The scanner running when the most recent sweep fired (null if none).</summary>
        public string LastSweptScanner => _lastScanner;

        /// <param name="gfxThresholdBytes">Graphics-memory danger line; ≤0 auto-derives from reported VRAM.</param>
        /// <param name="reservedThresholdBytes">Total-reserved danger line; ≤0 auto-derives from system memory.</param>
        public ScanMemoryWatchdog(long gfxThresholdBytes = 0, long reservedThresholdBytes = 0)
        {
            _gfxThresholdBytes = gfxThresholdBytes > 0 ? gfxThresholdBytes : DefaultGfxThreshold();
            _reservedThresholdBytes = reservedThresholdBytes > 0 ? reservedThresholdBytes : DefaultReservedThreshold();
        }

        // 40% of reported VRAM, floored at 1 GB. Normal scans peak ~113 MB of graphics memory, so this leaves a wide
        // margin below the driver's real ceiling while still catching a runaway long before it OOMs.
        private static long DefaultGfxThreshold()
        {
            long vram = (long)SystemInfo.graphicsMemorySize * 1024L * 1024L;
            return Math.Max(1024L * 1024L * 1024L, (long)(vram * 0.4));
        }

        // 70% of system memory, floored at 8 GB. Native/managed reserved growth is not what crashed the editor (VRAM was),
        // but a runaway CPU-side accumulation gets the same safety net. The high floor avoids false sweeps on normal runs.
        private static long DefaultReservedThreshold()
        {
            long sys = (long)SystemInfo.systemMemorySize * 1024L * 1024L;
            return Math.Max(8L * 1024L * 1024L * 1024L, (long)(sys * 0.7));
        }

        /// <summary>
        /// Pure sweep decision — unit-testable without a live editor. Sweep when either memory reading is at/over its
        /// threshold AND the post-sweep cooldown has elapsed.
        /// </summary>
        public static bool ShouldSweep(
            long gfxBytes, long gfxThreshold,
            long reservedBytes, long reservedThreshold,
            int ticksSinceSweep, int cooldownTicks)
            => ticksSinceSweep >= cooldownTicks
               && (gfxBytes >= gfxThreshold || reservedBytes >= reservedThreshold);

        /// <summary>Sample memory for the current scanner and force a reclaim if we're in the danger zone. Called once per asset.</summary>
        public void Tick(string scannerName)
        {
            _ticksSinceSweep++;
            long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long reserved = Profiler.GetTotalReservedMemoryLong();
            if (!ShouldSweep(gfx, _gfxThresholdBytes, reserved, _reservedThresholdBytes, _ticksSinceSweep, SweepCooldownTicks))
                return;

            EditorUtility.UnloadUnusedAssetsImmediate();
            _ticksSinceSweep = 0;
            _sweeps++;
            _lastScanner = scannerName;
            long gfxAfter = Profiler.GetAllocatedMemoryForGraphicsDriver();
            Debug.LogWarning(
                $"[PerfLint] Memory watchdog swept during '{scannerName}': graphics {gfx / 1048576}MB→{gfxAfter / 1048576}MB, " +
                $"reserved {reserved / 1048576}MB (thresholds gfx={_gfxThresholdBytes / 1048576}MB / reserved={_reservedThresholdBytes / 1048576}MB). " +
                "Repeated sweeps mean a scanner is loading assets without periodic reclaim — add ScannerUtil.ThrottleReclaim there.");
        }
    }
}
