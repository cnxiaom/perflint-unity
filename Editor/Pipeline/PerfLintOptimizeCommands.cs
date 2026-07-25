using System;
using System.Collections.Generic;
using System.Linq;
using PerfLint.Ci;
using PerfLint.Core;
using PerfLint.Licensing;
using PerfLint.Scanners;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace PerfLint.Ci.Pipeline
{
    /// <summary>
    /// Goal-targeted "one-click optimize" exposed as Unity Pipeline CLI commands, so an agent talking to your OPEN
    /// editor (Claude Code / Cursor over <c>unity mcp</c>) can optimize by goal in plain language.
    /// <code>
    ///   unity command perflint_optimize_plan  --goal build     # or memory — read-only, free
    ///   unity command perflint_optimize_apply --goal build     # applies the safe waste tier; Pro
    ///   unity command perflint_optimize_apply --goal build --dry_run
    /// </code>
    ///
    /// DESIGN (open-editor first — the majority of users run the agent against an editor they have open, see
    /// docs/conversational-optimize-ux.md): the wire applies ONLY the auto/waste tier — deterministic import-setting
    /// fixes, undoable via Edit &gt; Undo, the exact set the editor's "Fix All" applies. EVERY trade-off (Static
    /// Batching, texture streaming, Addressables extraction) AND every irreversible op (duplicate merge) is surfaced in
    /// the plan but is NEVER executed over the wire — the user runs those in the editor, where each action's own
    /// confirmation carries the full warning. "Waste is automatable; trade-offs are yours" → trade-offs are yours to
    /// run in your editor.
    ///
    /// The apply modifies the OPEN project through the same code path as "Fix All"; an open PerfLint window re-syncs
    /// itself automatically (PerfLintChangeTracker → PerfLintAutoRescan), so this layer stays UI-decoupled.
    /// For headless CI (no editor) use <see cref="PerfLintCli.RunFix"/> instead.
    /// </summary>
    public static class PerfLintOptimizeCommands
    {
        [CliCommand("perflint_optimize_plan",
            "Plan a goal-targeted optimization (build size or memory): the safe, reversible waste this can auto-apply, plus the trade-offs and manual items to do in the editor. Local, free, read-only; uploads no code or assets.")]
        public static OptimizePlanDto PerfLintOptimizePlan(
            [CliArg("goal", "What to optimize: 'build' (build size, project-wide) or 'memory' (runtime memory, scoped to the open scene). Matched loosely; default 'build'.")] string goal = "build")
        {
            var dim = ParseDimension(goal);
            var scan = LiveOrScan();
            var scope = dim == SavingsDimension.Memory ? OpenSceneDependencies() : null;
            var plan = OptimizePlan.Build(scan.Findings, dim, scope);
            return OptimizePlanDto.From(plan, dim, MemoryScopeNote(dim, plan, scan), scan.AutoFixableCount);
        }

        [CliCommand("perflint_optimize_apply",
            "Apply ONLY the safe, reversible waste fixes for a goal (build size or memory) — the auto tier, undoable via Edit > Undo — then re-scan and report the verified delta. Trade-offs and irreversible ops are never applied here; the plan lists them to do in the editor. Pro. Modifies the open project.")]
        public static OptimizeApplyDto PerfLintOptimizeApply(
            [CliArg("goal", "What to optimize: 'build' or 'memory'. Matched loosely; default 'build'.")] string goal = "build",
            [CliArg("dry_run", "Report what would be applied without changing anything.")] bool dryRun = false)
        {
            var dim = ParseDimension(goal);
            var before = LiveOrScan();
            var scope = dim == SavingsDimension.Memory ? OpenSceneDependencies() : null;
            var plan = OptimizePlan.Build(before.Findings, dim, scope);

            OptimizeApplyDto result;
            if (dryRun)
                result = OptimizeApplyDto.DryRun(before, plan, dim);
            else if (plan.AutoItems.Count == 0)
                result = OptimizeApplyDto.Nothing(before, plan, dim);
            else if (!LicenseService.IsPro)
                result = OptimizeApplyDto.ProRequired(before, plan, dim);
            else
            {
                var (applied, failed) = PerfLintOps.ApplyFixList(plan.AutoItems);
                var after = PerfLintOps.Scan();
                ScanResultStore.Save(after); // keep the baseline live for the next call / a closed window
                result = OptimizeApplyDto.Applied(before, after, plan, dim, applied, failed);
            }

            // Memory is scene-scoped: an empty/untitled scene makes the auto tier read 0 while the project may have
            // MBs reclaimable elsewhere. Tell the agent the project-wide figure + how to act on it, or it relays a
            // false "nothing to optimize for memory" (mirrors the window's scope banner). Build is project-wide — no note.
            var note = MemoryScopeNote(dim, plan, before);
            if (!string.IsNullOrEmpty(note)) result.message += " " + note;
            return result;
        }

        // ── Helpers ────────────────────────────────────────────────

        /// <summary>Loose natural mapping so the agent can pass "memory" / "cut memory" / "build size" etc. Defaults to build.</summary>
        internal static SavingsDimension ParseDimension(string goal)
        {
            if (!string.IsNullOrEmpty(goal))
            {
                var g = goal.Trim().ToLowerInvariant();
                if (g.Contains("mem") || g.Contains("ram")) return SavingsDimension.Memory;
            }
            return SavingsDimension.Build;
        }

        internal static string DimName(SavingsDimension d) => d == SavingsDimension.Memory ? "memory" : "build";

        /// <summary>
        /// The "this goal is empty, but the project isn't" pointer, appended wherever a dimension's auto tier
        /// doesn't cover everything one click could fix.
        ///
        /// A goal's auto tier can be legitimately empty while the project has hundreds of one-click fixes: BUILD
        /// is structurally empty (build savings come from dedup <see cref="FindingAction"/>s, not <see cref="IFix"/>es,
        /// and the wire only applies IFix), and MEMORY is scoped to the open scene. Saying "nothing to apply"
        /// without naming the command that DOES reach them makes an agent either stop ("nothing to fix") or call
        /// perflint_optimize_apply and get nothing back — observed live 2026-07-24, where an agent relayed
        /// "perflint_optimize_apply handles the auto tier" to the user about 204 fixes it cannot reach.
        /// This is CLAUDE.md's cross-reference rule: never point at a capability that won't fire.
        ///
        /// <paramref name="coveredHere"/> is a subset of <paramref name="projectAutoFixable"/> by construction
        /// (AutoItems = CanAutoFix ∧ savings in this dimension), so empty output means this plan covers everything.
        /// </summary>
        internal static string FixPointer(int projectAutoFixable, int coveredHere) =>
            projectAutoFixable > coveredHere
                ? $" The project has {projectAutoFixable} one-click-fixable finding(s) in total, of which this goal's "
                  + $"auto tier covers {coveredHere}; the rest are applied with perflint_fix (narrowable with "
                  + "--rule_id / --domain / --min_severity), not with perflint_optimize_apply."
                : "";

        /// <summary>
        /// The scan to plan/apply against. Prefers the OPEN window's live result — it carries the Fix instances
        /// OptimizePlan needs to classify the auto tier, and it's instant. Falls back to a fresh scan when no window
        /// is open (the on-disk baseline can't be used: restored findings have no Fix instances → nothing to apply).
        /// </summary>
        static ScanResult LiveOrScan()
        {
            var live = PerfLintLiveResult.Current;
            if (live != null) return live;
            var r = PerfLintOps.Scan();
            ScanResultStore.Save(r);
            return r;
        }

        /// <summary>
        /// The asset dependency set of the currently open scene(s), for the memory dimension's scene scope (memory is a
        /// per-moment quantity — only offer work a build of the open scene can show). Mirrors the window's
        /// GetOpenSceneDependencies; recomputed per call (no cache needed — one call per command).
        /// </summary>
        static HashSet<string> OpenSceneDependencies()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded && !string.IsNullOrEmpty(sc.path))
                    foreach (var d in AssetDatabase.GetDependencies(sc.path, true))
                        set.Add(d);
            }
            return set;
        }

        /// <summary>
        /// The memory dimension is scene-scoped (only assets a build of the open scene loads — memory is a per-moment
        /// quantity). An empty or unsaved ("untitled") scene therefore yields a ~0 plan even when the project has MBs of
        /// reclaimable memory in other scenes. This note gives the agent the project-wide figure + the "open your heaviest
        /// scene" guidance the window shows in its scope banner, so it never relays a false "no memory to optimize".
        /// Returns null for the build dimension (project-wide by nature — no scoping caveat).
        /// </summary>
        static string MemoryScopeNote(SavingsDimension dim, OptimizePlan plan, ScanResult scan)
        {
            if (dim != SavingsDimension.Memory) return null;
            long projectWide = SavingsSummary.Compute(scan.Findings).MemoryBytes;
            string note = $"Note: memory is scoped to the open scene(s): {OpenSceneNames()}. "
                + $"Project-wide, up to ~{ScannerUtil.Human(projectWide)} of memory is reclaimable across all scenes.";
            if (plan.TotalSavingsBytes < projectWide)
                note += " Open your heaviest scene and re-plan to act on more of it here.";
            return note;
        }

        /// <summary>Human-readable names of the open scene(s) for the scope note — "(untitled)" for an unsaved scene, matching the window.</summary>
        static string OpenSceneNames()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded) names.Add(string.IsNullOrEmpty(sc.name) ? "(untitled)" : sc.name);
            }
            return names.Count > 0 ? string.Join(", ", names) : "(none)";
        }
    }

    // ── Result DTOs (serialized to JSON by the pipeline server; public fields) ──

    /// <summary>The optimize plan projected for an agent: the auto tier it can apply over the wire, and the trade-off /
    /// manual items it must hand back to the user to run in the editor.</summary>
    public sealed class OptimizePlanDto
    {
        public string dimension;   // "build" | "memory"
        public string scope;       // "project" | "scene"
        public long upToBytes;     // auto + decision potential ("up to ~X"); manual is listed separately
        public string upToHuman;

        // Auto tier — the safe, reversible waste perflint_optimize_apply can apply over the wire.
        public int autoCount;
        public long autoBytes;
        public string autoHuman;
        public string[] autoGroups;      // "Title ×N ≈ ~X" (top 6 by savings)

        // Trade-offs + irreversible ops — surfaced with their cost, but DO THESE IN THE EDITOR (never applied over the wire).
        public OptimizeDecisionDto[] decideInEditor;

        // Report-only findings that carry an estimate but have no one-click action at all.
        public OptimizeManualDto[] manual;

        // Memory dimension only: scene-scope caveat + project-wide figure (memory is scoped to the open scene). Null for build.
        public string sceneScopeNote;

        public string editorHint;
        public string privacy;

        public static OptimizePlanDto From(OptimizePlan plan, SavingsDimension dim, string sceneScopeNote = null,
            int projectAutoFixableCount = 0)
        {
            return new OptimizePlanDto
            {
                dimension = PerfLintOptimizeCommands.DimName(dim),
                scope = dim == SavingsDimension.Memory ? "scene" : "project",
                upToBytes = plan.TotalSavingsBytes,
                upToHuman = ScannerUtil.Human(plan.TotalSavingsBytes),
                autoCount = plan.AutoItems.Count,
                autoBytes = plan.AutoSavingsBytes,
                autoHuman = ScannerUtil.Human(plan.AutoSavingsBytes),
                autoGroups = plan.AutoItems
                    .GroupBy(f => f.GroupTitleOrTitle)
                    .Select(x => new { Title = x.Key, Count = x.Count(), Bytes = x.Sum(f => OptimizePlan.SavingsOf(f, dim)) })
                    .OrderByDescending(x => x.Bytes)
                    .Take(6)
                    .Select(x => $"{x.Title} ×{x.Count} ≈ ~{ScannerUtil.Human(x.Bytes)}")
                    .ToArray(),
                decideInEditor = plan.DecisionGroups.Select(g => new OptimizeDecisionDto
                {
                    ruleId = g.RuleId,
                    label = g.Label,
                    count = g.Findings.Count,
                    bytes = g.SavingsBytes,
                    human = ScannerUtil.Human(g.SavingsBytes),
                    caution = g.Caution,
                    reversible = !g.Irreversible
                }).ToArray(),
                manual = plan.ManualGroups.Select(m => new OptimizeManualDto
                {
                    ruleId = m.RuleId,
                    title = m.Title,
                    count = m.Count,
                    bytes = m.SavingsBytes,
                    human = ScannerUtil.Human(m.SavingsBytes)
                }).ToArray(),
                sceneScopeNote = sceneScopeNote,
                editorHint = EditorHint(plan, projectAutoFixableCount),
                privacy = "Findings include file paths and line numbers, which are returned to the agent you connected. "
                    + "The scan runs locally and uploads no code or assets."
            };
        }

        /// <summary>
        /// The agent's next-step pointer, gated to what this plan can ACTUALLY do. Both clauses used to ship
        /// unconditionally and both could dangle: "perflint_optimize_apply applies the auto tier" when that tier is
        /// empty (see <see cref="PerfLintOptimizeCommands.FixPointer"/>), and the decideInEditor sentence when there
        /// is nothing under decideInEditor.
        /// </summary>
        internal static string EditorHint(OptimizePlan plan, int projectAutoFixableCount)
        {
            string hint = plan.AutoItems.Count > 0
                ? "perflint_optimize_apply applies this goal's auto tier for you."
                : "Nothing in this plan can be applied over the wire — this goal's auto tier is empty.";
            hint += PerfLintOptimizeCommands.FixPointer(projectAutoFixableCount, plan.AutoItems.Count);
            if (plan.DecisionGroups.Count > 0)
                hint += " Everything under decideInEditor is a judgment call left to you — open Tools > PerfLint in the "
                    + "editor and use the one-click Optimize dialog; each item shows its own confirmation (irreversible "
                    + "ones warn to commit to version control first).";
            return hint;
        }
    }

    public sealed class OptimizeDecisionDto
    {
        public string ruleId;
        public string label;
        public int count;
        public long bytes;
        public string human;
        public string caution;      // plain-language trade-off / cost
        public bool reversible;     // false = not undoable (e.g. duplicate merge deletes files)
    }

    public sealed class OptimizeManualDto
    {
        public string ruleId;
        public string title;
        public int count;
        public long bytes;
        public string human;
    }

    /// <summary>Result of perflint_optimize_apply. Any reclaimed figure is FIRM (the auto tier never includes ceiling
    /// items — those are actions, left for the editor), verified by the post-apply re-scan.</summary>
    public sealed class OptimizeApplyDto
    {
        public string status;       // "applied" | "dry_run" | "pro_required" | "nothing"
        public string dimension;
        public bool entitled;
        public int applied, failed, autoPlanned;
        public long reclaimedBytes; // firm, re-scan verified
        public string reclaimedHuman;
        public int scoreBefore, scoreAfter;
        public string gradeBefore, gradeAfter;
        public int decideInEditorCount;   // trade-offs left for the user
        public long decideInEditorBytes;
        public string upgradeUrl;   // set only when status == "pro_required"
        public string message;

        static OptimizeApplyDto Base(ScanResult before, OptimizePlan plan, SavingsDimension dim, string status) => new OptimizeApplyDto
        {
            status = status,
            dimension = PerfLintOptimizeCommands.DimName(dim),
            entitled = LicenseService.IsPro,
            autoPlanned = plan.AutoItems.Count,
            scoreBefore = before.HealthScore(),
            scoreAfter = before.HealthScore(),
            gradeBefore = before.HealthGrade(),
            gradeAfter = before.HealthGrade(),
            decideInEditorCount = plan.DecisionGroups.Count,
            decideInEditorBytes = plan.DecisionSavingsBytes,
        };

        public static OptimizeApplyDto DryRun(ScanResult before, OptimizePlan plan, SavingsDimension dim)
        {
            var dto = Base(before, plan, dim, "dry_run");
            dto.message = $"Dry run — nothing changed. Would apply {plan.AutoItems.Count} safe {dto.dimension} fixes "
                + $"(~{ScannerUtil.Human(plan.AutoSavingsBytes)}). {plan.DecisionGroups.Count} trade-off group(s) "
                + $"(~{ScannerUtil.Human(plan.DecisionSavingsBytes)}) are left for you to run in the editor."
                + PerfLintOptimizeCommands.FixPointer(before.AutoFixableCount, plan.AutoItems.Count);
            return dto;
        }

        public static OptimizeApplyDto Nothing(ScanResult before, OptimizePlan plan, SavingsDimension dim)
        {
            var dto = Base(before, plan, dim, "nothing");
            // Never let "nothing for this goal" read as "nothing anywhere" — the build goal's auto tier is
            // structurally empty and memory's is scene-scoped, while the project may have hundreds of one-click
            // fixes that only perflint_fix reaches.
            dto.message = (plan.DecisionGroups.Count > 0
                ? $"No auto-applicable {dto.dimension} waste. {plan.DecisionGroups.Count} trade-off group(s) "
                    + $"(~{ScannerUtil.Human(plan.DecisionSavingsBytes)}) remain — do those in the editor."
                : $"No safe {dto.dimension} waste and no trade-offs found for this goal.")
                + PerfLintOptimizeCommands.FixPointer(before.AutoFixableCount, plan.AutoItems.Count);
            return dto;
        }

        public static OptimizeApplyDto ProRequired(ScanResult before, OptimizePlan plan, SavingsDimension dim)
        {
            var dto = Base(before, plan, dim, "pro_required");
            dto.entitled = false;
            dto.upgradeUrl = LicenseSettings.BuyUrl;
            dto.message = $"Applying fixes needs Pro — activate a license in the editor. Would apply {plan.AutoItems.Count} "
                + $"safe {dto.dimension} fixes (~{ScannerUtil.Human(plan.AutoSavingsBytes)}). Upgrade: {LicenseSettings.BuyUrl}";
            return dto;
        }

        public static OptimizeApplyDto Applied(ScanResult before, ScanResult after, OptimizePlan plan, SavingsDimension dim, int applied, int failed)
        {
            long reclaimed = dim == SavingsDimension.Memory
                ? PerfLintOps.SavedMemoryBytes(before, after)
                : PerfLintOps.SavedBuildBytes(before, after);
            var dto = Base(before, plan, dim, "applied");
            dto.entitled = true;
            dto.applied = applied;
            dto.failed = failed;
            dto.reclaimedBytes = reclaimed;
            dto.reclaimedHuman = ScannerUtil.Human(reclaimed);
            dto.scoreAfter = after.HealthScore();
            dto.gradeAfter = after.HealthGrade();
            dto.message = $"Applied {applied} safe {dto.dimension} fixes ({failed} failed). "
                + $"Grade {before.HealthGrade()}→{after.HealthGrade()}, reclaimed ~{ScannerUtil.Human(reclaimed)} "
                + $"(re-scan verified). {plan.DecisionGroups.Count} trade-off group(s) "
                + $"(~{ScannerUtil.Human(plan.DecisionSavingsBytes)}) left for you in the editor. "
                + "Edit > Undo reverts what was just applied.";
            return dto;
        }
    }
}
