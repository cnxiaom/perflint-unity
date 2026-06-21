using System.Collections.Generic;
using UnityEditor;

namespace PerfLint.Core
{
    /// <summary>
    /// Listens to asset imports and registers **scripts that the user has manually edited/deleted/moved**
    /// into <see cref="PerfLintPendingRescan"/>, so that after a domain reload the window performs an
    /// incremental rescan of only those files — otherwise, after manually editing code, the report keeps
    /// showing stale findings (e.g. commenting out a line does not make its warning disappear).
    ///
    /// This is the counterpart of the AI-fix incremental refresh (where the verifier registers changed files)
    /// for the "non-AI, pure manual edit" scenario: save script → recompile →
    /// domain reload → window rebuilds and consumes this list → RescanFile makes it real-time.
    ///
    /// Only watches <c>.cs</c> files (only script-based rules do file-level incremental updates in this project:
    /// DebugLog / Migration / Script GC-Roslyn); and only records when a persisted baseline already exists
    /// (no point recording when there is no report to update). If the batch is too large,
    /// <see cref="PerfLintPendingRescan"/> automatically falls back to a "whole baseline expired" marker
    /// to avoid serially rescanning hundreds of files on domain reload.
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
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".cs")) changed.Add(p);
            }

            Consider(importedAssets);     // newly created / re-imported after modification
            Consider(deletedAssets);      // deleted: RescanFile cannot read the file → clears its old findings
            Consider(movedAssets);        // new path after move
            Consider(movedFromAssetPaths); // old path before move → clears findings under the old path

            if (changed.Count > 0) PerfLintPendingRescan.Record(changed);
        }
    }
}
