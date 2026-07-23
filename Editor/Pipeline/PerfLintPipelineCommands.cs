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
    /// "Fix All" applies, undoable via Edit &gt; Undo — and requires Pro. Scanning and the gate are free.
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

        [CliCommand("perflint_fix", "Auto-apply the safe deterministic fixes (import/project settings — the 'Fix All' set, undoable), re-scan, and report the delta. Trade-offs and AI fixes are left for review. Pro. Modifies the open project.")]
        public static FixResultDto PerfLintFix(
            [CliArg("dry_run", "Report what would be fixed and what needs review, without changing anything.")] bool dryRun = false)
        {
            var before = PerfLintOps.Scan();
            ScanResultStore.Save(before);
            var plan = FixPlan.From(before);

            if (dryRun)
                return FixResultDto.DryRun(before, plan);
            if (!LicenseService.IsPro)
                return FixResultDto.ProRequired(before, plan);

            var (applied, failed) = PerfLintOps.ApplyFixes(plan);
            var after = PerfLintOps.Scan();
            ScanResultStore.Save(after);
            return FixResultDto.Applied(before, after, plan, applied, failed);
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
        public string status;   // "dry_run" | "pro_required" | "applied"
        public bool dryRun;
        public bool entitled;
        public int applied, failed, autoFixable, needsReview;
        public int findingsBefore, findingsAfter, scoreBefore, scoreAfter;
        public string gradeBefore, gradeAfter;
        public long savedMemoryBytesEst, savedBuildBytesEst;
        public string message;

        public static FixResultDto DryRun(ScanResult before, FixPlan plan) => new FixResultDto
        {
            status = "dry_run",
            dryRun = true,
            entitled = LicenseService.IsPro,
            autoFixable = plan.AutoFixable.Count,
            needsReview = plan.NeedsReview,
            findingsBefore = before.Findings.Count,
            findingsAfter = before.Findings.Count,
            scoreBefore = before.HealthScore(),
            scoreAfter = before.HealthScore(),
            gradeBefore = before.HealthGrade(),
            gradeAfter = before.HealthGrade(),
            message = "Dry run — nothing changed. " + plan.AutoFixable.Count + " auto-fixable, " + plan.NeedsReview + " need review.",
        };

        public static FixResultDto ProRequired(ScanResult before, FixPlan plan) => new FixResultDto
        {
            status = "pro_required",
            entitled = false,
            autoFixable = plan.AutoFixable.Count,
            needsReview = plan.NeedsReview,
            findingsBefore = before.Findings.Count,
            findingsAfter = before.Findings.Count,
            scoreBefore = before.HealthScore(),
            scoreAfter = before.HealthScore(),
            gradeBefore = before.HealthGrade(),
            gradeAfter = before.HealthGrade(),
            message = "Applying fixes needs Pro — activate a license in the editor. " + plan.AutoFixable.Count + " would be auto-fixed.",
        };

        public static FixResultDto Applied(ScanResult before, ScanResult after, FixPlan plan, int applied, int failed)
        {
            long savedMem = PerfLintOps.SavedMemoryBytes(before, after);
            long savedBuild = PerfLintOps.SavedBuildBytes(before, after);
            int review = FixPlan.From(after).NeedsReview;
            return new FixResultDto
            {
                status = "applied",
                entitled = true,
                applied = applied,
                failed = failed,
                autoFixable = plan.AutoFixable.Count,
                needsReview = review,
                findingsBefore = before.Findings.Count,
                findingsAfter = after.Findings.Count,
                scoreBefore = before.HealthScore(),
                scoreAfter = after.HealthScore(),
                gradeBefore = before.HealthGrade(),
                gradeAfter = after.HealthGrade(),
                savedMemoryBytesEst = savedMem,
                savedBuildBytesEst = savedBuild,
                message = "Applied " + applied + " fixes (" + failed + " failed). Grade " + before.HealthGrade() + "→" + after.HealthGrade()
                          + ", saved ~" + (savedMem / (1024 * 1024)) + " MB memory. " + review + " need review.",
            };
        }
    }
}
