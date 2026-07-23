using System;
using PerfLint.Core;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Ci
{
    /// <summary>
    /// Shared automation operations, reused by the batchmode <c>-executeMethod</c> entry points
    /// (<see cref="PerfLintCli"/>) and the optional Pipeline <c>[CliCommand]</c> surface. Public so the
    /// separate <c>PerfLint.Editor.Pipeline</c> assembly can call in — one source of truth for
    /// scan-with-progress and the safe deterministic fix batch.
    /// </summary>
    public static class PerfLintOps
    {
        /// <summary>Run a full scan, forwarding scanner progress to <paramref name="onProgress"/> (scanner name, 0..1). Does not persist — the caller saves if it wants to.</summary>
        public static ScanResult Scan(Action<string, float> onProgress = null)
        {
            var ctx = new ScanContext(reportProgress: onProgress ?? ((_, __) => { }));
            return ScanRunner.Run(ctx);
        }

        /// <summary>
        /// Apply every deterministic <see cref="IFix"/> fix in the plan in one batch — the same set the
        /// editor's "Fix All" applies (undoable import/project settings, no domain reload). Never touches
        /// trade-off actions or AI fixes. Reports (done, total) per fix. Returns (applied, failed).
        /// </summary>
        public static (int applied, int failed) ApplyFixes(FixPlan plan, Action<int, int> onProgress = null)
        {
            int applied = 0, failed = 0;
            var list = plan.AutoFixable;
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        var r = list[i].Fix.Apply();
                        if (r.Success) applied++;
                        else { failed++; Debug.LogWarning("[PerfLint] fix failed " + list[i].RuleId + ": " + r.Message); }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Debug.LogWarning("[PerfLint] fix threw " + list[i].RuleId + ": " + e.Message);
                    }
                    onProgress?.Invoke(i + 1, list.Count);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            return (applied, failed);
        }

        /// <summary>Realized memory savings = before-potential minus after-potential (clamped ≥ 0) — the honest before/after re-scan delta.</summary>
        public static long SavedMemoryBytes(ScanResult before, ScanResult after) => Delta(before, after, false);

        /// <summary>Realized build-size savings, same before/after re-scan delta.</summary>
        public static long SavedBuildBytes(ScanResult before, ScanResult after) => Delta(before, after, true);

        static long Delta(ScanResult before, ScanResult after, bool build)
        {
            var b = SavingsSummary.Compute(before.Findings);
            var a = SavingsSummary.Compute(after.Findings);
            long d = build ? b.BuildBytes - a.BuildBytes : b.MemoryBytes - a.MemoryBytes;
            return d > 0 ? d : 0;
        }
    }
}
