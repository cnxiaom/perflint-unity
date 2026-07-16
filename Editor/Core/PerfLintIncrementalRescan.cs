namespace PerfLint.Core
{
    /// <summary>
    /// The shared "consume pending changed files → fold each file's fresh findings into a baseline" step.
    /// Used by BOTH the open window (keeps its live, Fix-instance-carrying result) and the background auto-pump
    /// (updates the persisted baseline when no window is open) — one implementation so the two never diverge.
    ///
    /// Each pending path is re-scanned via <see cref="ScanRunner.RescanFile"/>, which re-runs the file-level scanners
    /// that claim it and replaces only that file's findings (a deleted file's scanners produce nothing, clearing its
    /// stale findings). Project-wide verdicts (duplicate content, unreferenced graph, Addressables dupes) are NOT
    /// touched — import-setting edits (the common case) don't affect them; content/structure changes that do are
    /// covered by the Cap→stale fallback in <see cref="PerfLintPendingRescan"/>.
    /// </summary>
    public static class PerfLintIncrementalRescan
    {
        /// <summary>
        /// Consumes the pending-rescan queue and folds each changed file's fresh findings into <paramref name="baseline"/>,
        /// returning the updated result. <paramref name="changed"/> is set when at least one file was actually re-scanned.
        /// Returns the baseline unchanged (and changed=false) when nothing is pending or baseline is null.
        /// </summary>
        public static ScanResult Apply(ScanResult baseline, out bool changed)
        {
            changed = false;
            if (baseline == null) return null;

            var files = PerfLintPendingRescan.Consume();
            if (files.Length == 0) return baseline;

            var result = baseline;
            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file)) continue;
                var updated = ScanRunner.RescanFile(file, result);
                // RescanFile returns the SAME instance when no file-level scanner claims the path (nothing to do), and a
                // NEW merged result when it re-scanned — reference inequality is the "actually changed" signal.
                if (updated != null && !ReferenceEquals(updated, result))
                {
                    result = updated;
                    changed = true;
                }
            }
            return result;
        }
    }
}
