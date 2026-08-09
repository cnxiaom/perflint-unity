using System.Collections.Generic;
using UnityEditor;

namespace PerfLint.Core
{
    /// <summary>
    /// Listens to asset imports and registers **any asset a file-level scanner can incrementally re-scan** — scripts the
    /// user edited/deleted/moved AND now textures/audio/materials/etc. — into <see cref="PerfLintPendingRescan"/>, so the
    /// report stays live after a manual edit instead of showing stale findings (e.g. changing a texture's compression, or
    /// commenting out a Debug.Log, without a full ~150s rescan).
    ///
    /// Two consumers drain the queue (see <see cref="PerfLintAutoRescan"/>): after a script edit's domain reload the window
    /// consumes on rebuild; after an asset edit (no domain reload) the debounced auto-pump brings the baseline up to date —
    /// updating the open window's live result, or the on-disk baseline when no window is open.
    ///
    /// Which paths qualify is asked of the discovered file scanners' path-based <see cref="IFileScanner.Handles"/>
    /// (<see cref="ScanRunner.IsFileScannable"/>), so this automatically covers every scanner that opts into incremental
    /// re-scan — no per-type list to maintain here. Only records when a persisted baseline already exists (nothing to
    /// update otherwise). Oversized batches fall back to a "whole baseline expired" marker inside
    /// <see cref="PerfLintPendingRescan"/> to avoid serially rescanning hundreds of files.
    /// </summary>
    internal sealed class PerfLintChangeTracker : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            // Recorded BEFORE the early-return below: whether a scan baseline exists has nothing to do with whether a
            // measurement has gone stale, and a benchmark comparison needs to know the project moved underneath it
            // even in a project that has never been scanned.
            RecordForMeasurementStaleness(importedAssets, deletedAssets, movedAssets);

            // No saved report → no baseline to incrementally update; recording would be pointless.
            if (!ScanResultStore.Exists()) return;

            var changed = new List<string>();
            void Consider(string[] paths)
            {
                if (paths == null) return;
                foreach (var p in paths)
                    if (!string.IsNullOrEmpty(p) && ScanRunner.IsFileScannable(p)) changed.Add(p);
            }

            Consider(importedAssets);     // newly created / re-imported after modification
            Consider(deletedAssets);      // deleted: RescanFile cannot read the file → clears its old findings
            Consider(movedAssets);        // new path after move
            Consider(movedFromAssetPaths); // old path before move → clears findings under the old path

            if (changed.Count > 0)
            {
                PerfLintPendingRescan.Record(changed);
                // Asset edits don't trigger a domain reload, so the window's on-reload consume won't fire — nudge the
                // debounced auto-pump to catch the baseline up on the next editor tick.
                PerfLintAutoRescan.Notify();
            }
        }

        /// <summary>
        /// Notes in <see cref="ProjectEditJournal"/> that the project changed, so a runtime measurement taken before
        /// this point can be shown as describing a project that no longer exists.
        ///
        /// Counts only real asset paths: .meta files ride along with almost every import and would inflate every
        /// number by roughly 2×, which turns "3 things changed" into a figure the user can see is wrong.
        /// </summary>
        private static void RecordForMeasurementStaleness(string[] imported, string[] deleted, string[] moved)
        {
            // Not while the editor is in (or entering) Play Mode. Entering and leaving Play Mode makes Unity
            // re-import things of its own accord, and counting that churn as user edits would have a measurement
            // declare itself out of date the moment it was taken — the one situation where it is certainly current.
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;

            int assets = 0, packages = 0;
            Count(imported, ref assets, ref packages);
            Count(deleted, ref assets, ref packages);
            Count(moved, ref assets, ref packages);

            // RecordAssetChanges declines while PerfLint is applying its own fixes — those re-imports are already
            // recorded, by name, as the fix that caused them.
            if (assets > 0) ProjectEditJournal.RecordAssetChanges(assets);
            if (packages > 0) ProjectEditJournal.RecordPackageChanges(packages);
        }

        /// <summary>
        /// Splits changed paths into the user's own content and packages.
        ///
        /// The split matters because a package that updates itself is not something the user did: reporting it as
        /// "22 other file changes" beside their own edits made "you changed nothing" read as false. .meta files are
        /// skipped — they ride along with nearly every import and would roughly double every count.
        /// </summary>
        private static void Count(string[] paths, ref int assets, ref int packages)
        {
            if (paths == null) return;
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (p.StartsWith("Packages/", System.StringComparison.Ordinal)) packages++;
                else assets++;
            }
        }
    }
}
