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
    }
}
