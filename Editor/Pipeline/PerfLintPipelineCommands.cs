using PerfLint.Ci;
using PerfLint.Core;
using PerfLint.Licensing;
using Unity.Pipeline.Commands;

namespace PerfLint.Ci.Pipeline
{
    /// <summary>
    /// PerfLint exposed as Unity Pipeline CLI commands (com.unity.pipeline). With the package installed
    /// and the pipeline server running you can, against your OPEN editor:
    /// <code>
    ///   unity command                 # lists every command, incl. perflint_scan / perflint_gate / perflint_fix
    ///   unity command perflint_scan
    ///   unity command perflint_gate --min-score 60
    ///   unity command perflint_fix    # or  perflint_fix --dry-run
    /// </code>
    /// No editor path, no -batchmode, no -projectPath, no -executeMethod, and no need to close the editor.
    ///
    /// These run in the live editor and RETURN a result (they never call EditorApplication.Exit).
    /// <see cref="PerfLintFix"/> modifies your open project — the same deterministic fixes the editor's
    /// "Fix All" applies, recoverable from version control rather than via Edit &gt; Undo — and requires Pro. Scanning and the gate are free.
    /// For headless CI (no editor running) use the batchmode entry points instead
    /// (PerfLint.Ci.PerfLintCli.RunGate / RunFix; see the docs).
    /// </summary>
    public static class PerfLintPipelineCommands
    {
        [CliCommand("perflint_scan", "Scan the project (performance / assets / migration) and return the health score, grade, and finding counts. Local, free, read-only.")]
        public static ScanSummary PerfLintScan()
        {
            var r = PerfLintOps.Scan();
            ScanResultStore.Save(r);
            return ScanSummary.From(r);
        }

        [CliCommand("perflint_gate", "Scan and evaluate a health-gate policy; returns pass/fail plus the reasons. Free, read-only.")]
        public static GateResult PerfLintGate(
            [CliArg("max_critical", "Fail if the Critical count exceeds this. Default 0 (any Critical fails); -1 disables.")] int maxCritical = 0,
            [CliArg("min_score", "Fail if the health score is below this. -1 disables (default).")] int minScore = -1,
            [CliArg("max_warning", "Fail if the Warning count exceeds this. -1 disables (default).")] int maxWarning = -1)
        {
            var r = PerfLintOps.Scan();
            ScanResultStore.Save(r);
            var v = GatePolicy.Evaluate(r, new GateOptions(minScore, maxCritical, maxWarning, null, null));
            return GateResult.From(v);
        }

        [CliCommand("perflint_fix", "Auto-apply the safe deterministic fixes (import/project settings — the 'Fix All' set, undoable), re-scan, and report the delta. Narrow the run with rule_id / domain / min_severity to fix one category at a time. Trade-offs and AI fixes are left for review. Pro. Modifies the open project.")]
        public static FixResultDto PerfLintFix(
            [CliArg("dry_run", "Report what would be fixed and what needs review, without changing anything.")] bool dryRun = false,
            [CliArg("rule_id", "Only fix this rule — exact, prefix, or the short form without its domain prefix: 'PERF.TEX', 'PERF.TEX002' and 'TEX002' all work ('GC003' finds 'PERF.GC003'). Empty = every rule.")] string ruleId = null,
            [CliArg("domain", "Only fix this domain: performance / assets / migration / projectsettings / runtime. Empty = all.")] string domain = null,
            [CliArg("min_severity", "Only fix findings at or above this severity: info / warning / critical. Default info (= all).")] string minSeverity = "info")
        {
            var before = PerfLintOps.Scan();
            ScanResultStore.Save(before);

            // Narrowing is what lets an agent do what it just told the user ("fix the 121 warnings") instead of
            // silently applying the whole auto set. Same filter semantics as perflint_list_findings, so a rollup
            // row an agent read there maps 1:1 onto a fix run here.
            var filter = FindingQuery.Filter.Parse(minSeverity, domain, ruleId);
            var scoped = filter.IsAll ? before.Findings : FindingQuery.Match(before.Findings, filter);
            var plan = FixPlan.FromFindings(scoped);
            string scope = filter.Describe();

            if (dryRun)
                return FixResultDto.DryRun(before, plan, scope);
            if (plan.AutoFixable.Count == 0)
            {
                var nothing = FixResultDto.Nothing(before, plan, scope);
                // A rule filter that names no rule in the scan must not read as "already clean".
                nothing.message += ListFindingsDto.UnknownRuleHint(before.Findings, ruleId);
                return nothing;
            }
            if (!LicenseService.IsPro)
                return FixResultDto.ProRequired(before, plan, scope);

            var (applied, failed) = PerfLintOps.ApplyFixes(plan);
            var after = PerfLintOps.Scan();
            ScanResultStore.Save(after);
            // Scope the post-run review tally the same way, or a narrowed run would report the whole project's backlog.
            int reviewAfter = FixPlan.FromFindings(filter.IsAll ? after.Findings : FindingQuery.Match(after.Findings, filter)).NeedsReview;
            return FixResultDto.Applied(before, after, plan, applied, failed, scope, reviewAfter);
        }
    }

    // ── Result DTOs (serialized to JSON by the pipeline server; public fields) ──

    public sealed class ScanSummary
    {
        public int score;
        public string grade;
        public int findings, critical, warning, info, autoFixable, needsReview;

        public static ScanSummary From(ScanResult r)
        {
            var plan = FixPlan.From(r);
            return new ScanSummary
            {
                score = r.HealthScore(),
                grade = r.HealthGrade(),
                findings = r.Findings.Count,
                critical = r.CriticalCount,
                warning = r.WarningCount,
                info = r.InfoCount,
                autoFixable = plan.AutoFixable.Count,
                needsReview = plan.NeedsReview,
            };
        }
    }

    public sealed class GateResult
    {
        public bool passed;
        public int score;
        public string grade;
        public int critical, warning, info;
        public string violations;

        public static GateResult From(GateVerdict v) => new GateResult
        {
            passed = v.Passed,
            score = v.Score,
            grade = v.Grade,
            critical = v.Critical,
            warning = v.Warning,
            info = v.Info,
            violations = v.Violations,
        };
    }

    public sealed class FixResultDto
    {
        public string status;   // "dry_run" | "nothing" | "pro_required" | "applied"
        public bool dryRun;
        public bool entitled;
        /// <summary>What this run covered — "whole project" or the narrowing that was applied ("rule PERF.TEX002"…).
        /// Always reported so a narrowed run can never read as a whole-project one.</summary>
        public string scope;
        public int applied, failed, autoFixable, needsReview;
        public int findingsBefore, findingsAfter, scoreBefore, scoreAfter;
        public string gradeBefore, gradeAfter;
        public long savedMemoryBytesEst, savedBuildBytesEst;
        public string message;

        static FixResultDto Base(ScanResult before, FixPlan plan, string scope, string status) => new FixResultDto
        {
            status = status,
            scope = scope,
            entitled = LicenseService.IsPro,
            autoFixable = plan.AutoFixable.Count,
            needsReview = plan.NeedsReview,
            findingsBefore = before.Findings.Count,
            findingsAfter = before.Findings.Count,
            scoreBefore = before.HealthScore(),
            scoreAfter = before.HealthScore(),
            gradeBefore = before.HealthGrade(),
            gradeAfter = before.HealthGrade(),
        };

        public static FixResultDto DryRun(ScanResult before, FixPlan plan, string scope)
        {
            var dto = Base(before, plan, scope, "dry_run");
            dto.dryRun = true;
            dto.message = "Dry run — nothing changed. " + plan.AutoFixable.Count + " auto-fixable, "
                          + plan.NeedsReview + " need review (scope: " + scope + ").";
            return dto;
        }

        /// <summary>Nothing to apply in this scope — reported explicitly instead of "applied 0", so a filter that
        /// matched no fixable finding never reads as a successful run.</summary>
        public static FixResultDto Nothing(ScanResult before, FixPlan plan, string scope)
        {
            var dto = Base(before, plan, scope, "nothing");
            dto.message = "No auto-fixable findings in scope (" + scope + "). " + plan.NeedsReview
                          + " need review — nothing was changed.";
            return dto;
        }

        public static FixResultDto ProRequired(ScanResult before, FixPlan plan, string scope)
        {
            var dto = Base(before, plan, scope, "pro_required");
            dto.entitled = false;
            dto.message = "Applying fixes needs Pro — activate a license in the editor. " + plan.AutoFixable.Count
                          + " would be auto-fixed (scope: " + scope + ").";
            return dto;
        }

        public static FixResultDto Applied(ScanResult before, ScanResult after, FixPlan plan, int applied, int failed,
            string scope, int reviewAfter)
        {
            long savedMem = PerfLintOps.SavedMemoryBytes(before, after);
            long savedBuild = PerfLintOps.SavedBuildBytes(before, after);
            var dto = Base(before, plan, scope, "applied");
            dto.entitled = true;
            dto.applied = applied;
            dto.failed = failed;
            dto.needsReview = reviewAfter;
            dto.findingsAfter = after.Findings.Count;
            dto.scoreAfter = after.HealthScore();
            dto.gradeAfter = after.HealthGrade();
            dto.savedMemoryBytesEst = savedMem;
            dto.savedBuildBytesEst = savedBuild;
            dto.message = "Applied " + applied + " fixes (" + failed + " failed) in scope: " + scope
                          + ". Grade " + before.HealthGrade() + "→" + after.HealthGrade()
                          + ", saved ~" + (savedMem / (1024 * 1024)) + " MB memory. " + reviewAfter
                          + " need review. Edit > Undo does NOT revert this — restore from version control if you want it back.";
            return dto;
        }
    }
}
