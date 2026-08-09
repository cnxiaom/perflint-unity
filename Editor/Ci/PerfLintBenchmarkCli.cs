using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PerfLint.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfLint.Ci
{
    /// <summary>
    /// Headless driver for the repeatability study: runs the same benchmark N times, changing nothing between
    /// repetitions, and reports how much the numbers move on their own.
    ///
    /// This exists to answer one question before anything is built on top of it — <b>is an editor Play Mode
    /// measurement stable enough that a before/after difference means something?</b> If the run-to-run spread
    /// of frame time is ±20%, then "24ms → 16ms after our fix" is not a result, it is a coin flip with a
    /// narrative, and the honest move is to not ship that claim. The spread measured here becomes the noise
    /// band every later comparison is judged against (<see cref="BenchmarkStats"/>).
    ///
    /// <code>
    ///   Unity -projectPath &lt;proj&gt; \
    ///     -executeMethod PerfLint.Ci.PerfLintBenchmarkCli.RunNoiseStudy \
    ///     [-perflintBenchScene Assets/Scenes/Main.unity] \
    ///     [-perflintBenchWarmup 5] [-perflintBenchSample 20] [-perflintBenchReps 5] \
    ///     [-perflintBenchReport noise-report.txt] [-perflintBenchCsv noise.csv] \
    ///     [-perflintBenchMaxCv 15] [-perflintBenchExit]
    ///   # exit: 0 = study completed, 1 = benchmark failed or CV gate tripped, 2 = error
    /// </code>
    ///
    /// Deliberately NOT run with <c>-batchmode -nographics</c>: with no graphics device the render counters have
    /// nothing to count and frame time measures an idle editor, which would make the study measure the wrong
    /// thing perfectly. Do not add <c>-quit</c> either — the driver runs asynchronously across several Play Mode
    /// round-trips and exits by itself.
    ///
    /// Output is stable English, like the rest of the CI surface.
    /// </summary>
    [InitializeOnLoad]
    public static class PerfLintBenchmarkCli
    {
        const string LogTag = "[PerfLint Benchmark CI]";

        // The study outlives several domain reloads (two per Play Mode round-trip), and each one wipes both our
        // static fields AND our EditorApplication.update subscription. Everything the post-run reporting needs
        // therefore lives in SessionState, and the [InitializeOnLoad] constructor re-arms the poll.
        // Found the hard way: without this the runs completed correctly, then nothing wrote the report and the
        // editor never exited — a failure invisible to unit tests, which is exactly why this path needs a real run.
        const string KActive = "PerfLint.BenchStudy.Active";
        const string KReport = "PerfLint.BenchStudy.Report";
        const string KCsv = "PerfLint.BenchStudy.Csv";
        const string KMaxCv = "PerfLint.BenchStudy.MaxCv";
        const string KExit = "PerfLint.BenchStudy.Exit";
        const string KRequireHotspots = "PerfLint.BenchStudy.RequireHotspots";

        static PerfLintBenchmarkCli()
        {
            if (SessionState.GetBool(KActive, false)) Attach();
        }

        static void Attach()
        {
            EditorApplication.update -= PollUntilDone;
            EditorApplication.update += PollUntilDone;
        }

        public static void RunNoiseStudy()
        {
            try
            {
                var opts = BenchOptions.FromCommandLine(Environment.GetCommandLineArgs());
                SessionState.SetString(KReport, opts.ReportPath ?? "");
                SessionState.SetString(KCsv, opts.CsvPath ?? "");
                SessionState.SetFloat(KMaxCv, opts.MaxCvPercent);
                SessionState.SetBool(KExit, opts.ExitEditor);
                SessionState.SetBool(KRequireHotspots, opts.RequireHotspots);

                string sceneError = OpenTargetScene(opts.ScenePath, out string scenePath);
                if (sceneError != null) { Fatal(sceneError); return; }

                var spec = new BenchmarkSpec
                {
                    scenePath = scenePath,
                    sceneGuid = AssetDatabase.AssetPathToGUID(scenePath),
                    warmupSeconds = opts.WarmupSeconds,
                    sampleSeconds = opts.SampleSeconds,
                    repetitions = opts.Repetitions,
                    driveCamera = false,
                    saveRuntimeSession = opts.SaveSession
                };

                Emit($"PERFLINT_BENCH: start scene={scenePath} reps={spec.repetitions} " +
                     $"warmup={spec.warmupSeconds:0.#}s sample={spec.sampleSeconds:0.#}s");

                string refusal = BenchmarkRunner.Begin(spec);
                if (refusal != null) { Fatal("refused to start: " + refusal); return; }

                SessionState.SetBool(KActive, true);
                Attach();
            }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} {e}");
                Fatal("error: " + e.Message);
            }
        }

        static void PollUntilDone()
        {
            if (!BenchmarkRunner.IsFinished) return;

            EditorApplication.update -= PollUntilDone;
            SessionState.SetBool(KActive, false);

            try
            {
                var session = BenchmarkRunner.LoadSession(BenchmarkRunner.CurrentSessionId);
                bool failed = BenchmarkRunner.CurrentPhase == BenchmarkRunner.Phase.Failed;

                if (session == null || !session.HasRuns)
                {
                    Fatal("no runs were collected" + (failed ? " · " + BenchmarkRunner.LastError : ""));
                    return;
                }

                var analysis = NoiseAnalysis.Of(session);
                string report = analysis.ToReport(failed ? BenchmarkRunner.LastError : null);

                Console.WriteLine(report);
                Debug.Log($"{LogTag}\n{report}");

                string reportPath = SessionState.GetString(KReport, "");
                string csvPath = SessionState.GetString(KCsv, "");
                if (!string.IsNullOrEmpty(reportPath)) WriteText(reportPath, report);
                if (!string.IsNullOrEmpty(csvPath)) WriteText(csvPath, analysis.ToCsv());

                Emit(analysis.ToLogLine());

                // The merge phase's only real test. Printed always when asked for; fatal only when it did not happen.
                bool hotspotsOk = true;
                if (SessionState.GetBool(KRequireHotspots, false))
                {
                    hotspotsOk = AuditHotspots(session, out string audit);
                    Console.WriteLine(audit);
                    Debug.Log($"{LogTag}\n{audit}");
                    if (!string.IsNullOrEmpty(reportPath)) AppendText(reportPath, "\n" + audit);
                }

                float maxCv = SessionState.GetFloat(KMaxCv, -1f);
                int code = 0;
                if (failed) code = 1;
                else if (!hotspotsOk) code = 1;
                else if (maxCv >= 0)
                {
                    var ft = analysis.Find(BenchmarkMetricKeys.FrameTimeMs);
                    if (ft.HasData && ft.Cv * 100.0 > maxCv)
                    {
                        Emit($"PERFLINT_BENCH: FAIL frame-time CV {ft.Cv * 100:0.0}% > {maxCv:0.0}%");
                        code = 1;
                    }
                }
                Exit(code);
            }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} {e}");
                Fatal("error while reporting: " + e.Message);
            }
        }

        /// <summary>
        /// Checks that every repetition actually came back with merged hotspots, and prints what they were.
        ///
        /// What this catches that no unit test can: the merge is an asynchronous replay of profiler frames that must
        /// complete INSIDE the Play Mode session that recorded them, because leaving Play Mode reloads the domain and
        /// takes the callback with it. It went unnoticed for the entire life of the benchmark path that this never
        /// ran at all, and a pure-function test would not have noticed either.
        ///
        /// Deliberately asserts on <c>hotspotsMerged</c> rather than on the list being non-empty: an empty list from a
        /// merge that ran is a real (if unlikely) answer, whereas a merge that never ran is the failure.
        /// </summary>
        static bool AuditHotspots(BenchmarkSession session, out string text)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Hotspot merge audit");
            sb.AppendLine("-------------------");

            int merged = 0, withMarkers = 0, withHitRate = 0;
            foreach (var run in session.Runs)
            {
                var hs = run.hotspots ?? Array.Empty<BenchmarkHotspot>();
                if (run.hotspotsMerged) merged++;
                if (hs.Length > 0) withMarkers++;

                var top = hs.Length > 0 ? hs[0] : null;
                bool hasHits = top != null && top.sampledFrames > 0;
                if (hasHits) withHitRate++;

                sb.AppendLine($"run {run.index + 1}: merged={run.hotspotsMerged} markers={hs.Length}" +
                              (top != null
                                  ? $" · top \"{top.marker}\" {top.selfMsPerFrame:0.00} ms/frame · hit {top.hitFrames}/{top.sampledFrames}"
                                  : " · (none)"));
            }

            int n = session.Runs.Count;
            bool ok = merged == n && withHitRate == n;
            sb.AppendLine();
            sb.AppendLine($"{merged}/{n} runs merged · {withMarkers}/{n} produced markers · {withHitRate}/{n} carry a sampled-frame hit rate");
            sb.AppendLine(ok
                ? "PERFLINT_BENCH: HOTSPOTS OK"
                : "PERFLINT_BENCH: FAIL hotspot merge did not complete on every run — see the editor log for a merge timeout or an early Play Mode exit");
            text = sb.ToString();
            return ok;
        }

        static void AppendText(string path, string text)
        {
            try { File.AppendAllText(path, text); } catch { /* the report is a convenience, not the result */ }
        }

        /// <summary>Opens the requested scene, or resolves the one to use. Returns null on success.</summary>
        static string OpenTargetScene(string requested, out string scenePath)
        {
            scenePath = null;

            if (!string.IsNullOrEmpty(requested))
            {
                if (!File.Exists(requested)) return $"scene not found: {requested}";
                EditorSceneManager.OpenScene(requested, OpenSceneMode.Single);
                scenePath = requested;
                return null;
            }

            // No scene given: prefer whatever is already open, then the first enabled scene in Build Settings.
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && !string.IsNullOrEmpty(active.path))
            {
                scenePath = active.path;
                return null;
            }

            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled || string.IsNullOrEmpty(s.path)) continue;
                EditorSceneManager.OpenScene(s.path, OpenSceneMode.Single);
                scenePath = s.path;
                return null;
            }

            return "no scene to benchmark — pass -perflintBenchScene, or open a scene / add one to Build Settings";
        }

        static void Fatal(string message)
        {
            SessionState.SetBool(KActive, false);
            Emit("PERFLINT_BENCH: ERROR " + message);
            Exit(2);
        }

        // Mirrors PerfLintCli.ExitWith: never close an interactive editor someone invoked this from by hand.
        // The extra opt-in flag exists because this study, unlike the scan gate, CANNOT run under -nographics
        // (no graphics device → no draw calls to count, and frame time measures an idle editor). It therefore
        // runs in a normal windowed editor, where isBatchMode is false and a script driving it would otherwise
        // wait forever — so an automated caller passes -perflintBenchExit to ask for the editor to close itself.
        static void Exit(int code)
        {
            if (Application.isBatchMode || SessionState.GetBool(KExit, false)) EditorApplication.Exit(code);
            else Debug.Log($"{LogTag} interactive editor — not exiting. Exit code would be {code}.");
        }

        static void Emit(string line)
        {
            Console.WriteLine(line);
            Debug.Log($"{LogTag} {line}");
        }

        static void WriteText(string path, string content)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Debug.Log($"{LogTag} wrote {path}");
        }
    }

    /// <summary>Command-line options for the benchmark driver.</summary>
    public readonly struct BenchOptions
    {
        public readonly string ScenePath;
        public readonly float WarmupSeconds;
        public readonly float SampleSeconds;
        public readonly int Repetitions;
        public readonly string ReportPath;
        public readonly string CsvPath;
        /// <summary>Fail the run when the frame-time coefficient of variation exceeds this percentage. -1 = disabled.</summary>
        public readonly float MaxCvPercent;
        /// <summary>Close the editor when finished even outside batch mode — see <c>PerfLintBenchmarkCli.Exit</c> for why this is needed here.</summary>
        public readonly bool ExitEditor;
        /// <summary>Also publish the measurement as the panel's runtime session, so the report and the "do this next" card pick it up.</summary>
        public readonly bool SaveSession;

        /// <summary>
        /// Fail the run unless every repetition came back with merged hotspots.
        ///
        /// This is the integration test for the merge phase, and it needs a real run to mean anything: the merge is
        /// an async replay of profiler frames that has to finish INSIDE the Play Mode session that produced them,
        /// because the domain reload on the way out destroys the callback. No EditMode test can reach that — which
        /// is exactly how the benchmark path went this long without merging hotspots at all.
        /// </summary>
        public readonly bool RequireHotspots;

        public BenchOptions(string scenePath, float warmup, float sample, int reps, string reportPath, string csvPath,
            float maxCvPercent, bool exitEditor, bool saveSession = false, bool requireHotspots = false)
        {
            ScenePath = scenePath; WarmupSeconds = warmup; SampleSeconds = sample;
            Repetitions = reps; ReportPath = reportPath; CsvPath = csvPath;
            MaxCvPercent = maxCvPercent; ExitEditor = exitEditor; SaveSession = saveSession;
            RequireHotspots = requireHotspots;
        }

        public static BenchOptions FromCommandLine(string[] args)
        {
            string scene = null, report = null, csv = null;
            float warmup = 5f, sample = 20f, maxCv = -1f;
            bool exitEditor = false, saveSession = false, requireHotspots = false;
            int reps = 5; // a study wants more repetitions than a routine measurement

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-perflintBenchScene": scene = Next(args, i); break;
                        case "-perflintBenchWarmup": warmup = ParseFloat(Next(args, i), warmup); break;
                        case "-perflintBenchSample": sample = ParseFloat(Next(args, i), sample); break;
                        case "-perflintBenchReps": reps = ParseInt(Next(args, i), reps); break;
                        case "-perflintBenchReport": report = Next(args, i); break;
                        case "-perflintBenchCsv": csv = Next(args, i); break;
                        case "-perflintBenchMaxCv": maxCv = ParseFloat(Next(args, i), maxCv); break;
                        case "-perflintBenchExit": exitEditor = true; break;
                        case "-perflintBenchSaveSession": saveSession = true; break;
                        case "-perflintBenchRequireHotspots": requireHotspots = true; break;
                    }
                }
            }

            return new BenchOptions(scene, warmup, sample, Math.Max(1, reps), report, csv, maxCv, exitEditor, saveSession,
                requireHotspots);
        }

        static string Next(string[] args, int i) => (i + 1 < args.Length) ? args[i + 1] : null;

        static int ParseInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static float ParseFloat(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>
    /// Turns a set of identical repetitions into the answer the study exists for: per metric, how much does this
    /// number move when nothing changes, and is that small enough to detect a real improvement through.
    /// Pure aside from string building — unit-tested.
    /// </summary>
    public sealed class NoiseAnalysis
    {
        public BenchmarkSession Session { get; }
        public IReadOnlyList<BenchmarkStats.Spread> Spreads { get; }

        NoiseAnalysis(BenchmarkSession session, IReadOnlyList<BenchmarkStats.Spread> spreads)
        {
            Session = session;
            Spreads = spreads;
        }

        public static NoiseAnalysis Of(BenchmarkSession session)
        {
            var spreads = new List<BenchmarkStats.Spread>();
            foreach (var key in BenchmarkMetricKeys.All)
            {
                var s = BenchmarkStats.SpreadOf(session.Runs, key);
                if (s.HasData) spreads.Add(s);
            }
            return new NoiseAnalysis(session, spreads);
        }

        public BenchmarkStats.Spread Find(string key)
        {
            foreach (var s in Spreads)
                if (string.Equals(s.Key, key, StringComparison.Ordinal)) return s;
            return new BenchmarkStats.Spread(key, 0, 0, 0, 0, 0);
        }

        /// <summary>
        /// Whether the first run stands apart from the rest — the direct test of whether the warmup window is long
        /// enough. A first run that is systematically slower means shader compilation / streaming / JIT is still
        /// being paid inside the sampling window, and every "improvement" measured against it would partly be
        /// warmup wearing off.
        /// </summary>
        public string WarmupVerdict()
        {
            var runs = Session.Runs;
            if (runs.Count < 3) return "not enough runs to judge warmup adequacy (need ≥3)";

            var rest = new List<BenchmarkRun>();
            for (int i = 1; i < runs.Count; i++) rest.Add(runs[i]);

            var restSpread = BenchmarkStats.SpreadOf(rest, BenchmarkMetricKeys.FrameTimeMs);
            double first = BenchmarkStats.Value(runs[0].Get(BenchmarkMetricKeys.FrameTimeMs),
                                                BenchmarkStats.Headline(BenchmarkMetricKeys.FrameTimeMs));

            if (!restSpread.HasData || double.IsNaN(first) || Math.Abs(restSpread.Mean) < double.Epsilon)
                return "frame time unavailable — cannot judge warmup adequacy";

            double diffPct = (first - restSpread.Mean) / Math.Abs(restSpread.Mean) * 100.0;
            double bandPct = BenchmarkStats.NoiseSigmas * restSpread.Cv * 100.0;

            return Math.Abs(diffPct) <= Math.Max(bandPct, BenchmarkStats.MinReportableDeltaPercent)
                ? $"OK — run 1 is within noise of the rest ({diffPct:+0.0;-0.0;0.0}%, band ±{bandPct:0.0}%)"
                : $"TOO SHORT — run 1 differs from the rest by {diffPct:+0.0;-0.0}% (band ±{bandPct:0.0}%); increase warmup";
        }

        public string ToLogLine()
        {
            var ft = Find(BenchmarkMetricKeys.FrameTimeMs);
            int stable = 0, marginal = 0, unstable = 0;
            foreach (var s in Spreads)
            {
                switch (s.Stability)
                {
                    case MetricStability.Stable: stable++; break;
                    case MetricStability.Marginal: marginal++; break;
                    case MetricStability.Unstable: unstable++; break;
                }
            }
            return $"PERFLINT_BENCH: OK runs={Session.Runs.Count} " +
                   $"frameTimeCv={(ft.HasData ? (ft.Cv * 100).ToString("0.0", CultureInfo.InvariantCulture) : "n/a")}% " +
                   $"stable={stable} marginal={marginal} unstable={unstable}";
        }

        public string ToReport(string failureNote)
        {
            var sb = new StringBuilder();
            var fp = Session.Fingerprint;

            sb.AppendLine("PerfLint benchmark repeatability study");
            sb.AppendLine("======================================");
            sb.AppendLine($"session      : {Session.Id}");
            sb.AppendLine($"scene        : {Session.Spec?.scenePath ?? "(unknown)"}");
            sb.AppendLine($"runs         : {Session.Runs.Count}" +
                          (Session.Spec != null ? $" of {Session.Spec.repetitions} requested" : ""));
            if (Session.Spec != null)
                sb.AppendLine($"window       : {Session.Spec.warmupSeconds:0.#}s warmup + {Session.Spec.sampleSeconds:0.#}s sample, camera held still");
            if (fp != null)
            {
                sb.AppendLine($"editor       : Unity {fp.unityVersion} · {fp.buildTarget} · {fp.renderPipeline}");
                sb.AppendLine($"conditions   : quality \"{fp.qualityName}\" · vSync {fp.vSyncCount} · targetFrameRate {fp.targetFrameRate} · game view {fp.screenWidth}x{fp.screenHeight}");
                var warn = fp.UsabilityWarning();
                if (!string.IsNullOrEmpty(warn)) sb.AppendLine($"WARNING      : {warn}");
                // Deep Profile is no longer a usability refusal — it is a trade, and the report has to name it or a
                // reader takes the inflated milliseconds at face value.
                var inflated = fp.TimingsInflatedWarning();
                if (!string.IsNullOrEmpty(inflated)) sb.AppendLine($"WARNING      : {inflated}");
            }
            if (!string.IsNullOrEmpty(failureNote))
                sb.AppendLine($"INCOMPLETE   : {failureNote}");
            sb.AppendLine();

            // Per-run headline values, so an outlier run is visible rather than just averaged away.
            sb.AppendLine("Per-run headline values");
            sb.AppendLine("-----------------------");
            sb.Append("run".PadRight(6)).Append("frames".PadLeft(8))
              .Append("frameMs(p50)".PadLeft(14)).Append("gpuMs(p50)".PadLeft(12))
              .Append("gcB/frame".PadLeft(12)).Append("memPeakMB".PadLeft(12)).Append("draws".PadLeft(9)).AppendLine();
            for (int i = 0; i < Session.Runs.Count; i++)
            {
                var r = Session.Runs[i];
                sb.Append((i + 1).ToString().PadRight(6))
                  .Append(r.frameCount.ToString().PadLeft(8))
                  .Append(Cell(r, BenchmarkMetricKeys.FrameTimeMs, 14, "0.00"))
                  .Append(Cell(r, BenchmarkMetricKeys.GpuFrameTimeMs, 12, "0.00"))
                  .Append(Cell(r, BenchmarkMetricKeys.GcPerFrameBytes, 12, "0"))
                  .Append(CellMb(r, BenchmarkMetricKeys.TotalMemoryBytes, 12))
                  .Append(Cell(r, BenchmarkMetricKeys.DrawCalls, 9, "0"))
                  .AppendLine();
            }
            sb.AppendLine();

            sb.AppendLine("Run-to-run spread (nothing was changed between runs)");
            sb.AppendLine("----------------------------------------------------");
            sb.Append("metric".PadRight(26)).Append("n".PadLeft(4)).Append("mean".PadLeft(16))
              .Append("sd".PadLeft(14)).Append("cv%".PadLeft(9)).Append("min".PadLeft(16))
              .Append("max".PadLeft(16)).Append("  verdict").AppendLine();
            foreach (var s in Spreads)
            {
                sb.Append(BenchmarkMetricKeys.Label(s.Key).PadRight(26))
                  .Append(s.N.ToString().PadLeft(4))
                  .Append(Num(s.Mean).PadLeft(16))
                  .Append(Num(s.StdDev).PadLeft(14))
                  .Append((s.Cv * 100).ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9))
                  .Append(Num(s.Min).PadLeft(16))
                  .Append(Num(s.Max).PadLeft(16))
                  .Append("  ").Append(Verdict(s))
                  .AppendLine();
            }
            sb.AppendLine();

            sb.AppendLine($"Warmup adequacy : {WarmupVerdict()}");
            sb.AppendLine();

            sb.AppendLine("What this means");
            sb.AppendLine("---------------");
            sb.AppendLine($"  STABLE   (cv <= {BenchmarkStats.StableCvMax * 100:0}%)  — a ~10% change is comfortably detectable; report deltas plainly.");
            sb.AppendLine($"  MARGINAL (cv <= {BenchmarkStats.MarginalCvMax * 100:0}%) — only large changes are distinguishable; always show the noise band.");
            sb.AppendLine($"  UNSTABLE (cv >  {BenchmarkStats.MarginalCvMax * 100:0}%) — dominated by noise; do NOT report before/after deltas for this metric.");
            sb.AppendLine();
            sb.AppendLine("  Frame time is machine-specific and never transfers to a device — it is only ever a relative,");
            sb.AppendLine("  editor-side change. For memory, GC volume and per-frame call counts it is the IMPROVEMENT");
            sb.AppendLine("  that transfers: those are properties of the content and the code, so a reduction measured");
            sb.AppendLine("  here is expected to hold on the target device.");
            sb.AppendLine();
            sb.AppendLine("  The ABSOLUTE figures transfer in neither case. Memory sampled in Play Mode includes the");
            sb.AppendLine("  editor itself (whole gigabytes of it), so it must never be checked against a device memory");
            sb.AppendLine("  budget — only its before/after difference is meaningful.");

            return sb.ToString();
        }

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("run,metric,avg,min,max,median,p95,sampleCount");
            foreach (var r in Session.Runs)
            {
                if (r.metrics == null) continue;
                foreach (var m in r.metrics)
                {
                    if (m == null || !m.hasData) continue;
                    sb.Append(r.index + 1).Append(',').Append(m.key).Append(',')
                      .Append(Inv(m.avg)).Append(',').Append(Inv(m.min)).Append(',').Append(Inv(m.max)).Append(',')
                      .Append(Inv(m.median)).Append(',').Append(Inv(m.p95)).Append(',').Append(m.sampleCount)
                      .AppendLine();
                }
            }
            return sb.ToString();
        }

        static string Verdict(BenchmarkStats.Spread s) => s.Stability switch
        {
            MetricStability.Stable => "STABLE",
            MetricStability.Marginal => "MARGINAL",
            MetricStability.Unstable => "UNSTABLE",
            _ => "n/a"
        };

        static string Cell(BenchmarkRun r, string key, int width, string format)
        {
            var m = r.Get(key);
            double v = BenchmarkStats.Value(m, BenchmarkStats.Headline(key));
            return (double.IsNaN(v) ? "n/a" : v.ToString(format, CultureInfo.InvariantCulture)).PadLeft(width);
        }

        static string CellMb(BenchmarkRun r, string key, int width)
        {
            var m = r.Get(key);
            double v = BenchmarkStats.Value(m, BenchmarkStats.Headline(key));
            return (double.IsNaN(v) ? "n/a" : (v / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)).PadLeft(width);
        }

        static string Num(double v) =>
            Math.Abs(v) >= 100000 ? v.ToString("0", CultureInfo.InvariantCulture)
                                  : v.ToString("0.###", CultureInfo.InvariantCulture);

        static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
