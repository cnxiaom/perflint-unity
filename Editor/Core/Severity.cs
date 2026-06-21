namespace PerfLint.Core
{
    /// <summary>
    /// Issue severity. Used for sorting, health-score deduction weights, and UI color coding.
    /// </summary>
    public enum Severity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    /// <summary>
    /// Diagnostic domain. Each IScanner belongs to one domain; the UI groups findings by domain.
    /// </summary>
    public enum Domain
    {
        Performance,
        Assets,
        Migration,
        ProjectSettings,

        /// <summary>
        /// Runtime (Play Mode) performance-sampling diagnostics. Unlike the other "static scan" domains,
        /// this domain does not go through ScanRunner / IScanner; instead, RuntimeSampler collects
        /// Profiler data during Play Mode and RuntimeAnalyzer produces the Findings.
        /// </summary>
        Runtime
    }
}
