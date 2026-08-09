using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Persists the last runtime sampling session to <c>Library/PerfLint/last-runtime.json</c>.
    ///
    /// Why this has to exist: entering and leaving Play Mode costs two domain reloads, and a
    /// <see cref="RuntimeProfileResult"/> is an ordinary managed object — so until now every measurement was
    /// destroyed on the way out of the very Play Mode session that produced it. That is the physical reason the
    /// runtime panel could never feed the main report, the health score, an export, or a before/after comparison:
    /// not a missing feature, an architecturally impossible one.
    ///
    /// Unlike <see cref="ScanResultStore"/>, this round-trip is LOSSLESS for the findings: RuntimeAnalyzer never
    /// attaches an <see cref="IFix"/> or a <see cref="FindingAction"/> to a RUN.* finding (runtime diagnoses,
    /// it does not repair), so there is no unserializable delegate to lose. Only <see cref="Finding.Ping"/> is
    /// rebuilt generically from the code location, exactly as ScanResultStore does.
    ///
    /// Metrics are stored as <see cref="BenchmarkMetric"/> — the same DTO the benchmark loop uses — so a sampling
    /// session and a benchmark run speak identical units and can be compared without a conversion layer.
    /// </summary>
    public static class RuntimeSessionStore
    {
        /// <summary>A restored sampling session: the diagnoses, the measured numbers, and the conditions they were taken under.</summary>
        public sealed class Session
        {
            public IReadOnlyList<Finding> Findings { get; }
            public IReadOnlyList<BenchmarkMetric> Metrics { get; }
            public DateTime CapturedAtUtc { get; }
            public double DurationSeconds { get; }
            public int FrameCount { get; }
            /// <summary>Scene names loaded at sampling time. Used to tell the user when the measurement no longer describes what is on screen.</summary>
            public IReadOnlyList<string> Scenes { get; }
            /// <summary>Scene the sample started in, or null when unrecorded. See <see cref="DescribesScene"/>.</summary>
            public string StartScene { get; }

            /// <summary>Deep Profile inflates main-thread time several-fold — a session recorded under it is a localization aid, not a frame-time measurement.</summary>
            public bool WasDeepProfile { get; }

            /// <summary>
            /// These findings were produced by a different build of PerfLint than the one running now.
            ///
            /// Not an error and not a reason to hide anything — the measurement is as valid as it ever was. It means
            /// the WORDING and the THRESHOLDS are the old build's, because findings are stored verbatim and loading
            /// does not re-run the analyzer. Without saying so, an updated PerfLint looks like it changed nothing.
            /// False for sessions written before stamping existed: unknown is not the same as different.
            /// </summary>
            public bool FromDifferentBuild { get; }

            /// <summary>
            /// Top markers by main-thread self time, with their sampled-frame hit rates. Empty when the merge did not
            /// run — which <see cref="HotspotsMerged"/>, not the emptiness, is how you tell.
            /// </summary>
            public IReadOnlyList<Hotspot> Hotspots { get; }

            /// <summary>Whether hotspot attribution ran for this session. False on a session stored before hotspots were persisted, and on one whose merge failed.</summary>
            public bool HotspotsMerged { get; }

            /// <summary>QualitySettings.vSyncCount at sampling time. Anything above 0 means frame time was quantised to the display refresh, not free-running.</summary>
            public int VSyncCount { get; }
            /// <summary>Application.targetFrameRate at sampling time; -1 = uncapped.</summary>
            public int TargetFrameRate { get; }

            /// <summary>
            /// Whether the frame rate was being held down while this was recorded. Such a reading measures the cap,
            /// not the machine — the same scene read 16.68 ms with VSync on and 5.4 ms with it off.
            /// </summary>
            public bool FrameRateCapped => VSyncCount > 0 || TargetFrameRate > 0;

            public Session(IReadOnlyList<Finding> findings, IReadOnlyList<BenchmarkMetric> metrics,
                DateTime capturedAtUtc, double durationSeconds, int frameCount,
                IReadOnlyList<string> scenes, bool wasDeepProfile,
                int vSyncCount = 0, int targetFrameRate = -1,
                IReadOnlyList<Hotspot> hotspots = null, bool hotspotsMerged = false,
                string activeScene = null, bool fromDifferentBuild = false, string startScene = null)
            {
                StartScene = startScene;
                FromDifferentBuild = fromDifferentBuild;
                VSyncCount = vSyncCount;
                TargetFrameRate = targetFrameRate;
                Findings = findings ?? Array.Empty<Finding>();
                Metrics = metrics ?? Array.Empty<BenchmarkMetric>();
                CapturedAtUtc = capturedAtUtc;
                DurationSeconds = durationSeconds;
                FrameCount = frameCount;
                Scenes = scenes ?? Array.Empty<string>();
                WasDeepProfile = wasDeepProfile;
                Hotspots = hotspots ?? Array.Empty<Hotspot>();
                HotspotsMerged = hotspotsMerged;
                ActiveScene = activeScene;
            }

            public BenchmarkMetric Get(string key)
            {
                foreach (var m in Metrics)
                    if (m != null && string.Equals(m.key, key, StringComparison.Ordinal)) return m;
                return null;
            }

            /// <summary>Median-derived FPS; 0 when frame time was unavailable. Median rather than mean so one hitch does not define "how it runs".</summary>
            public double MedianFps
            {
                get
                {
                    var ft = Get(BenchmarkMetricKeys.FrameTimeMs);
                    return (ft != null && ft.hasData && ft.median > 0) ? 1000.0 / ft.median : 0;
                }
            }

            /// <summary>Order-insensitive key for the sampled scene set, comparable with <see cref="SceneKey"/> of the currently loaded scenes.</summary>
            public string SceneKeyValue => SceneKey(Scenes);

            /// <summary>
            /// Whether this measurement still describes what the editor currently has open. A session taken in a
            /// different scene must not be presented as if it described this one — the same comparability rule the
            /// benchmark fingerprint enforces, applied to the panel.
            /// </summary>
            /// <summary>
            /// The scene that was actually running when this was measured, or null for sessions recorded before it
            /// was kept. Additively loaded scenes are listed in <see cref="Scenes"/> but only one is ever active.
            /// </summary>
            public string ActiveScene { get; }

            /// <summary>
            /// Whether this measurement describes the scene everything on screen is currently about.
            ///
            /// The subject is <see cref="SceneInQuestion"/>, NOT the editor's active scene, and the difference is
            /// the whole bug: on a project that boots through an entry scene the editor sits on that entry scene
            /// permanently, so asking "is this measurement about what I have open" answers "no" forever — while the
            /// header, which does know about scene plans, shows the same measurement as current. One screen, two
            /// opposite claims, seen live.
            ///
            /// The list is still taken, and still used, by the fallback for sessions recorded before the active
            /// scene was kept.
            /// </summary>
            public bool DescribesScenes(IReadOnlyList<string> loadedSceneNames) =>
                DescribesScene(SceneInQuestion(), loadedSceneNames);

            /// <summary>
            /// Whether this measurement describes what is running now.
            ///
            /// Compares the ACTIVE scene, not the set of loaded ones. A project that loads several scenes additively
            /// and runs one at a time — the URP sample does exactly this, and Tim confirmed only one is ever really
            /// running — records four names while the editor has one open, so a set comparison never matches and
            /// every measurement is thrown away as "taken somewhere else". The measurement was of the running scene;
            /// the others were loaded around it.
            ///
            /// Sessions recorded before the active scene was kept fall back to the old set comparison, so an existing
            /// measurement does not silently change meaning.
            /// </summary>
            public bool DescribesScene(string activeSceneNow, IReadOnlyList<string> loadedSceneNames) =>
                !string.IsNullOrEmpty(ActiveScene) && !string.IsNullOrEmpty(activeSceneNow)
                    // Either END of the run counts. A sample that begins in an entry scene and ends in the one it
                    // loads describes BOTH: press play from the entry scene again and you get this measurement back.
                    // Matching only the end told everyone parked on their entry scene — the normal place to park —
                    // that the only measurement on record was taken somewhere else, which was true and useless.
                    // Seen on a museum project: Init loads hnmz-overview, a button loads hnmz-enterprise, and every
                    // visit to Init claimed nothing had ever measured it.
                    ? string.Equals(ActiveScene, activeSceneNow, StringComparison.Ordinal)
                      || (!string.IsNullOrEmpty(StartScene) && string.Equals(StartScene, activeSceneNow, StringComparison.Ordinal))
                    : string.Equals(SceneKeyValue, SceneKey(loadedSceneNames), StringComparison.Ordinal);

            static string ActiveSceneNow() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            /// <summary>The scene being reasoned about right now — delegated so this and <see cref="ScenesInScope"/> cannot drift apart.</summary>
            static string SceneInQuestion() => RuntimeSessionStore.SceneInQuestion();

            /// <summary>The scenes that were loaded around the active one, for display. Empty when there were none.</summary>
            public List<string> AlsoLoaded()
            {
                var rest = new List<string>();
                if (Scenes == null) return rest;
                foreach (var n in Scenes)
                    if (!string.Equals(n, ActiveScene, StringComparison.Ordinal)) rest.Add(n);
                return rest;
            }

            /// <summary>
            /// The measured facts, as the plain numbers a ranking may reason from.
            ///
            /// GPU reliability is decided here, by the same test RuntimeAnalyzer applies: more GPU timings than
            /// frames proves the readings were duplicated rather than measured once per frame. Passing that flag
            /// along is what stops "0.2 ms" from quietly becoming "the GPU is idle, go optimize your scripts".
            /// </summary>
            public PerfMeasurement ToMeasurement()
            {
                // The GAME's frame time, not the editor's whole frame — and this is the one place that decides it for
                // every screen the Autopilot draws.
                //
                // In the Editor the main thread runs EditorLoop and PlayerLoop in the same frame, and EditorLoop is
                // the editor drawing its own windows. On the reference project: 3.775 ms whole frame vs 2.084 ms game,
                // so 1.69 ms — 81% on top — of work no build performs. Fed to a 240 FPS budget those two numbers
                // disagree about every conclusion downstream: 90.6% of budget ("sitting on the line", CPU-bound,
                // ranking led by shader and script rules) versus 50.0% ("inside it", ranking led by build size). The
                // whole verdict rested on 0.6% of headroom that belonged to the editor's own UI.
                //
                // RUN.FPS001 has always done this (RuntimeAnalyzer, with the same sample-count guard, and a comment
                // that says judging a project by work it does not ship "is the opposite of the job"). Its regression
                // test pins it — on RUN.FPS001. Nothing pinned it HERE, so the same mistake lived twice in one package
                // with the suite green. The new assertion is on PerfMeasurement for exactly that reason.
                //
                // DELIBERATELY UNLIKE RuntimeAnalyzer: when the game-side series is unusable, that one falls back to
                // the whole frame because a finding is better than silence. This does NOT, because falling back here
                // is indistinguishable from the bug being fixed — it would quietly restore editor-wide judging for
                // every screen. The figure is still carried (it is real, and worth showing), flagged as unjudgeable.
                var game = Get(BenchmarkMetricKeys.GameFrameTimeMs);
                bool gameSide = game != null && game.hasData && game.sampleCount >= RuntimeAnalyzer.MinGameFrameSamples;

                var frame = gameSide ? game : Get(BenchmarkMetricKeys.FrameTimeMs);
                if (frame == null || !frame.hasData) return PerfMeasurement.None;

                var gpu = Get(BenchmarkMetricKeys.GpuFrameTimeMs);
                bool gpuReliable = gpu != null && gpu.hasData
                                   && FrameCount > 0 && gpu.sampleCount <= FrameCount * 1.2;

                var gc = Get(BenchmarkMetricKeys.GcPerFrameBytes);
                var mem = Get(BenchmarkMetricKeys.TotalMemoryBytes);
                var draw = Get(BenchmarkMetricKeys.DrawCalls);

                // Net growth, NOT the peak-to-trough swing: a healthy GC sawtooth makes max−min large while nothing
                // is actually leaking. Uses the same first-half/second-half trend RUN.MEM001 reports, so the card
                // and the finding can never quote different numbers for the same thing.
                double growth = mem != null && mem.hasData ? mem.TrendDelta : 0;

                return new PerfMeasurement(
                    frameMsMedian: frame.median,
                    gpuMsMedian: gpuReliable ? gpu.median : 0,
                    gpuReliable: gpuReliable,
                    frameRateCapped: FrameRateCapped,
                    // Deep Profile sessions are now a legitimate measurement — they are the only way to get
                    // per-method markers — so the panel has to be told that their wall-clock figures are the
                    // profiler's, or it would judge a comfortable project as missing its target several times over.
                    timingsInflated: WasDeepProfile,
                    gcBytesPerFrame: gc != null && gc.hasData ? gc.avg : 0,
                    memoryPeakBytes: mem != null && mem.hasData ? mem.max : 0,
                    memoryGrowthBytes: growth,
                    drawCalls: draw != null && draw.hasData ? draw.avg : 0,
                    // Only the frame-budget question is refused. GC, memory growth and draw calls are unaffected by
                    // which of the two frame series this is — throwing the whole measurement away would take the GC
                    // attribution and the memory-growth verdict down with a frame-time problem they do not share.
                    frameTimeIsEditorWide: !gameSide);
            }

            /// <summary>
            /// Renders this session into the report's plain-data form. Only metrics that actually have data are
            /// emitted — an absent counter (platform/version dependent) must read as absent, never as a zero, which
            /// in a performance report would look like a free win.
            /// </summary>
            public RuntimeEvidence ToEvidence()
            {
                var rows = new List<RuntimeEvidence.Row>();

                void Time(string key, string label)
                {
                    var m = Get(key);
                    if (m == null || !m.hasData) return;
                    rows.Add(new RuntimeEvidence.Row(label,
                        L.Tr($"{m.median:0.00} ms median · {m.p95:0.00} ms p95", $"中位 {m.median:0.00} ms · p95 {m.p95:0.00} ms")));
                }

                void Bytes(string key, string label, bool perFrame = false)
                {
                    var m = Get(key);
                    if (m == null || !m.hasData) return;
                    rows.Add(new RuntimeEvidence.Row(label, perFrame
                        ? L.Tr($"{ScannerUtil.Human((long)m.avg)} avg/frame · {ScannerUtil.Human((long)m.max)} peak",
                               $"平均 {ScannerUtil.Human((long)m.avg)}/帧 · 峰值 {ScannerUtil.Human((long)m.max)}")
                        : L.Tr($"{ScannerUtil.Human((long)m.max)} peak · {ScannerUtil.Human((long)m.avg)} avg",
                               $"峰值 {ScannerUtil.Human((long)m.max)} · 平均 {ScannerUtil.Human((long)m.avg)}")));
                }

                void Count(string key, string label)
                {
                    var m = Get(key);
                    if (m == null || !m.hasData) return;
                    rows.Add(new RuntimeEvidence.Row(label,
                        L.Tr($"{m.avg:0} avg · {m.max:0} peak", $"平均 {m.avg:0} · 峰值 {m.max:0}")));
                }

                var fps = MedianFps;
                if (fps > 0)
                    rows.Add(new RuntimeEvidence.Row(L.Tr("Frame rate", "帧率"),
                        L.Tr($"{fps:0} FPS (median, this machine)", $"{fps:0} FPS（中位数，本机口径）")));

                Time(BenchmarkMetricKeys.FrameTimeMs, L.Tr("Main thread", "主线程"));
                Time(BenchmarkMetricKeys.GpuFrameTimeMs, L.Tr("GPU", "GPU"));
                Bytes(BenchmarkMetricKeys.GcPerFrameBytes, L.Tr("GC allocation", "GC 分配"), perFrame: true);
                Bytes(BenchmarkMetricKeys.TotalMemoryBytes, L.Tr("Total used memory", "已用内存总量"));
                Bytes(BenchmarkMetricKeys.GcUsedBytes, L.Tr("Managed heap", "托管堆"));
                Bytes(BenchmarkMetricKeys.GfxUsedBytes, L.Tr("Graphics memory", "图形内存"));
                Count(BenchmarkMetricKeys.DrawCalls, L.Tr("Draw calls", "Draw Call"));
                Count(BenchmarkMetricKeys.SetPassCalls, L.Tr("SetPass calls", "SetPass"));
                Count(BenchmarkMetricKeys.Triangles, L.Tr("Triangles", "三角形"));

                return new RuntimeEvidence(
                    CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    DurationSeconds,
                    FrameCount,
                    string.Join(", ", Scenes),
                    WasDeepProfile,
                    rows);
            }
        }

        static string FilePath
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint", "last-runtime.json");
            }
        }

        /// <summary>Order-insensitive comparison key for a set of scene names.</summary>
        public static string SceneKey(IReadOnlyList<string> names) =>
            names == null ? "" : string.Join("|", names.OrderBy(n => n ?? "", StringComparer.Ordinal));

        /// <summary>Scene names currently loaded in the editor. Raw names — localization belongs to the display layer.</summary>
        /// <summary>
        /// The scenes a measurement has to be about for it to count right now.
        ///
        /// The loaded ones, until a scene plan says otherwise. With a plan, the editor's open scene answers a
        /// different question — that is the entire reason plans exist: the game boots through Init, the numbers are
        /// about the level it loads, and the editor sits on Init the whole time.
        ///
        /// Missing this is what put two contradictory sentences on one screen: the header said
        /// "hnmz-enterprise · 3.87 ms · re-measured" (the baseline knows about plans) while the body said "no runtime
        /// measurement yet — the only one on record was taken in hnmz-enterprise, so nothing below has seen THIS
        /// scene run" (this side did not). Both were describing the same measurement, of the scene the plan names,
        /// on a project whose editor was parked on Init. <see cref="BenchmarkVerifyState.BaselineDescribesSceneToMeasure"/>
        /// learned this rule when plans were added; every other reader of "does this measurement apply" was left
        /// comparing against the open scene.
        ///
        /// The rule for WHICH scene a plan means lives in <see cref="BenchmarkVerifyState.PlannedSceneGuid"/> and is
        /// called rather than repeated — target when there is one, else the start scene.
        /// </summary>
        public static List<string> ScenesInScope()
        {
            string planned = PlannedSceneName();
            // A plan naming a scene whose asset is gone has no scene to scope to. Falling through to the open scenes
            // is right: the plan cannot run either, and the panels say so in their own words.
            return string.IsNullOrEmpty(planned) ? LoadedSceneNames() : new List<string> { planned };
        }

        /// <summary>
        /// The one scene everything on screen is currently about: the plan's target when there is one, else whatever
        /// the editor has active.
        ///
        /// This is the value <see cref="Session.DescribesScenes"/> tests against, and getting it from here rather
        /// than from SceneManager is the fix for a screen that contradicted itself. Passing a scene LIST was not
        /// enough — the modern branch of that test never looked at the list, so a correction that only changed the
        /// argument changed nothing at all. Live proof it changed nothing: the panel went on saying "the only
        /// measurement on record was taken in hnmz-enterprise, so nothing below has seen this scene run" about a
        /// measurement of hnmz-enterprise, on a project whose plan names hnmz-enterprise.
        /// </summary>
        public static string SceneInQuestion()
        {
            string planned = PlannedSceneName();
            return string.IsNullOrEmpty(planned)
                ? UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                : planned;
        }

        /// <summary>
        /// Name of the scene the plan aims at, or "" when there is no plan or its scene is gone.
        ///
        /// Which scene a plan MEANS is <see cref="BenchmarkVerifyState.PlannedSceneGuid"/>'s rule (target when there
        /// is one, else the start scene) and is called rather than repeated.
        /// </summary>
        static string PlannedSceneName()
        {
            string guid = BenchmarkVerifyState.PlannedSceneGuid();
            return string.IsNullOrEmpty(guid) ? "" : BenchmarkScenePlan.NameOf(BenchmarkScenePlan.PathOf(guid));
        }

        public static List<string> LoadedSceneNames()
        {
            var loaded = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                loaded.Add(string.IsNullOrEmpty(sc.name) ? "(untitled)" : sc.name);
            }
            return loaded;
        }

        /// <summary>
        /// Persists a completed sampling session. Any IO/serialization failure is swallowed — like the scan store,
        /// persistence is an optimization and must never take down the sampling path that produced the data.
        /// </summary>
        public static void Save(RuntimeProfileResult result, IReadOnlyList<Finding> findings, IReadOnlyList<string> scenes = null,
            string startScene = null)
        {
            if (result == null) return;
            try
            {
                var dto = new Dto
                {
                    capturedTicks = DateTime.UtcNow.Ticks,
                    durationSeconds = result.DurationSeconds,
                    frameCount = result.FrameCount,
                    wasDeepProfile = result.WasDeepProfile,
                    // Recorded because a capped frame rate makes the whole "are you hitting your target" question
                    // unanswerable, and without these two values there is no way to tell afterwards that it was.
                    vSyncCount = QualitySettings.vSyncCount,
                    targetFrameRate = Application.targetFrameRate,
                    scenes = (scenes ?? LoadedSceneNames()).ToArray(),
                    // The scene that was RUNNING. Everything else in "scenes" was loaded around it, and matching on
                    // the whole set threw away every measurement in a project that loads additively.
                    activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    // Where the sample BEGAN, which is not always where it ended: an entry scene that loads the real
                    // one at runtime (Init -> the actual content) ends elsewhere, and judging "does this measurement
                    // describe what I have open" on the end alone told everyone parked on their entry scene that the
                    // measurement was taken somewhere else. It was taken somewhere else — starting from here.
                    startScene = startScene,
                    // Findings are written once and read back verbatim — loading never re-runs the analyzer. So the
                    // wording, thresholds and advice in a stored session are those of the build that produced it, and
                    // after an update the panel shows the old ones until the next sample. Stamping the build is what
                    // lets it say so instead of looking like the update did nothing.
                    perfLintVersion = PerfLint.Core.PerfLintBuildStamp.Version,
                    perfLintAssemblyTicks = PerfLint.Core.PerfLintBuildStamp.AssemblyWrittenAtUtcTicks,
                    metrics = BenchmarkRun.MetricsFrom(result),
                    // Stored so a hotspot survives the domain reload that ends Play Mode, which is what lets a later
                    // measurement be compared against it call path by call path. hotspotsAvailable is kept separate
                    // from the list being empty: "the merge did not run" and "the merge found nothing" are different
                    // facts, and only the second one may ever be read as an improvement.
                    hotspotsAvailable = result.HotspotsAvailable,
                    hotspots = BenchmarkRun.HotspotsFrom(result),
                    findings = (findings ?? Array.Empty<Finding>()).Where(f => f != null).Select(ToDto).ToArray()
                };

                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(dto));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"Failed to persist the runtime session (does not affect usage): {ex.Message}",
                    $"运行时采样结果持久化失败（不影响使用）：{ex.Message}"));
            }
        }

        /// <summary>Restores the last sampling session, or null when there is none / it cannot be parsed.</summary>
        public static Session Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return null;

                var dto = JsonUtility.FromJson<Dto>(File.ReadAllText(path));
                if (dto == null) return null;

                var findings = (dto.findings ?? Array.Empty<FindingDto>()).Select(FromDto).ToList();
                var hotspots = new List<Hotspot>();
                foreach (var h in dto.hotspots ?? Array.Empty<BenchmarkHotspot>())
                    if (h != null) hotspots.Add(h.ToHotspot());

                return new Session(
                    findings,
                    dto.metrics ?? Array.Empty<BenchmarkMetric>(),
                    new DateTime(dto.capturedTicks, DateTimeKind.Utc),
                    dto.durationSeconds,
                    dto.frameCount,
                    dto.scenes ?? Array.Empty<string>(),
                    dto.wasDeepProfile,
                    dto.vSyncCount,
                    // Sessions written before this field existed deserialize as 0, which would read as "capped at
                    // 0 fps". Treat 0 as the uncapped sentinel Unity itself uses (-1).
                    dto.targetFrameRate == 0 ? -1 : dto.targetFrameRate,
                    hotspots,
                    dto.hotspotsAvailable,
                    // Null for sessions written before this existed — DescribesScene falls back to the old set
                    // comparison for those rather than silently changing what they claim.
                    string.IsNullOrEmpty(dto.activeScene) ? null : dto.activeScene,
                    PerfLint.Core.PerfLintBuildStamp.DiffersFrom(dto.perfLintVersion, dto.perfLintAssemblyTicks),
                    string.IsNullOrEmpty(dto.startScene) ? null : dto.startScene);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"Failed to restore the runtime session (treating as not sampled): {ex.Message}",
                    $"运行时采样结果恢复失败（将作未采样处理）：{ex.Message}"));
                return null;
            }
        }

        public static bool Exists()
        {
            try { return File.Exists(FilePath); }
            catch { return false; }
        }

        /// <summary>
        /// Whether a session may contribute to the figures shown for the given loaded scenes: it must exist, have
        /// found something, and have been recorded in those same scenes. A measurement from elsewhere is still worth
        /// keeping and mentioning — it just doesn't get to move the numbers.
        /// </summary>
        public static bool Applies(Session session, IReadOnlyList<string> loadedSceneNames) =>
            session != null && session.Findings.Count > 0 && session.DescribesScenes(loadedSceneNames);

        /// <summary>
        /// The result to DISPLAY: the static scan plus a runtime measurement that applies. Returns the scan unchanged
        /// when it doesn't.
        ///
        /// Display only, and that is a hard rule rather than a preference: every mutating path — ScanRunner's
        /// RescanRules / RescanFile, Fix All, OptimizePlan — rebuilds findings from the scanners, and no scanner can
        /// rebuild a RUN.* finding. Anything merged into the stored result would therefore be dropped, silently, by
        /// the next incremental rescan.
        ///
        /// ScannerRuleMap is carried over unchanged: RUN.* rules have no owning scanner, and RescanRules treats an
        /// unmapped rule as "no owners → return previous", so leaving them out is the correct no-op.
        /// </summary>
        public static ScanResult Merge(ScanResult scan, Session session, IReadOnlyList<string> loadedSceneNames)
        {
            if (scan == null) return null;
            if (!Applies(session, loadedSceneNames)) return scan;

            var merged = new List<Finding>(scan.Findings.Count + session.Findings.Count);
            merged.AddRange(scan.Findings);
            merged.AddRange(session.Findings);
            return new ScanResult(merged, scan.Duration, scan.ScannerRuleMap, scan.CompletedAtUtc);
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* ignore */ }
        }

        // ── Serialization mapping ──────────────────────────────
        //
        // Deliberately a smaller DTO than ScanResultStore's: a RUN.* finding has no Fix, no Action and no savings
        // estimate, so carrying those fields would be storing "0" and "false" forever and inviting someone to
        // believe they mean something.

        static FindingDto ToDto(Finding f) => new FindingDto
        {
            ruleId = f.RuleId,
            domain = (int)f.Domain,
            severity = (int)f.Severity,
            title = f.Title,
            groupTitle = f.GroupTitle,
            detail = f.Detail,
            targetPath = f.TargetPath,
            codeFile = f.CodeFile,
            codeLine = f.CodeLine,
            locateTargets = LocateTargetsToDto(f)
        };

        /// <summary>
        /// Keeps the locate targets that named their objects, and drops the ones that did not.
        ///
        /// A target whose Ping is a closure with nothing to name cannot be rebuilt from disk, and writing its label
        /// alone would restore a button that does nothing — the failure this whole change exists to remove, in a new
        /// place. Only nameable targets are stored.
        /// </summary>
        static LocateTargetDto[] LocateTargetsToDto(Finding f)
        {
            if (f.LocateTargets == null || f.LocateTargets.Count == 0) return null;
            var kept = new List<LocateTargetDto>();
            foreach (var t in f.LocateTargets)
            {
                if (t.ObjectPaths == null || t.ObjectPaths.Count == 0) continue;
                kept.Add(new LocateTargetDto { label = t.Label, paths = new List<string>(t.ObjectPaths).ToArray() });
            }
            return kept.Count > 0 ? kept.ToArray() : null;
        }

        /// <summary>
        /// Rebuilds the locate targets, each selecting whatever of its objects is still in the open scenes.
        ///
        /// The scene will have been edited since the measurement — that is the normal case, not an error — so a
        /// target selects what it finds and says so when it finds nothing, rather than appearing to do nothing. The
        /// warning names the label, because "Rock_01_LOD0 (x30)" is what the reader clicked.
        /// </summary>
        static List<Finding.LocateTarget> LocateTargetsFromDto(LocateTargetDto[] dtos)
        {
            if (dtos == null || dtos.Length == 0) return null;
            var targets = new List<Finding.LocateTarget>(dtos.Length);
            foreach (var d in dtos)
            {
                if (d?.paths == null || d.paths.Length == 0) continue;
                var paths = d.paths;
                string label = d.label;
                targets.Add(new Finding.LocateTarget(label, () =>
                {
                    var found = ScannerUtil.FindByHierarchyPaths(paths);
                    if (found.Count == 0)
                    {
                        Debug.LogWarning("[PerfLint] " + L.Tr(
                            $"'{label}' was measured in a scene that has changed since — none of its {paths.Length} object(s) are in the open scene(s) now. Sample again to refresh.",
                            $"「{label}」测量时所在的场景此后已改动——它的 {paths.Length} 个对象目前都不在已打开的场景里。重新采样即可刷新。"));
                        return;
                    }
                    Selection.objects = found.ToArray();
                    EditorGUIUtility.PingObject(found[0]);
                }, paths));
            }
            return targets.Count > 0 ? targets : null;
        }

        static Finding FromDto(FindingDto d)
        {
            // Same generic Ping reconstruction as ScanResultStore: jump to the code location when there is one,
            // otherwise highlight the asset. Runtime hotspots are the case that matters — their whole value is
            // "click here to land on the script that is costing you the frame".
            Action ping = null;
            if (!string.IsNullOrEmpty(d.codeFile) && d.codeLine > 0)
            {
                string cf = d.codeFile; int cl = d.codeLine;
                ping = () => ScannerUtil.OpenScriptAtLine(cf, cl);
            }
            // "Assets/X.cs:42" — the same form the script findings use, and the reason a restored runtime finding
            // could name MouseLock.Update() in its title and then merely highlight the file when clicked: this store
            // was the one place that did not parse it.
            else if (ScannerUtil.TryParsePathLine(d.targetPath, out string sp, out int sl))
            {
                ping = () => ScannerUtil.OpenScriptAtLine(sp, sl);
            }
            // A bare script path — a session recorded before the line was resolved into the target. A script must be
            // OPENED, never merely pinged: highlighting a .cs file in the Project window and calling that "Locate" is
            // exactly the complaint. Opens at the top since the line isn't on disk; a fresh sample lands on the method.
            else if (!string.IsNullOrEmpty(d.targetPath) && d.targetPath.EndsWith(".cs", StringComparison.Ordinal))
            {
                string tp = d.targetPath;
                ping = () => ScannerUtil.OpenScript(tp, null);
            }
            else if (LooksLikeAssetPath(d.targetPath))
            {
                string tp = d.targetPath;
                ping = () => ScannerUtil.PingAsset(tp);
            }

            return new Finding(
                ruleId: d.ruleId,
                domain: (Domain)d.domain,
                severity: (Severity)d.severity,
                title: d.title,
                groupTitle: string.IsNullOrEmpty(d.groupTitle) ? null : d.groupTitle,
                detail: d.detail,
                targetPath: d.targetPath,
                ping: ping,
                codeFile: string.IsNullOrEmpty(d.codeFile) ? null : d.codeFile,
                codeLine: d.codeLine,
                locateTargets: LocateTargetsFromDto(d.locateTargets));
        }

        static bool LooksLikeAssetPath(string p) =>
            !string.IsNullOrEmpty(p) &&
            (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal));

        [Serializable]
        sealed class Dto
        {
            public long capturedTicks;
            public double durationSeconds;
            public int frameCount;
            public bool wasDeepProfile;
            public int vSyncCount;
            public int targetFrameRate;
            public string[] scenes;
            public string activeScene;
            /// <summary>Scene the sample STARTED in. Differs from activeScene whenever the entry scene loads the real one at runtime. Empty on sessions written before this existed.</summary>
            public string startScene;
            /// <summary>Which PerfLint build produced these findings. Empty/0 on sessions written before stamping existed — treated as unknown, not as different.</summary>
            public string perfLintVersion;
            public long perfLintAssemblyTicks;
            public BenchmarkMetric[] metrics;
            /// <summary>Whether the hotspot merge ran. Absent (false) on every session written before hotspots were persisted.</summary>
            public bool hotspotsAvailable;
            public BenchmarkHotspot[] hotspots;
            public FindingDto[] findings;
        }

        /// <summary>One locate target, flattened: the label and the scene paths it selects.</summary>
        [Serializable]
        sealed class LocateTargetDto
        {
            public string label;
            public string[] paths;
        }

        [Serializable]
        sealed class FindingDto
        {
            public string ruleId;
            public int domain;
            public int severity;
            public string title;
            public string groupTitle;
            public string detail;
            public string targetPath;
            public string codeFile;
            public int codeLine;
            public LocateTargetDto[] locateTargets;
        }
    }
}
