using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
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
            => ApplyFixList(plan.AutoFixable, onProgress);

        /// <summary>
        /// Apply a specific list of auto-fixable findings in one batch — the shared core behind <see cref="ApplyFixes"/>
        /// and the goal-targeted optimize command (which passes a dimension-filtered subset). Every finding must carry a
        /// live <see cref="Finding.Fix"/> instance (a result restored from disk has none). Same Start/StopAssetEditing
        /// batching and Undo behaviour as the editor's "Fix All". Returns (applied, failed).
        /// </summary>
        /// <summary>
        /// Gives back a list whose fixes can actually run, re-scanning the affected rules for any finding whose Fix
        /// delegate did not survive the trip to disk.
        ///
        /// A restored finding remembers that it WAS fixable and cannot carry the delegate that does it, which is why
        /// FindingActions.ApplyRule re-scans before applying. The batch path had no such step, so it could only ever
        /// be handed findings from a scan still in memory. One re-scan for the whole batch, over the distinct rules
        /// present, rather than one per finding.
        /// </summary>
        static IReadOnlyList<Finding> Revive(IReadOnlyList<Finding> list)
        {
            bool anyDead = false;
            foreach (var f in list) if (f != null && f.Fix == null && f.WasAutoFixable) { anyDead = true; break; }
            if (!anyDead) return list;

            var rules = new List<string>();
            foreach (var f in list)
                if (f != null && !string.IsNullOrEmpty(f.RuleId) && !rules.Contains(f.RuleId)) rules.Add(f.RuleId);

            ScanResult live;
            try { live = ScanRunner.RescanRules(rules, ScanResultStore.Load()?.Result); }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr($"Could not re-check before applying: {e.Message}",
                                                      $"应用前重新检查失败：{e.Message}"));
                return list;
            }
            if (live == null) return list;

            // Matched on rule + target, because a rule can carry many findings and each fix belongs to one asset.
            var revived = new List<Finding>();
            foreach (var f in list)
            {
                if (f == null) continue;
                if (f.Fix != null) { revived.Add(f); continue; }
                foreach (var c in live.Findings)
                    if (c.Fix != null
                        && string.Equals(c.RuleId, f.RuleId, StringComparison.Ordinal)
                        && string.Equals(c.TargetPath, f.TargetPath, StringComparison.Ordinal))
                    { revived.Add(c); break; }
            }
            return revived;
        }

        public static (int applied, int failed) ApplyFixList(IReadOnlyList<Finding> autoFixable, Action<int, int> onProgress = null)
        {
            int applied = 0, failed = 0;
            var list = Revive(autoFixable ?? Array.Empty<Finding>());
            if (list.Count == 0) return (0, 0);
            // Per rule, so a before/after measurement can later name what it is measuring the effect of.
            var appliedByRule = new Dictionary<string, int>(StringComparer.Ordinal);
            // The re-imports below are ours. Without this they are also filed as the user's own unnamed edits —
            // the same act counted twice, and "was anything done here besides our fixes?" answered yes by our fixes.
            // The scope covers StopAssetEditing/Refresh deliberately: batched imports all land there, not at Apply().
            using (ProjectEditJournal.SuppressUserEdits())
            {
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        var r = list[i].Fix.Apply();
                        if (r.Success)
                        {
                            applied++;
                            string rid = list[i].RuleId ?? "";
                            appliedByRule[rid] = (appliedByRule.TryGetValue(rid, out int n) ? n : 0) + 1;
                        }
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
            }
            foreach (var kv in appliedByRule) ProjectEditJournal.RecordFix(kv.Key, kv.Value);
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
