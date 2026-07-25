using System;

namespace PerfLint.Core
{
    /// <summary>
    /// Lets code outside the UI (the Pipeline command surface driven by an agent over the CLI/MCP wire) read the
    /// OPEN window's LIVE scan result — the one that still carries <see cref="Finding.Fix"/> instances — instead of
    /// the Fix-less on-disk baseline (<see cref="ScanResultStore"/>) or paying for a fresh full scan.
    ///
    /// Mirrors the hand-off pattern of <see cref="PerfLintAutoRescan.WindowRefresh"/>: the window publishes its
    /// accessor while open (OnEnable) and clears it on close (OnDisable), so a null <see cref="Provider"/>
    /// unambiguously means "no window is open — fall back to a scan".
    ///
    /// Why this is load-bearing for the optimize commands: <see cref="OptimizePlan.Build"/> classifies the AUTO
    /// (safe-waste) tier by <see cref="Finding.CanAutoFix"/> (Fix != null). A result restored from disk has no Fix
    /// instances, so every finding would collapse into the manual tier and the optimize commands would find nothing
    /// to apply. The live window result keeps the Fix instances → correct tiers, and it is instant (no scan) in the
    /// common case: the majority of users run the agent against an editor they already have open.
    /// </summary>
    public static class PerfLintLiveResult
    {
        /// <summary>Set by the open window to return its live <see cref="ScanResult"/> (with Fix instances); null when no window is open.</summary>
        public static Func<ScanResult> Provider;

        /// <summary>The open window's live result, or null when no window is open (caller should scan instead).</summary>
        public static ScanResult Current => Provider?.Invoke();
    }
}
