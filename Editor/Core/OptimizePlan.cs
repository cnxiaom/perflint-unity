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

                if (f.CanAutoFix)
                {
                    plan.AutoItems.Add(f);
                    plan.AutoSavingsBytes += s;
                    if (!f.SavingsAreCeiling) plan.FirmActionableSavingsBytes += s;
                }
                else if (f.HasAction)
                {
                    if (!decisionByRule.TryGetValue(f.RuleId, out var g))
                    {
                        g = new DecisionGroup
                        {
                            RuleId = f.RuleId,
                            Findings = new List<Finding>(),
                            Label = f.Action.Label,
                            Caution = CautionFor(f.RuleId)
                        };
                        decisionByRule[f.RuleId] = g;
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
    }
}
