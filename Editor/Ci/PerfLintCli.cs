using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PerfLint.Core;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Ci
{
    /// <summary>
    /// Headless entry points for CI / automation. Everything here is diagnosis only
    /// (scan → health score → shareable report), which is permanently free — no Pro
    /// license is required, and nothing in the project is modified.
    ///
    /// Health gate (fails the build when the project regresses):
    /// <code>
    ///   Unity -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod PerfLint.Ci.PerfLintCli.RunGate \
    ///     [-perflintMinScore 70] [-perflintMaxCritical 0] [-perflintMaxWarning -1] \
    ///     [-perflintGateJson gate.json] [-perflintReportHtml report.html]
    ///   # exit code: 0 = pass, 1 = gate failed, 2 = error
    /// </code>
    ///
    /// Report export (writes the self-contained shareable HTML report):
    /// <code>
    ///   Unity -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod PerfLint.Ci.PerfLintCli.ExportReport [-perflintReportHtml report.html]
    ///   # exit code: 0 = ok, 2 = error
    /// </code>
    ///
    /// The exit code and the JSON file are the machine-readable contract. The console
    /// line (prefixed PERFLINT_GATE / PERFLINT_REPORT) is a convenience for log greps.
    /// All of it is stable English and intentionally NOT localized.
    /// </summary>
    public static partial class PerfLintCli
    {
        const string LogTag = "[PerfLint CI]";

        /// <summary>Scan, evaluate the gate policy, emit the verdict, and exit with 0/1/2.</summary>
        public static void RunGate()
        {
            ExitWith(RunGateCore);
        }

        /// <summary>Scan and write the shareable HTML report to -perflintReportHtml (default ./PerfLintReport.html). Exit 0/2.</summary>
        public static void ExportReport()
        {
            ExitWith(ExportReportCore);
        }

        static int RunGateCore()
        {
            var opts = GateOptions.FromCommandLine(Environment.GetCommandLineArgs());
            var result = ScanWithProgress("scanning project…");
            ScanResultStore.Save(result); // harmless in CI; makes the scan inspectable if Library persists

            var verdict = GatePolicy.Evaluate(result, opts);
            Emit(verdict.ToLogLine());

            if (!string.IsNullOrEmpty(opts.GateJsonPath))
                WriteText(opts.GateJsonPath, verdict.ToJson());
            if (!string.IsNullOrEmpty(opts.ReportHtmlPath))
                WriteText(opts.ReportHtmlPath, BuildHtml(result));

            return verdict.Passed ? 0 : 1;
        }

        static int ExportReportCore()
        {
            var opts = GateOptions.FromCommandLine(Environment.GetCommandLineArgs());
            var path = string.IsNullOrEmpty(opts.ReportHtmlPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "PerfLintReport.html")
                : opts.ReportHtmlPath;

            var result = ScanWithProgress("scanning project…");
            ScanResultStore.Save(result);
            WriteText(path, BuildHtml(result));
            Emit("PERFLINT_REPORT: OK path=" + path
                 + " grade=" + result.HealthGrade() + " score=" + result.HealthScore()
                 + " findings=" + result.Findings.Count);
            return 0;
        }

        // Runs body, maps exceptions to exit code 2, and exits the editor ONLY in batch mode.
        // In an interactive editor we never call EditorApplication.Exit (that would close the
        // user's editor if someone invoked this by hand) — we just log what the code would be.
        static void ExitWith(Func<int> body)
        {
            int code;
            try
            {
                code = body();
            }
            catch (Exception e)
            {
                Emit("PERFLINT_GATE: ERROR " + e.Message);
                Debug.LogError(LogTag + " " + e);
                code = 2;
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(code);
            else
                Debug.Log(LogTag + " interactive editor — not exiting. Exit code would be " + code + ".");
        }

        static string BuildHtml(ScanResult result) =>
            HtmlReport.Build(result, Application.productName, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        static void WriteText(string path, string content)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(false)); // no BOM
            Debug.Log(LogTag + " wrote " + path);
        }

        static void Emit(string line)
        {
            Console.WriteLine(line);          // clean stdout line for CI to grep
            Debug.Log(LogTag + " " + line);   // and into Editor.log / -logFile
        }

        // Runs a scan while emitting lightweight progress (one line as each scanner starts, with the
        // overall percentage) so a headless run on a large project doesn't look frozen. Reuses the same
        // ReportProgress hook the editor's progress bar uses.
        static ScanResult ScanWithProgress(string phaseLabel)
        {
            Emit(phaseLabel);
            string lastScanner = null;
            return PerfLintOps.Scan((name, frac) =>
            {
                if (name == lastScanner) return; // throttle: one line when each scanner begins
                lastScanner = name;
                Emit("  scan: " + name + " (" + (int)(frac * 100) + "%)");
            });
        }
    }

    /// <summary>Gate thresholds parsed from the command line. A threshold of -1 disables that check.</summary>
    public readonly struct GateOptions
    {
        /// <summary>Fail if health score &lt; this. -1 = disabled (default).</summary>
        public readonly int MinScore;
        /// <summary>Fail if Critical count &gt; this. Default 0 (any Critical fails). -1 = disabled.</summary>
        public readonly int MaxCritical;
        /// <summary>Fail if Warning count &gt; this. -1 = disabled (default).</summary>
        public readonly int MaxWarning;
        /// <summary>Optional path to write the verdict JSON.</summary>
        public readonly string GateJsonPath;
        /// <summary>Optional path to also write the HTML report during a gate run.</summary>
        public readonly string ReportHtmlPath;

        public GateOptions(int minScore, int maxCritical, int maxWarning, string gateJsonPath, string reportHtmlPath)
        {
            MinScore = minScore;
            MaxCritical = maxCritical;
            MaxWarning = maxWarning;
            GateJsonPath = gateJsonPath;
            ReportHtmlPath = reportHtmlPath;
        }

        public static GateOptions FromCommandLine(string[] args)
        {
            int minScore = -1, maxCritical = 0, maxWarning = -1;
            string gateJson = null, reportHtml = null;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-perflintMinScore": minScore = ParseInt(NextArg(args, i), minScore); break;
                        case "-perflintMaxCritical": maxCritical = ParseInt(NextArg(args, i), maxCritical); break;
                        case "-perflintMaxWarning": maxWarning = ParseInt(NextArg(args, i), maxWarning); break;
                        case "-perflintGateJson": gateJson = NextArg(args, i); break;
                        case "-perflintReportHtml": reportHtml = NextArg(args, i); break;
                    }
                }
            }

            return new GateOptions(minScore, maxCritical, maxWarning, gateJson, reportHtml);
        }

        static string NextArg(string[] args, int i) => (i + 1 < args.Length) ? args[i + 1] : null;

        static int ParseInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>The outcome of evaluating a <see cref="ScanResult"/> against a <see cref="GateOptions"/> policy.</summary>
    public readonly struct GateVerdict
    {
        public readonly bool Passed;
        public readonly int Score;
        public readonly string Grade;
        public readonly int Critical;
        public readonly int Warning;
        public readonly int Info;
        /// <summary>Estimated recoverable memory across the findings (informational, not part of pass/fail).</summary>
        public readonly long FoundMemoryBytes;
        /// <summary>Estimated recoverable build size across the findings (informational).</summary>
        public readonly long FoundBuildBytes;
        /// <summary>Empty when passed; otherwise a "; "-joined list of the thresholds that tripped.</summary>
        public readonly string Violations;

        public GateVerdict(bool passed, int score, string grade, int critical, int warning, int info,
            long foundMemoryBytes, long foundBuildBytes, string violations)
        {
            Passed = passed;
            Score = score;
            Grade = grade;
            Critical = critical;
            Warning = warning;
            Info = info;
            FoundMemoryBytes = foundMemoryBytes;
            FoundBuildBytes = foundBuildBytes;
            Violations = violations ?? string.Empty;
        }

        public string ToLogLine()
        {
            var s = "PERFLINT_GATE: " + (Passed ? "PASS" : "FAIL")
                + " score=" + Score + " grade=" + Grade
                + " critical=" + Critical + " warning=" + Warning + " info=" + Info
                + " found~=" + Mb(FoundMemoryBytes) + "MB";
            if (!Passed)
                s += " | violations: " + Violations;
            return s;
        }

        public string ToJson()
        {
            var dto = new Json
            {
                result = Passed ? "PASS" : "FAIL",
                passed = Passed,
                score = Score,
                grade = Grade,
                critical = Critical,
                warning = Warning,
                info = Info,
                foundMemoryBytesEst = FoundMemoryBytes,
                foundBuildBytesEst = FoundBuildBytes,
                violations = Violations
            };
            return JsonUtility.ToJson(dto, true);
        }

        static long Mb(long bytes) => bytes / (1024 * 1024);

        [Serializable]
        class Json
        {
            public string result;
            public bool passed;
            public int score;
            public string grade;
            public int critical;
            public int warning;
            public int info;
            public long foundMemoryBytesEst;
            public long foundBuildBytesEst;
            public string violations;
        }
    }

    /// <summary>Pure, deterministic gate policy: evaluate a scan result against thresholds. Unit-tested.</summary>
    public static class GatePolicy
    {
        public static GateVerdict Evaluate(ScanResult result, GateOptions options)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            int score = result.HealthScore();
            int critical = result.CriticalCount;
            int warning = result.WarningCount;
            int info = result.InfoCount;

            var totals = SavingsSummary.Compute(result.Findings);

            var violations = new List<string>();
            if (options.MaxCritical >= 0 && critical > options.MaxCritical)
                violations.Add("critical " + critical + " > " + options.MaxCritical);
            if (options.MaxWarning >= 0 && warning > options.MaxWarning)
                violations.Add("warning " + warning + " > " + options.MaxWarning);
            if (options.MinScore >= 0 && score < options.MinScore)
                violations.Add("score " + score + " < " + options.MinScore);

            return new GateVerdict(
                passed: violations.Count == 0,
                score: score,
                grade: result.HealthGrade(),
                critical: critical,
                warning: warning,
                info: info,
                foundMemoryBytes: totals.MemoryBytes,
                foundBuildBytes: totals.BuildBytes,
                violations: string.Join("; ", violations));
        }
    }
}
