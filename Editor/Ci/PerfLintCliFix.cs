using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.Licensing;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Ci
{
    public static partial class PerfLintCli
    {
        /// <summary>
        /// Headless auto-fix: scan, apply the DETERMINISTIC safe fixes (import / project settings — the same
        /// set the editor's "Fix All" batch applies: undoable, no domain reload), re-scan, and report the
        /// realized delta. Trade-offs (Static Batching, dedup, Mipmap Streaming …) and AI fixes are NEVER
        /// auto-applied — they're reported as "needs review" so a human decides. Applying is a Pro feature;
        /// scanning is free.
        ///
        ///   Unity -batchmode -projectPath . -executeMethod PerfLint.Ci.PerfLintCli.RunFix \
        ///     [-perflintLicense &lt;key&gt;] [-perflintDryRun] [-perflintReportHtml r.html] [-perflintFixJson f.json]
        ///
        /// Exit: 0 = ok (fixed / dry-run / nothing to fix), 2 = error, 3 = Pro required (not entitled, not dry-run).
        /// Runs in an interactive editor are forced to dry-run (a hand-invoked RunFix never modifies the project).
        /// </summary>
        public static void RunFix() => ExitWith(RunFixCore);

        static int RunFixCore()
        {
            var opts = FixOptions.FromCommandLine(Environment.GetCommandLineArgs());

            var before = ScanWithProgress("scanning project…");
            ScanResultStore.Save(before);
            var plan = FixPlan.From(before);

            // Safety: only -batchmode ever applies. A hand-invoked RunFix (menu / eval) stays read-only.
            bool dryRun = opts.DryRun || !Application.isBatchMode;
            if (dryRun)
            {
                string why = opts.DryRun ? "dry-run"
                    : (!Application.isBatchMode ? "interactive editor — dry-run only (use -batchmode to apply)" : "dry-run");
                Emit("PERFLINT_FIX: DRY-RUN auto-fixable=" + plan.AutoFixable.Count + " needs-review=" + plan.NeedsReview
                     + " grade=" + before.HealthGrade() + " score=" + before.HealthScore() + " | " + why);
                WriteFixOutputs(opts, before, before, 0, 0, true, why);
                return 0;
            }

            // Applying is Pro. Resolve entitlement: persisted license first, then -perflintLicense (one seat).
            var ent = HeadlessLicense.TryEntitle(opts.LicenseKey);
            if (!ent.Entitled)
            {
                Emit("PERFLINT_FIX: PRO-REQUIRED auto-fixable=" + plan.AutoFixable.Count
                     + " needs-review=" + plan.NeedsReview + " | " + ent.Message);
                WriteFixOutputs(opts, before, before, 0, 0, false, ent.Message);
                return 3;
            }

            // Apply the safe deterministic batch — mirrors the editor's ApplyFixesCore, minus UI/progress.
            int total = plan.AutoFixable.Count;
            int step = Math.Max(1, total / 10);
            Emit("applying " + total + " deterministic fixes…");
            var (applied, failed) = PerfLintOps.ApplyFixes(plan, (done, tot) =>
            {
                if (done % step == 0 || done == tot) Emit("  applied " + done + "/" + tot);
            });

            // Re-scan for the honest realized delta (before/after difference of the re-scan — not a tally of attempts).
            var after = ScanWithProgress("re-scanning to measure impact…");
            ScanResultStore.Save(after);
            long savedMem = PerfLintOps.SavedMemoryBytes(before, after);
            long savedBuild = PerfLintOps.SavedBuildBytes(before, after);

            Emit("PERFLINT_FIX: DONE applied=" + applied + " failed=" + failed
                 + " findings=" + before.Findings.Count + "->" + after.Findings.Count
                 + " grade=" + before.HealthGrade() + "->" + after.HealthGrade()
                 + " saved~=" + Mb(savedMem) + "MB mem / " + Mb(savedBuild) + "MB build"
                 + " needs-review=" + FixPlan.From(after).NeedsReview);
            WriteFixOutputs(opts, before, after, applied, failed, false, ent.Message);
            return 0;
        }

        static long Mb(long bytes) => bytes / (1024 * 1024);

        static void WriteFixOutputs(FixOptions opts, ScanResult before, ScanResult after, int applied, int failed, bool dryRun, string licMsg)
        {
            if (!string.IsNullOrEmpty(opts.ReportHtmlPath))
                WriteText(opts.ReportHtmlPath, BuildHtml(after));
            if (!string.IsNullOrEmpty(opts.FixJsonPath))
                WriteText(opts.FixJsonPath, FixResultJson(before, after, applied, failed, dryRun, licMsg));
        }

        static string FixResultJson(ScanResult before, ScanResult after, int applied, int failed, bool dryRun, string licMsg)
        {
            var dto = new FixJson
            {
                dryRun = dryRun,
                applied = applied,
                failed = failed,
                autoFixable = FixPlan.From(before).AutoFixable.Count,
                needsReview = FixPlan.From(after).NeedsReview,
                findingsBefore = before.Findings.Count,
                findingsAfter = after.Findings.Count,
                gradeBefore = before.HealthGrade(),
                gradeAfter = after.HealthGrade(),
                scoreBefore = before.HealthScore(),
                scoreAfter = after.HealthScore(),
                savedMemoryBytesEst = PerfLintOps.SavedMemoryBytes(before, after),
                savedBuildBytesEst = PerfLintOps.SavedBuildBytes(before, after),
                license = licMsg,
            };
            return JsonUtility.ToJson(dto, true);
        }

        [Serializable]
        class FixJson
        {
            public bool dryRun; public int applied; public int failed;
            public int autoFixable; public int needsReview;
            public int findingsBefore; public int findingsAfter;
            public string gradeBefore; public string gradeAfter;
            public int scoreBefore; public int scoreAfter;
            public long savedMemoryBytesEst; public long savedBuildBytesEst;
            public string license;
        }
    }

    /// <summary>Command-line options for <see cref="PerfLintCli.RunFix"/>.</summary>
    public readonly struct FixOptions
    {
        public readonly string LicenseKey;    // -perflintLicense, or env PERFLINT_LICENSE
        public readonly bool DryRun;          // -perflintDryRun
        public readonly string ReportHtmlPath;
        public readonly string FixJsonPath;

        public FixOptions(string licenseKey, bool dryRun, string reportHtmlPath, string fixJsonPath)
        {
            LicenseKey = licenseKey; DryRun = dryRun; ReportHtmlPath = reportHtmlPath; FixJsonPath = fixJsonPath;
        }

        public static FixOptions FromCommandLine(string[] args)
        {
            string key = null, reportHtml = null, fixJson = null;
            bool dryRun = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-perflintLicense": key = NextArg(args, i); break;
                        case "-perflintDryRun": dryRun = true; break;
                        case "-perflintReportHtml": reportHtml = NextArg(args, i); break;
                        case "-perflintFixJson": fixJson = NextArg(args, i); break;
                    }
                }
            }
            // Env fallback for the key — lets CI keep it in a secret env var instead of the process arg list.
            if (string.IsNullOrEmpty(key))
            {
                var envKey = Environment.GetEnvironmentVariable("PERFLINT_LICENSE");
                if (!string.IsNullOrEmpty(envKey)) key = envKey.Trim();
            }
            return new FixOptions(key, dryRun, reportHtml, fixJson);
        }

        static string NextArg(string[] args, int i) => (i + 1 < args.Length) ? args[i + 1] : null;
    }

    /// <summary>
    /// Splits a scan into what RunFix may auto-apply vs what it must leave to a human. Pure + unit-tested.
    /// Auto = deterministic <see cref="IFix"/> fixes (import / project settings — the "Fix All" set: undoable,
    /// no domain reload). Needs-review = trade-off actions (<see cref="FindingAction"/>) and AI fixes, which
    /// change quality knobs, delete files, or need semantic judgment — never applied headless.
    /// </summary>
    public readonly struct FixPlan
    {
        public readonly IReadOnlyList<Finding> AutoFixable;
        public readonly int NeedsReview;

        FixPlan(IReadOnlyList<Finding> autoFixable, int needsReview) { AutoFixable = autoFixable; NeedsReview = needsReview; }

        public static FixPlan From(ScanResult scan)
        {
            var auto = new List<Finding>();
            int review = 0;
            if (scan != null)
            {
                foreach (var f in scan.Findings)
                {
                    if (f.Fix != null) auto.Add(f);
                    else if (f.Action != null || f.AiFixable) review++;
                }
            }
            return new FixPlan(auto, review);
        }
    }
}
