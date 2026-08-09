using System.Collections.Generic;
using System.Linq;
using PerfLint.L10n;

namespace PerfLint.Core
{
    /// <summary>Which savings dimension a one-click optimize run targets. Maps 1:1 to the two Finding estimate fields.</summary>
    public enum SavingsDimension
    {
        Memory,
        Build
    }

    /// <summary>
    /// The "one-click optimize" plan for one savings dimension, split into two tiers:
    ///
    ///   · Auto tier — findings with a deterministic <see cref="Finding.Fix"/> (import-settings changes, Edit&gt;Undo-able,
    ///     the same population Fix All batches today). Safe to run as one click; the plan dialog is the single confirmation.
    ///   · Decision tier — findings whose payoff needs an <see cref="Finding.Action"/> (config changes / destructive ops:
    ///     disabling Static Batching trades draw calls for memory, dedup merge deletes files…). These are NEVER run
    ///     silently: each group is an opt-in checkbox with a plain-language caution, and execution goes through the
    ///     action's normal confirmation flow so the existing consent wording is not bypassed.
    ///
    /// Report-only findings (no Fix, no Action) are excluded — a plan lists only what a click can actually do
    /// (the "executable state audit" rule: never hand the user a button that can't work).
    /// Pure logic, unit-testable; the window supplies findings and dispatches execution.
    /// </summary>
    public sealed class OptimizePlan
    {
        /// <summary>One decision-tier row: all of a rule's actionable findings, executed together as that rule's batch.</summary>
        public sealed class DecisionGroup
        {
            public string RuleId;
            public List<Finding> Findings;
            public long SavingsBytes;
            /// <summary>Button/row label — the action's own label (already user-facing and localized).</summary>
            public string Label;
            /// <summary>One-line plain-language trade-off warning; empty when the rule has no special caution.</summary>
            public string Caution;
            /// <summary>True when this action cannot be undone via Edit &gt; Undo (see <see cref="OptimizePlan.IsIrreversible"/>).
            /// Advisory — surfaced so an agent can warn honestly; execution is editor-only for every decision item regardless.</summary>
            public bool Irreversible;

            /// <summary>
            /// True when NONE of this group's findings still carries a live <see cref="Finding.Action"/> — the scan came
            /// back from disk and the delegates did not survive the trip. The group is still real (the waste is still
            /// there and <see cref="Finding.WasActionable"/> records that a click could reach it), but it cannot be run
            /// as-is: every executor bails on <c>Action == null</c>. The caller MUST re-scan the rule to revive the
            /// delegates before dispatching, exactly as the auto tier relies on ApplyFixList reviving Fix delegates.
            /// Ignoring this flag produces a checkbox that runs and does nothing at all.
            /// </summary>
            public bool NeedsRevive;
        }

        /// <summary>
        /// One manual-tier row: a rule's REPORT-ONLY findings that carry a savings estimate. Not executable, but the
        /// dialog must show them — the panel's scene figure counts them, and hiding them made "29.8 MB next to the
        /// button, 0.27 MB inside the dialog" (real user-reported gap). The row explains where the rest lives.
        /// </summary>
        public sealed class ManualGroup
        {
            public string RuleId;
            /// <summary>Rule-level display title (GroupTitleOrTitle of the findings).</summary>
            public string Title;
            public int Count;
            public long SavingsBytes;
        }

        public SavingsDimension Dimension;
        public List<Finding> AutoItems = new List<Finding>();
        public long AutoSavingsBytes;
        public List<DecisionGroup> DecisionGroups = new List<DecisionGroup>();
        public long DecisionSavingsBytes;
        public List<ManualGroup> ManualGroups = new List<ManualGroup>();
        public long ManualSavingsBytes;

        /// <summary>
        /// The firm, one-click-deliverable part (auto + non-ceiling decision items). This is the figure the panel
        /// shows next to the button as "(~X one-click)" — it must match what running the whole plan can tally.
        /// </summary>
        public long FirmActionableSavingsBytes;

        public long TotalSavingsBytes => AutoSavingsBytes + DecisionSavingsBytes;
        /// <summary>Executable emptiness — manual-only findings don't summon the optimize button (nothing to run).</summary>
        public bool IsEmpty => AutoItems.Count == 0 && DecisionGroups.Count == 0;

        public static long SavingsOf(Finding f, SavingsDimension d) =>
            d == SavingsDimension.Memory ? f.EstimatedMemorySavingsBytes : f.EstimatedBuildSavingsBytes;

        /// <summary>
        /// Builds the plan. For the MEMORY dimension pass the open scene(s)' dependency set as
        /// <paramref name="memorySceneScope"/>: memory is a per-moment quantity, so the plan only offers work the
        /// user can PERCEIVE in a build of the scene they're looking at (product rule 2026-07-17) — findings whose
        /// target asset the open scenes actually load, plus pathless findings (scene-derived by construction, e.g.
        /// the static-batching bill, or already scene-scoped estimates like the streaming ceiling). Pass null for
        /// no scoping — the BUILD dimension always does (a build ships every scene, project-wide is the honest unit).
        /// </summary>
        public static OptimizePlan Build(IReadOnlyList<Finding> findings, SavingsDimension dimension, ISet<string> memorySceneScope = null)
        {
            var plan = new OptimizePlan { Dimension = dimension };
            if (findings == null) return plan;

            bool sceneScoped = dimension == SavingsDimension.Memory && memorySceneScope != null;
            var decisionByRule = new Dictionary<string, DecisionGroup>();
            var manualByRule = new Dictionary<string, ManualGroup>();
            foreach (var f in findings)
            {
                if (f == null) continue;
                long s = SavingsOf(f, dimension);
                if (s <= 0) continue;
                if (sceneScoped && !string.IsNullOrEmpty(f.TargetPath) && !memorySceneScope.Contains(f.TargetPath))
                    continue; // asset not loaded by the open scene(s) → nothing perceivable to offer here

                // WasAutoFixable counts. A Fix delegate cannot survive the trip to disk, so every finding in a
                // restored scan reads as not-fixable -- and this plan is built from the restored scan whenever the
                // panel has not been rescanned since the last domain reload. Measured on urp3dsample: 24 findings
                // worth 195.6 MB of one-click savings, all filed under "manual", the Optimize button gated off, and
                // the Pipeline command answering an agent that there was nothing to optimise. ApplyFixList revives
                // the delegates before applying, the same way FindingActions.ApplyRule does for a single rule.
                if (f.CanAutoFix || f.WasAutoFixable)
                {
                    plan.AutoItems.Add(f);
                    plan.AutoSavingsBytes += s;
                    if (!f.SavingsAreCeiling) plan.FirmActionableSavingsBytes += s;
                }
                // WasActionable counts, for the same reason WasAutoFixable does one branch up: an Action delegate is no
                // more serializable than a Fix delegate, so every finding in a restored scan reads as not-actionable.
                // Only the auto tier was taught this, and the asymmetry had teeth — ASSET.AADUP001 (Addressables
                // duplicate packing, the paid headline feature) fell straight through to the manual tier the moment the
                // report came back from disk, which is the state the panel is in after any domain reload. Observed on a
                // real project: 366 duplicate-packed assets filed under "no one-click available", the Autopilot's build
                // plan reporting them as manual work, and the optimize button gated off entirely on any project whose
                // build-size waste is Action-shaped only.
                else if (f.HasAction || f.WasActionable)
                {
                    if (!decisionByRule.TryGetValue(f.RuleId, out var g))
                    {
                        g = new DecisionGroup
                        {
                            RuleId = f.RuleId,
                            // The action's own label is user-facing and localized, but it lives on the delegate that
                            // just died on disk. The rule's group title is the same thing the main panel's row header
                            // shows, so a revived-later group is still named something the reader recognises.
                            Label = f.HasAction ? f.Action.Label : f.GroupTitleOrTitle,
                            Findings = new List<Finding>(),
                            Caution = CautionFor(f.RuleId),
                            Irreversible = IsIrreversible(f.RuleId),
                            NeedsRevive = true   // cleared below by the first finding that still has a live Action
                        };
                        decisionByRule[f.RuleId] = g;
                    }
                    if (f.HasAction)
                    {
                        // A live delegate anywhere in the group makes it runnable, and its label beats the fallback.
                        g.NeedsRevive = false;
                        g.Label = f.Action.Label;
                    }
                    g.Findings.Add(f);
                    g.SavingsBytes += s;
                    plan.DecisionSavingsBytes += s;
                    if (!f.SavingsAreCeiling) plan.FirmActionableSavingsBytes += s;
                }
                else
                {
                    // Report-only: never executable, but VISIBLE — the dialog's manual tier accounts for the gap
                    // between the panel's scene figure and what the buttons can deliver.
                    if (!manualByRule.TryGetValue(f.RuleId, out var m))
                    {
                        m = new ManualGroup { RuleId = f.RuleId, Title = f.GroupTitleOrTitle };
                        manualByRule[f.RuleId] = m;
                    }
                    m.Count++;
                    m.SavingsBytes += s;
                    plan.ManualSavingsBytes += s;
                }
            }

            plan.DecisionGroups = decisionByRule.Values
                .OrderByDescending(g => g.SavingsBytes)
                .ThenBy(g => g.RuleId, System.StringComparer.Ordinal)
                .ToList();
            plan.ManualGroups = manualByRule.Values
                .OrderByDescending(m => m.SavingsBytes)
                .ThenBy(m => m.RuleId, System.StringComparer.Ordinal)
                .ToList();
            return plan;
        }

        /// <summary>
        /// What running a <see cref="DecisionGroup.NeedsRevive"/> group does BEFORE its own confirmation appears, said
        /// per rule because the rules do wildly different things and one sentence for all of them was wrong.
        ///
        /// Shipped as exactly that mistake for one commit: every revivable group was labelled "Addressables may ask
        /// you to save modified scenes", including ASSET.DUP001, whose rescan is DuplicateAssetScanner re-hashing the
        /// project and never touches Addressables. Tim caught it in a screenshot. Same failure the cross-reference
        /// rule exists to prevent — a sentence about one capability printed next to another.
        /// </summary>
        public static string RescanNoteFor(string ruleId)
        {
            // The Addressables family runs Unity's own analysis, which REFUSES to start while any scene is dirty:
            // it puts up a modal "Modified Scenes must be saved to continue" and blocks the editor until answered.
            // Measured on a real project holding the main thread for six minutes.
            if (!string.IsNullOrEmpty(ruleId) && ruleId.StartsWith("ASSET.AA", System.StringComparison.Ordinal))
                return L.Tr("Re-scans this rule first — Addressables' analysis will not run while scenes are unsaved and asks you to save them.",
                            "执行前会先重扫该规则——Addressables 的分析不允许存在未保存的场景，会先要求你保存。");

            if (ruleId == "ASSET.DUP001")
                return L.Tr("Re-scans this rule first, which re-hashes every asset in the project — on a large project that is not quick.",
                            "执行前会先重扫该规则——它要对全工程资产重新哈希，大工程上不会很快。");

            return L.Tr("Re-scans this rule first, to restore the action itself.",
                        "执行前会先重扫该规则，以恢复该操作本身。");
        }

        /// <summary>
        /// Plain-language trade-off line per decision-tier rule. Looked up at build time (NOT a static readonly table —
        /// L.Tr in a type initializer freezes the language for the session, the known MigrationScanner ApiRules pitfall).
        /// Unknown rules fall back to empty: the action's own confirm dialog still carries its full warning.
        /// </summary>
        internal static string CautionFor(string ruleId)
        {
            switch (ruleId)
            {
                case "PERF.SBATCH001":
                    return L.Tr("Trades draw calls for memory — frame time may rise. Verify in the Profiler afterwards.",
                                "用 Draw Call 换内存，帧时间可能上升——执行后请用 Profiler 验证。");
                case "PERF.TEXSTR001":
                    return L.Tr("Modifies Quality Settings and reimports the textures. Check visuals afterwards.",
                                "会修改 QualitySettings 并重导入相关纹理，开启后请检查画质。");
                case "ASSET.DUP001":
                    return L.Tr("Deletes redundant copies and redirects every reference — not undoable. Commit to version control first.",
                                "删除多余副本并重定向全部引用，不可撤销——请先提交版本控制。");
                case "ASSET.AADUP001":
                    return L.Tr("Only adds Addressable marks (low risk). Revert via Tools > PerfLint.",
                                "仅添加 Addressable 标记（低风险），可经 Tools > PerfLint 回退。");
                default:
                    return "";
            }
        }

        /// <summary>
        /// Decision-tier rules whose action cannot be undone via Edit &gt; Undo (deletes files / redirects references
        /// project-wide). ADVISORY only — it lets a caller warn honestly ("not undoable"). It does NOT gate execution:
        /// no decision-tier action is ever run over the CLI/MCP wire regardless (the agent surface applies only the
        /// auto/waste tier; every trade-off, reversible or not, is left for the user to run in the editor where its
        /// own confirmation dialog carries the full warning). Single source of truth, unit-tested.
        /// </summary>
        /// <summary>Deletes files / cannot be undone via Edit &gt; Undo. ASSET.DUP001 (duplicate merge) is the one such rule today. Public so any surface offering to run it can warn honestly and refuse to do it inline.</summary>
        public static bool IsIrreversible(string ruleId) => ruleId == "ASSET.DUP001";
    }
}
