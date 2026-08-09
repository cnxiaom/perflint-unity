using System;
using System.Collections.Generic;
using System.IO;
using PerfLint.L10n;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Turning Deep Profile on or off, in one place.
    ///
    /// It lived inside the Runtime Profiler window, which was fine while that window held the only button. But the
    /// GC finding's own title says "enable Deep Profile to pinpoint the source" — the one action that unlocks
    /// function-level attribution — and a sentence naming an action the reader cannot take from where they are
    /// reading it is the trap this project keeps falling into. So the action moves somewhere both can call.
    ///
    /// The Play-Mode confirmation travels WITH it, because the cost is the surprising part: the toggle recompiles
    /// scripts, and a recompile cannot happen while playing, so saying yes ends the session being measured.
    /// </summary>
    public static class DeepProfileControl
    {
        public static bool Enabled => UnityEditorInternal.ProfilerDriver.deepProfiling;

        /// <summary>Sets it, after confirming when that would end a running session. Returns whether it was changed.</summary>
        public static bool Set(bool on)
        {
            if (Enabled == on) return false;

            if (UnityEditor.EditorApplication.isPlaying)
            {
                bool ok = UnityEditor.EditorUtility.DisplayDialog(
                    L.Tr("Deep Profile", "Deep Profile"),
                    on
                        ? L.Tr("Turning Deep Profile ON recompiles scripts and will exit the current Play Mode. Continue?\n\nDeep Profile refines CPU hotspots to specific script methods (ClassName.Method) but has high overhead — use it for localization, not for measuring real frame rate. After it's on, re-enter Play Mode and sample.", "开启 Deep Profile 需要重新编译脚本，会退出当前 Play Mode。继续？\n\nDeep Profile 能把 CPU 热点细化到具体脚本方法（ClassName.Method），但开销很大——仅用于定位、勿用于测真实帧率。开启后重新进入 Play Mode 采样。")
                        : L.Tr("Turning Deep Profile OFF recompiles scripts and will exit the current Play Mode. Continue?", "关闭 Deep Profile 需要重新编译脚本，会退出当前 Play Mode。继续？"),
                    L.Tr("Continue", "继续"), L.Tr("Cancel", "取消"));
                if (!ok) return false;
            }

            UnityEditorInternal.ProfilerDriver.deepProfiling = on;
            // The reload is what actually (un)injects the instrumentation. In Play Mode it also exits Play Mode, since
            // a domain reload cannot run while playing. The flag survives the reload.
            UnityEditor.EditorUtility.RequestScriptReload();
            return true;
        }
    }

    /// <summary>
    /// Drives N repetitions of a <see cref="BenchmarkSpec"/> through Play Mode without a human in the loop.
    ///
    /// Why this is a state machine and not a coroutine: a Play Mode round-trip costs TWO domain reloads
    /// (one entering, one exiting), and each of them destroys every managed object we are holding. So all
    /// state that has to outlive a phase boundary lives in <see cref="SessionState"/> (survives reloads,
    /// cleared on editor restart) or on disk, and the tick re-attaches itself from the
    /// <c>[InitializeOnLoad]</c> constructor after every reload. Each completed run is flushed to disk the
    /// moment it is collected — holding it in memory until the end would hand it straight to the reload
    /// that follows.
    ///
    /// The one window with no reload in it is Warmup → Sample → Collect: all three happen inside a single
    /// Play Mode session, which is what lets a single <see cref="RuntimeSampler"/> instance span them. If a
    /// script recompile forces a reload in the middle of that window the sampler is gone, and the run is
    /// failed rather than reported with a hole in it.
    ///
    /// Callers poll (<see cref="CurrentPhase"/> / <see cref="IsFinished"/>); there is deliberately no
    /// completion callback, because any delegate registered before the benchmark started has been wiped by
    /// the reloads long before it could fire.
    /// </summary>
    [InitializeOnLoad]
    public static class BenchmarkRunner
    {
        public enum Phase
        {
            Idle = 0,
            /// <summary>Opening the scene Play Mode has to boot from. A no-op when the plan does not name one.</summary>
            OpenScene,
            Prepare,
            EnterPlay,
            /// <summary>Playing, but not yet measuring: waiting for the scene the run is about to be loaded by the game itself.</summary>
            AwaitScene,
            Warmup,
            Sample,
            Collect,
            /// <summary>Replaying frame data to attribute the run's time to markers. Must finish before Play Mode ends — the reload on the way out would take it.</summary>
            Merge,
            ExitPlay,
            Settle,
            Done,
            Failed
        }

        const string LogTag = "[PerfLint Benchmark]";

        // ── Cross-reload state ────────────────────────────────
        const string KPhase = "PerfLint.Bench.Phase";
        const string KSpec = "PerfLint.Bench.Spec";
        const string KSessionId = "PerfLint.Bench.SessionId";
        const string KRunIndex = "PerfLint.Bench.RunIndex";
        const string KPhaseStart = "PerfLint.Bench.PhaseStart";
        const string KError = "PerfLint.Bench.Error";
        const string KVSyncSnapshot = "PerfLint.Bench.VSyncSnapshot"; // -1 = we did not change it
        const string KSceneDirtySnapshot = "PerfLint.Bench.SceneDirtySnapshot";
        const string KQuietSince = "PerfLint.Bench.QuietSince";       // timeSinceStartup of the last busy tick in Prepare
        // The scene Play Mode was actually in when this repetition's sampling window opened. SessionState rather than
        // a static because everything else in this machine is, for the same reason: a reload between Sample and
        // Collect would take a static with it and the run would be written claiming nothing was recorded.
        const string KSampledStartPath = "PerfLint.Bench.SampledStartPath";

        // The sampler only ever spans Warmup→Sample→Collect→Merge, which contains no domain reload — see the class remarks.
        static RuntimeSampler _sampler;

        // Set by the hotspot merge's completion callback, read by StepMerge on a later tick. Plain statics rather than
        // SessionState because the merge lives entirely inside one Play Mode session; if a reload does slip in, they
        // reset to false and the phase times out into "no hotspots for this run", which is the honest outcome.
        static bool _mergeDone;

        // Per-phase watchdogs (seconds). Anything that hangs — a scene that never finishes loading, a Play Mode
        // request the editor silently drops — must end as a reported failure with the project's settings restored,
        // never as an editor that sits in a half-configured state forever.
        const double PrepareTimeout = 30;
        const double OpenSceneTimeout = 120;
        const double EnterPlayTimeout = 180;
        /// <summary>
        /// Ceiling on waiting for the game to reach the scene being measured.
        ///
        /// Far longer than any other phase, and on purpose: this is the one phase whose length is set by the game and
        /// sometimes by the person playing it — a boot sequence with a splash and a menu, or a level that has to be
        /// selected by hand. A watchdog is still needed, because a target scene that is never reached (a wrong scene
        /// picked, a menu that needs a click nobody is there to give) must end as a named failure rather than an
        /// editor parked in Play Mode forever. Five minutes is long enough that it only fires when something is
        /// genuinely wrong, and the progress strip shows the clock the whole time so nobody has to guess.
        /// </summary>
        const double AwaitSceneTimeout = 300;
        const double CollectTimeout = 60;
        /// <summary>Ceiling on the hotspot merge. Generous because a Deep-Profile-sized frame is expensive to replay, but bounded: hotspots enrich a run, they are not what makes it valid.</summary>
        const double MergeTimeout = 45;
        const double ExitPlayTimeout = 180;
        const double SettleSeconds = 1.5;
        /// <summary>How long the editor must be free of compiles and imports before a run may start.</summary>
        const double QuietSeconds = 1.0;
        const double SettleTimeout = 60;
        /// <summary>Slack added on top of a phase's own intended duration before the watchdog fires.</summary>
        const double PhaseSlack = 60;

        static BenchmarkRunner()
        {
            // Re-arm after every domain reload, but only while a session is actually in flight.
            var p = CurrentPhase;
            if (p != Phase.Idle && p != Phase.Done && p != Phase.Failed)
                EditorApplication.update += Tick;

            // A setting we owe the user back but could not return yet. Re-armed here rather than by the session,
            // because the session is over by the time this can run — see RestoreSettings.
            if (SessionState.GetInt(KVSyncSnapshot, -1) >= 0)
                ArmPendingRestore();
        }

        // ── Public surface ────────────────────────────────────

        public static Phase CurrentPhase
        {
            get => (Phase)SessionState.GetInt(KPhase, (int)Phase.Idle);
            private set => SessionState.SetInt(KPhase, (int)value);
        }

        public static bool IsRunning
        {
            get { var p = CurrentPhase; return p != Phase.Idle && p != Phase.Done && p != Phase.Failed; }
        }

        public static bool IsFinished
        {
            get { var p = CurrentPhase; return p == Phase.Done || p == Phase.Failed; }
        }

        /// <summary>Machine-facing failure reason (also written into the session's status.json). Empty when there is none.</summary>
        public static string LastError => SessionState.GetString(KError, "");

        public static string CurrentSessionId => SessionState.GetString(KSessionId, "");

        /// <summary>0-based index of the repetition in flight.</summary>
        public static int CurrentRunIndex => SessionState.GetInt(KRunIndex, 0);

        /// <summary>One-line progress string for a status bar / CI log.</summary>
        public static string StatusLine()
        {
            var spec = LoadSpec();
            int total = spec?.repetitions ?? 0;
            return $"{CurrentPhase} run {CurrentRunIndex + 1}/{Math.Max(total, 1)}";
        }

        /// <summary>
        /// Everything needed to draw a progress readout, resolved in one place.
        ///
        /// It exists because the readout that matters most is the one nobody can reach: entering Play Mode brings the
        /// Game view forward, which hides the window that started the measurement behind it. So the numbers have to
        /// travel to wherever they can be shown, and they must be the same numbers everywhere — a strip saying "14s
        /// left" over a panel saying something else is worse than either alone.
        /// </summary>
        public readonly struct Progress
        {
            public Phase Phase { get; }
            /// <summary>1-based, for display.</summary>
            public int RunNumber { get; }
            public int Repetitions { get; }
            public double PhaseElapsedSeconds { get; }
            /// <summary>Seconds left in this phase, or NaN when the phase has no fixed length.</summary>
            public double PhaseRemainingSeconds { get; }
            /// <summary>
            /// Estimated seconds left in the whole session, or NaN when that cannot honestly be estimated.
            ///
            /// NaN while waiting for a scene, and that is the point: how long it takes to reach the level is set by
            /// the game, and sometimes by the person playing it. A countdown invented for that phase would be a
            /// guess presented as a measurement.
            /// </summary>
            public double TotalRemainingSeconds { get; }
            /// <summary>Name of the scene being waited for, empty when none is.</summary>
            public string TargetSceneName { get; }
            public bool IsMeasuring { get; }

            public Progress(Phase phase, int runNumber, int repetitions, double phaseElapsed,
                double phaseRemaining, double totalRemaining, string targetScene, bool measuring)
            {
                Phase = phase; RunNumber = runNumber; Repetitions = repetitions;
                PhaseElapsedSeconds = phaseElapsed; PhaseRemainingSeconds = phaseRemaining;
                TotalRemainingSeconds = totalRemaining; TargetSceneName = targetScene ?? "";
                IsMeasuring = measuring;
            }

            /// <summary>What this phase is doing, in the user's language.</summary>
            public string Headline
            {
                get
                {
                    switch (Phase)
                    {
                        case Phase.OpenScene: return L.Tr("Opening the scene", "正在打开场景");
                        case Phase.Prepare: return L.Tr("Getting ready", "准备中");
                        case Phase.EnterPlay: return L.Tr("Entering Play Mode", "正在进入 Play Mode");
                        case Phase.AwaitScene:
                            return string.IsNullOrEmpty(TargetSceneName)
                                ? L.Tr("Waiting for the scene", "正在等待场景")
                                : L.Tr($"Waiting for {TargetSceneName}", $"正在等待 {TargetSceneName}");
                        case Phase.Warmup: return L.Tr("Warming up", "预热中");
                        case Phase.Sample: return L.Tr("Sampling", "采样中");
                        case Phase.Collect: return L.Tr("Collecting", "收集中");
                        case Phase.Merge: return L.Tr("Attributing hotspots", "归因热点");
                        case Phase.ExitPlay: return L.Tr("Leaving Play Mode", "正在退出 Play Mode");
                        case Phase.Settle: return L.Tr("Settling", "等待稳定");
                        default: return L.Tr("Measuring", "测量中");
                    }
                }
            }

            /// <summary>
            /// What the user should be doing right now. Two different instructions, and getting them the wrong way
            /// round wastes the whole session: while waiting for the scene they may have to play the game to reach
            /// it, and from the warmup onwards any input at all lands in the numbers.
            ///
            /// Kept to a handful of words, because the headline beside it has already named the scene being waited
            /// for — an instruction that repeats it ("play until you get there") is both longer and vaguer than one
            /// that does not, and length here costs real estate on a bar that has to fit a narrow Game view.
            /// </summary>
            /// <summary>
            /// …and "then hold still" is half the instruction, not a nicety. Measured on the museum project: the
            /// three repetitions of a baseline read 445k / 214k / 439k vertices, and within EACH of them the first
            /// half of the window averaged ~445k against ~250k for the second. Nothing was loading — the camera was
            /// being moved. Two repetitions taken later with the camera parked agreed to 0.002%.
            ///
            /// So the sampling window is the part that has to be held still, and the strip is the only place that
            /// can say so at the moment it matters. Saying only "keep playing" — which is what it used to say — is
            /// an instruction to produce exactly the measurement that cannot be compared against anything.
            /// </summary>
            public string Instruction
            {
                get
                {
                    if (Phase == Phase.AwaitScene)
                        return L.Tr("waiting for it to load", "等待目标场景");

                    if (Phase != Phase.Warmup && Phase != Phase.Sample)
                        return L.Tr("stay in the Game view", "请勿离开游戏窗口");

                    // "Hold still" was the wrong instruction for most games, and Tim said so: scenes that need a
                    // player cannot be held still, and telling someone to freeze produces a measurement of a game
                    // nobody plays. What the comparison actually needs is not stillness — it is the SAME route
                    // twice, because a before and an after only mean something when they describe the same thing.
                    //
                    // So the first repetition of a baseline is the one that defines the route, and has nothing to
                    // match; everything after it — the rest of the baseline, the calibration behind it, and every
                    // later comparison — is trying to reproduce that one. A run that fails to is not silently
                    // trusted either: whatever moved inside its window widens that figure's noise band
                    // (BenchmarkStats.WithinRunSwingPercent), so an inconsistent route costs precision rather than
                    // producing a wrong verdict.
                    bool definesTheRoute = RunNumber <= 1
                        && string.Equals(BenchmarkIntent.Current, BenchmarkIntent.Baseline, StringComparison.Ordinal);

                    return definesTheRoute
                        ? L.Tr("explore the scene", "探索场景")
                        : L.Tr("retrace your last run", "尽量走上次那条路线");
                }
            }
        }

        /// <summary>
        /// Rough seconds of overhead per repetition on top of its warmup and sample: entering and leaving Play Mode,
        /// collecting, merging hotspots and settling. Shared with the panels so a button that promises "about 70s"
        /// and the strip that counts it down cannot disagree.
        /// </summary>
        public const double PerRunOverheadSeconds = 12;

        /// <summary>
        /// How many repetitions each kind of measurement takes, and how long each one samples for.
        ///
        /// Here rather than in the panel that draws the buttons, because the run that follows a baseline is started
        /// with no panel involved — see <see cref="BenchmarkIntent.ChainCalibrationAfterBaseline"/>. They were four
        /// consts inside PerfLintAutopilotWindow, which is fine right up until something outside a window has to
        /// start a measurement, and then it is a policy living in the wrong layer.
        /// </summary>
        public const int BaselineRepetitions = 3;
        /// <summary>Two is the minimum that yields a spread at all, and every non-baseline measurement uses it.</summary>
        public const int CompareRepetitions = 2;
        public const float DefaultWarmupSeconds = 5f;
        public const float DefaultSampleSeconds = 20f;

        /// <summary>Seconds after the sampling window closes before the next repetition can start.</summary>
        const double TailOverheadSeconds = 8;

        public static double EstimatedSessionSeconds(int repetitions, double warmupSeconds, double sampleSeconds) =>
            Math.Max(1, repetitions) * (warmupSeconds + sampleSeconds + PerRunOverheadSeconds);

        /// <summary>Live progress. <see cref="Progress.IsMeasuring"/> is false when nothing is running.</summary>
        public static Progress CurrentProgress
        {
            get
            {
                var phase = CurrentPhase;
                var spec = LoadSpec();
                if (!IsRunning || spec == null)
                    return new Progress(phase, 0, 0, 0, double.NaN, double.NaN, "", false);

                double elapsed = EditorApplication.timeSinceStartup - SessionState.GetFloat(KPhaseStart, 0f);
                if (elapsed < 0) elapsed = 0;

                int runIndex = CurrentRunIndex;
                int reps = Math.Max(1, spec.repetitions);
                double warm = spec.warmupSeconds, sample = spec.sampleSeconds;

                double phaseRemaining = double.NaN;
                if (phase == Phase.Warmup) phaseRemaining = Math.Max(0, warm - elapsed);
                else if (phase == Phase.Sample) phaseRemaining = Math.Max(0, sample - elapsed);

                // Everything still owed by the repetition in flight. Deliberately coarse after the sampling window
                // closes — what is left there is collection and a domain reload, which no clock we own can predict.
                double thisRun;
                switch (phase)
                {
                    case Phase.Warmup: thisRun = Math.Max(0, warm - elapsed) + sample + TailOverheadSeconds; break;
                    case Phase.Sample: thisRun = Math.Max(0, sample - elapsed) + TailOverheadSeconds; break;
                    case Phase.Collect:
                    case Phase.Merge:
                    case Phase.ExitPlay:
                    case Phase.Settle: thisRun = Math.Max(0, TailOverheadSeconds - elapsed); break;
                    default: thisRun = warm + sample + TailOverheadSeconds; break;
                }

                double total = thisRun + Math.Max(0, reps - runIndex - 1) * (warm + sample + PerRunOverheadSeconds);

                // Waiting on the game (or on a human playing it) has no honest estimate attached to it.
                if (phase == Phase.AwaitScene && spec.WaitsForScene) total = double.NaN;

                return new Progress(phase, runIndex + 1, reps, elapsed, phaseRemaining, total,
                    SceneNameOf(TargetScenePath(spec)), true);
            }
        }

        public static string BenchmarksRoot
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint", "benchmarks");
            }
        }

        public static string SessionDir(string sessionId) => Path.Combine(BenchmarksRoot, sessionId);

        /// <summary>
        /// Starts a benchmark session. Returns null on success, or a human-readable reason why it refused.
        ///
        /// Refusing up front is the point: every condition checked here is one that would produce a run that dies
        /// halfway — a compile in flight, an editor already in Play Mode.
        ///
        /// Deep Profile used to be refused here as well, and that check outlived the policy it belonged to. The
        /// panels stopped refusing and started explaining the trade, so the only thing left was a dialog appearing
        /// underneath a caption that had just promised the measurement would work. A refusal buried one layer below
        /// the screen that offers the button is worse than either answer on its own: two places decided the same
        /// question and only one of them was updated.
        /// </summary>
        public static string Begin(BenchmarkSpec spec)
        {
            var open = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            string refusal = RefusalFor(spec, IsRunning,
                EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode,
                EditorApplication.isCompiling, EditorApplication.isUpdating,
                ProfilerDriver.deepProfiling,
                open.path, open.isDirty);
            if (refusal != null) return refusal;

            return BeginWithId(spec, "bench-" + DateTime.UtcNow.Ticks.ToString());
        }

        /// <summary>
        /// Every condition that refuses a measurement, as a pure function of the editor state — so the policy can be
        /// tested, and so it lives in exactly one place.
        ///
        /// It is one function because it was three, and that is how a stale one survived. When Deep Profile stopped
        /// being a refusal, two of the three places were updated and this one was not — leaving a dialog that refused
        /// the measurement directly underneath a caption promising it would work. <paramref name="deepProfiling"/> is
        /// still a parameter, unused on purpose: it documents that the answer is deliberately "no objection", and a
        /// test pins it there.
        /// </summary>
        public static string RefusalFor(BenchmarkSpec spec, bool alreadyRunning, bool inPlayMode,
            bool compiling, bool importing, bool deepProfiling,
            string openScenePath = null, bool openSceneDirty = false)
        {
            if (spec == null) return "no benchmark spec";
            if (alreadyRunning) return "a benchmark is already running";
            if (inPlayMode) return "the editor is already in Play Mode — exit it first";
            if (compiling) return "scripts are still compiling — wait for the compile to finish";
            if (importing) return "the asset database is still importing — wait for the import to finish";
            if (spec.repetitions < 1) return "repetitions must be at least 1";
            if (spec.sampleSeconds <= 0) return "sampling window must be positive";

            // A plan pointing at a scene that no longer exists is refused rather than ignored. Falling back to
            // "measure whatever is open" would keep producing runs while the user believes a plan is in force, and
            // those runs would be filed under the wrong scene — which is the exact failure this whole feature exists
            // to end.
            if (!string.IsNullOrEmpty(spec.startSceneGuid) && string.IsNullOrEmpty(StartScenePath(spec)))
                return "the start scene set for measuring no longer exists — pick it again";
            if (!string.IsNullOrEmpty(spec.targetSceneGuid) && string.IsNullOrEmpty(TargetScenePath(spec)))
                return "the scene set to be measured no longer exists — pick it again";

            // Opening the start scene would discard unsaved work, and a state machine tick is the wrong place to ask
            // about it: a modal dialog raised from inside the loop stalls the loop it is running in. So the question
            // is refused up here, where the caller still has a user to put it to.
            string start = StartScenePath(spec);
            if (openSceneDirty && !string.IsNullOrEmpty(start)
                && !string.Equals(start, openScenePath, StringComparison.Ordinal))
                return $"the open scene has unsaved changes and measuring would replace it with " +
                       $"{SceneNameOf(start)} — save or discard them first";

            // Deep Profile is NOT refused. It costs the wall-clock figures, which the comparison drops rather than
            // reports, and it buys the per-method markers that nothing else can produce. See MeasurementModeNote.
            return null;
        }

        /// <summary>
        /// What a measurement started right now can and cannot answer, or null when there is no caveat.
        ///
        /// Shared by both panels for the same reason <see cref="RefusalFor"/> is one function: the same fact stated in
        /// two places is two places to be wrong, and this one has already been wrong once.
        /// </summary>
        public static string MeasurementModeNote() =>
            ProfilerDriver.deepProfiling
                ? L.Tr("Deep Profile is on: you'll get the exact method names and how often each one runs, but no frame rate — those milliseconds are the profiler's own cost, so the result leaves them out rather than reporting them wrong.",
                       "Deep Profile 已开启：这次能拿到精确的方法名与各自跑了多少帧，但拿不到帧率——那些毫秒是 Profiler 自身的开销，所以结果里会略去、而不是报一个错的。")
                : null;

        static string BeginWithId(BenchmarkSpec spec, string sessionId)
        {
            string dir = SessionDir(sessionId);
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { return "could not create the session directory: " + e.Message; }

            SessionState.SetString(KSessionId, sessionId);
            SessionState.SetString(KSpec, spec.ToJson());
            SessionState.SetInt(KRunIndex, 0);
            SessionState.SetString(KError, "");
            SessionState.SetInt(KVSyncSnapshot, -1);
            SessionState.SetBool(KSceneDirtySnapshot,
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty);
            SessionState.SetFloat(KQuietSince, (float)EditorApplication.timeSinceStartup);

            SafeWrite(Path.Combine(dir, "spec.json"), spec.ToJson());

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            GoTo(Phase.OpenScene);

            string plan = "";
            string start = SceneNameOf(StartScenePath(spec));
            string target = SceneNameOf(TargetScenePath(spec));
            if (!string.IsNullOrEmpty(start)) plan += $" · from {start}";
            if (!string.IsNullOrEmpty(target)) plan += $" · measuring {target}";
            Log($"started · {spec.repetitions}× ({spec.warmupSeconds:0.#}s warmup + {spec.sampleSeconds:0.#}s sample){plan} · session {sessionId}");
            return null;
        }

        /// <summary>Aborts the session, restores anything we changed, and leaves whatever runs already completed on disk.</summary>
        public static void Cancel(string reason = "cancelled by user")
        {
            if (!IsRunning) return;
            Fail(reason);
        }

        /// <summary>Loads a completed (or partial) session from disk. Returns null when the directory holds nothing usable.</summary>
        public static BenchmarkSession LoadSession(string sessionId)
        {
            string dir = SessionDir(sessionId);
            if (!Directory.Exists(dir)) return null;

            BenchmarkSpec spec = null;
            try
            {
                string sp = Path.Combine(dir, "spec.json");
                if (File.Exists(sp)) spec = BenchmarkSpec.FromJson(File.ReadAllText(sp));
            }
            catch { /* a missing spec still leaves the runs readable */ }

            var runs = new List<BenchmarkRun>();
            try
            {
                var files = Directory.GetFiles(dir, "run-*.json");
                Array.Sort(files, StringComparer.Ordinal); // run-000, run-001, … sorts chronologically
                foreach (var f in files)
                {
                    var r = BenchmarkRun.FromJson(File.ReadAllText(f));
                    if (r != null) runs.Add(r);
                }
            }
            catch (Exception e) { LogWarning("could not read session runs: " + e.Message); }

            return runs.Count > 0 || spec != null ? new BenchmarkSession(sessionId, spec, runs) : null;
        }

        /// <summary>
        /// The newest session whose first run started after <paramref name="utc"/>, or null when there is none.
        ///
        /// This is what makes "after" mean after: comparing a baseline against a session recorded BEFORE it was
        /// pinned would present the two in the wrong order and call whatever difference it found an improvement.
        /// Session ids are tick-prefixed, so walking them newest-first lets us stop at the first one that is too old.
        /// </summary>
        public static BenchmarkSession LoadLatestSessionAfter(DateTime utc)
        {
            foreach (var id in ListSessions())
            {
                var s = LoadSession(id);
                if (s == null || !s.HasRuns) continue;
                if (s.Runs[0].StartedAtUtc <= utc) return null; // and every remaining id is older still
                return s;
            }
            return null;
        }

        /// <summary>Session ids present on disk, newest first.</summary>
        public static IReadOnlyList<string> ListSessions()
        {
            try
            {
                if (!Directory.Exists(BenchmarksRoot)) return Array.Empty<string>();
                var dirs = Directory.GetDirectories(BenchmarksRoot);
                var names = new List<string>(dirs.Length);
                foreach (var d in dirs) names.Add(Path.GetFileName(d));
                names.Sort(StringComparer.Ordinal);
                names.Reverse(); // ids are tick-prefixed, so ordinal order is chronological
                return names;
            }
            catch { return Array.Empty<string>(); }
        }

        // ── State machine ─────────────────────────────────────

        static void Tick()
        {
            try { Step(); }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} {e}");
                Fail("internal error: " + e.Message);
            }
        }

        static void Step()
        {
            var phase = CurrentPhase;
            if (phase == Phase.Idle || phase == Phase.Done || phase == Phase.Failed)
            {
                EditorApplication.update -= Tick;
                return;
            }

            var spec = LoadSpec();
            if (spec == null) { Fail("the benchmark spec was lost"); return; }

            double elapsed = EditorApplication.timeSinceStartup - SessionState.GetFloat(KPhaseStart, 0f);

            // A recompile mid-flight is fatal to any phase that spans Play Mode: it forces a reload that takes the
            // scene, the sampler and the run with it. Better a named failure than a silently truncated measurement.
            if (EditorApplication.isCompiling && phase >= Phase.EnterPlay && phase <= Phase.Merge)
            {
                Fail("scripts recompiled during the run — the measurement was interrupted");
                return;
            }

            switch (phase)
            {
                case Phase.OpenScene: StepOpenScene(spec, elapsed); break;
                case Phase.Prepare: StepPrepare(spec, elapsed); break;
                case Phase.EnterPlay: StepEnterPlay(elapsed); break;
                case Phase.AwaitScene: StepAwaitScene(spec, elapsed); break;
                case Phase.Warmup: StepWarmup(spec, elapsed); break;
                case Phase.Sample: StepSample(spec, elapsed); break;
                case Phase.Collect: StepCollect(spec, elapsed); break;
                case Phase.Merge: StepMerge(elapsed); break;
                case Phase.ExitPlay: StepExitPlay(elapsed); break;
                case Phase.Settle: StepSettle(spec, elapsed); break;
            }
        }

        /// <summary>
        /// Opens the scene Play Mode has to boot from, when the plan names one and it is not already open.
        ///
        /// Runs BEFORE Prepare rather than after it, because opening a scene is exactly the kind of work Prepare
        /// exists to wait out: it imports, it can trigger a reimport, and a run started in that wake measures the
        /// editor putting itself back together. Ordering it first means the quiet window is measured after the
        /// disturbance rather than before it.
        ///
        /// Unsaved work is never at risk here — <see cref="RefusalFor"/> refuses the whole session while the open
        /// scene is dirty and a different one is about to replace it, so by the time this runs the question has been
        /// put to somebody who can answer it. Doing it the other way round would mean a state machine tick raising a
        /// modal dialog, which stalls the machine it is running inside.
        /// </summary>
        static void StepOpenScene(BenchmarkSpec spec, double elapsed)
        {
            if (elapsed > OpenSceneTimeout) { Fail("timed out opening the start scene"); return; }

            string want = StartScenePath(spec);
            if (string.IsNullOrEmpty(want)) { GoTo(Phase.Prepare); return; } // nothing to open — start from what's open

            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.Equals(active.path, want, StringComparison.Ordinal)) { GoTo(Phase.Prepare); return; }

            // Between repetitions this is normally already satisfied: leaving Play Mode restores the edit-mode scene,
            // whatever the game loaded on top of it. It is checked every round anyway, so a run cannot silently start
            // from somewhere else after a session that ended somewhere unexpected.
            try
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    want, UnityEditor.SceneManagement.OpenSceneMode.Single);
            }
            catch (Exception e)
            {
                Fail($"could not open the start scene ({want}): {e.Message}");
                return;
            }

            Log($"opened start scene {SceneNameOf(want)}");
            GoTo(Phase.Prepare);
        }

        static void StepPrepare(BenchmarkSpec spec, double elapsed)
        {
            if (elapsed > PrepareTimeout) { Fail("timed out preparing the run"); return; }

            // Not just "not busy right now" but "not busy for a while": an import or a compile finishing hands
            // control back before the editor has settled, and a re-measurement started in that gap is measuring the
            // tail of the change rather than its effect. Reset on every busy tick, so the window has to be clear.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                SessionState.SetFloat(KQuietSince, (float)EditorApplication.timeSinceStartup);
                return;
            }
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(KQuietSince, 0f) < QuietSeconds) return;

            // VSync clamps frame time to the display refresh interval, so with it on a 24ms→16ms improvement can
            // read as "no change at all". We turn it off for the duration and put it back afterwards — including
            // on the failure path, which is why the previous value goes into SessionState and not a static field.
            if (SessionState.GetInt(KVSyncSnapshot, -1) < 0 && QualitySettings.vSyncCount != 0)
            {
                SessionState.SetInt(KVSyncSnapshot, QualitySettings.vSyncCount);
                QualitySettings.vSyncCount = 0;
                Log("VSync temporarily disabled for the measurement (restored when the session ends)");
            }

            FocusGameView();
            GoTo(Phase.EnterPlay);
        }

        static void StepEnterPlay(double elapsed)
        {
            if (EditorApplication.isPlaying) { GoTo(Phase.AwaitScene); return; }
            if (elapsed > EnterPlayTimeout) { Fail("timed out entering Play Mode"); return; }

            // Request once, then wait. Re-asserting the flag every tick while Unity is mid-transition just fights it.
            if (!EditorApplication.isPlayingOrWillChangePlaymode && elapsed > 0.5)
                EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// Holds the measurement until the scene the run is ABOUT has actually loaded.
        ///
        /// The game reaches it by its own route — a boot sequence that loads it automatically, or a person playing
        /// through a menu to get there. Either way nothing is sampled until it is up, so the warmup clock starts
        /// against the scene being measured rather than against a splash screen. Without this, a project that boots
        /// through Init spends its whole warmup somewhere else and its sample wherever it happened to arrive.
        ///
        /// "Loaded" rather than "active": additive loads are ordinary, and a game that keeps a persistent manager
        /// scene active while the level renders on top of it is measuring the level either way. Requiring it to be
        /// the active scene would wait forever on exactly those projects.
        /// </summary>
        static void StepAwaitScene(BenchmarkSpec spec, double elapsed)
        {
            if (!EditorApplication.isPlaying) { Fail("Play Mode ended before the scene to measure was reached"); return; }

            if (!spec.WaitsForScene || TargetSceneLoaded(spec)) { GoTo(Phase.Warmup); return; }

            if (elapsed > AwaitSceneTimeout)
            {
                string name = SceneNameOf(TargetScenePath(spec)) ?? "the scene to measure";
                Fail($"{name} was never reached — the game did not load it within {AwaitSceneTimeout:0}s");
            }
        }

        /// <summary>Whether the scene the run is about is loaded right now. True when no scene is being waited for.</summary>
        static bool TargetSceneLoaded(BenchmarkSpec spec)
        {
            string want = TargetScenePath(spec);
            if (string.IsNullOrEmpty(want)) return true;

            int count = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (s.isLoaded && string.Equals(s.path, want, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Current path of a scene the spec names, resolved through its GUID first.
        ///
        /// The GUID wins because a spec can outlive a rename — it is written to disk, survives two domain reloads per
        /// repetition, and a session can be resumed against a project someone has since reorganised. The stored path
        /// is the fallback for a spec written before GUIDs were recorded.
        /// </summary>
        static string ResolveScenePath(string guid, string storedPath)
        {
            string byGuid = BenchmarkScenePlan.PathOf(guid);
            return !string.IsNullOrEmpty(byGuid) ? byGuid : storedPath;
        }

        static string StartScenePath(BenchmarkSpec spec) =>
            ResolveScenePath(spec?.startSceneGuid, spec?.startScenePath);

        static string TargetScenePath(BenchmarkSpec spec) =>
            ResolveScenePath(spec?.targetSceneGuid, spec?.targetScenePath);

        static void StepWarmup(BenchmarkSpec spec, double elapsed)
        {
            if (!EditorApplication.isPlaying) { Fail("Play Mode ended before sampling started"); return; }
            if (elapsed > spec.warmupSeconds + PhaseSlack) { Fail("timed out during warmup"); return; }

            // Uncapped, so the frame time we measure is the frame time the machine can actually produce.
            // This is a runtime-only setting and resets by itself when Play Mode ends — nothing to restore.
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;

            if (elapsed >= spec.warmupSeconds) GoTo(Phase.Sample);
        }

        static void StepSample(BenchmarkSpec spec, double elapsed)
        {
            if (!EditorApplication.isPlaying) { Fail("Play Mode ended during sampling"); return; }

            if (_sampler == null)
            {
                _sampler = new RuntimeSampler();
                _sampler.Start();
                // Read here, not from the spec: this is the scene the numbers are about to be taken in, which on a
                // project with a boot sequence is not the scene the spec names. Recorded at both ends of the window
                // so a clean measurement of another scene can be told apart from one that straddled a load —
                // see BenchmarkSceneTruth.
                SessionState.SetString(KSampledStartPath, ActiveScenePath());
                Log($"run {CurrentRunIndex + 1}: sampling {spec.sampleSeconds:0.#}s…");
            }

            if (elapsed > spec.sampleSeconds + PhaseSlack) { Fail("timed out during sampling"); return; }
            if (elapsed >= spec.sampleSeconds) GoTo(Phase.Collect);
        }

        /// <summary>Scene name from an asset path ("Assets/Init.unity" → "Init"), or null when there is no path.</summary>
        static string SceneNameOf(string scenePath) =>
            string.IsNullOrEmpty(scenePath) ? null : Path.GetFileNameWithoutExtension(scenePath);

        /// <summary>
        /// Asset path of the scene Play Mode is in right now, or "" when it has none.
        ///
        /// Empty is a real answer and must stay distinguishable from "we did not look": a scene created at runtime,
        /// or one never saved, has no asset path and therefore no identity to compare — reporting that as a
        /// disagreement would relabel a baseline on the strength of a blank.
        /// </summary>
        /// <summary>GUID for a scene asset path, or "" — the durable half of the pair, since a rename must not read as a different scene.</summary>
        static string GuidOfScene(string scenePath) =>
            string.IsNullOrEmpty(scenePath) ? "" : (AssetDatabase.AssetPathToGUID(scenePath) ?? "");

        static string ActiveScenePath()
        {
            try
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                return s.IsValid() ? s.path ?? "" : "";
            }
            catch { return ""; }
        }

        static void StepCollect(BenchmarkSpec spec, double elapsed)
        {
            if (elapsed > CollectTimeout) { Fail("timed out collecting the run"); return; }

            // The sampler is gone (a reload slipped in) or was never started — no honest way to salvage this run.
            if (_sampler == null) { Fail("the sampler was lost before the run could be collected"); return; }

            // Both of these MUST happen while still in Play Mode: Stop() captures the scene's batching snapshot
            // from live Renderers, and the fingerprint reads the Game View backbuffer size. The sampler is NOT
            // disposed here any more — the hotspot merge that follows reads the frame window it captured.
            RuntimeProfileResult result;
            BenchmarkFingerprint fingerprint;
            try
            {
                result = _sampler.Stop();
                fingerprint = CaptureFingerprint(spec);
            }
            catch
            {
                DisposeSampler();
                throw;
            }

            if (result == null) { DisposeSampler(); Fail("sampling produced no data"); return; }

            // Still in Play Mode, so this is the scene the sampling window CLOSED in. Together with the one recorded
            // when it opened, it is the only evidence that says whether these numbers describe one scene.
            string sampledStart = SessionState.GetString(KSampledStartPath, "");
            string sampledEnd = ActiveScenePath();

            int index = CurrentRunIndex;
            string path = Path.Combine(SessionDir(CurrentSessionId), $"run-{index:000}.json");
            var run = new BenchmarkRun
            {
                sessionId = CurrentSessionId,
                index = index,
                startedAtUtcTicks = DateTime.UtcNow.Ticks,
                durationSeconds = result.DurationSeconds,
                frameCount = result.FrameCount,
                fingerprint = fingerprint,
                metrics = BenchmarkRun.MetricsFrom(result),
                sceneDirtyStateRecorded = true,
                sceneWasDirtyAtStart = SessionState.GetBool(KSceneDirtySnapshot, false),
                sampledSceneRecorded = true,
                sampledScenePathAtStart = sampledStart,
                sampledSceneGuidAtStart = GuidOfScene(sampledStart),
                sampledScenePathAtEnd = sampledEnd,
                sampledSceneGuidAtEnd = GuidOfScene(sampledEnd)
            };

            // Straight to disk, before the merge and without waiting for it: the counters are what makes this run a
            // measurement, and the user can end Play Mode by hand at any moment. The merge rewrites this file when it
            // finishes; if it never does, what is on disk is a complete run with hotspotsMerged left false — which
            // reads as "we did not look", not as "there were none".
            SafeWrite(path, run.ToJson());

            var ft = run.Get(BenchmarkMetricKeys.FrameTimeMs);
            Log($"run {index + 1} collected · {run.frameCount} frames · " +
                (ft != null && ft.hasData ? $"median {ft.median:0.00} ms" : "frame time unavailable"));

            BeginHotspotMerge(spec, result, run, path);
            GoTo(Phase.Merge);
        }

        /// <summary>
        /// Attributes the run's main-thread time to markers, and rewrites the run with the result.
        ///
        /// This has to happen HERE, inside the Play Mode session that produced the data, and that is a hard
        /// constraint rather than a preference: the merge replays profiler frames over several editor ticks, and the
        /// domain reload on the way out of Play Mode destroys the callback mid-flight. Which is why the benchmark
        /// path had no hotspots at all until now — every measurement the "measure this scene" button ever produced
        /// carried thirteen global counters and nothing about which call path spent the time.
        /// </summary>
        static void BeginHotspotMerge(BenchmarkSpec spec, RuntimeProfileResult result, BenchmarkRun run, string path)
        {
            _mergeDone = false;
            try
            {
                _sampler.BeginHotspots(onComplete: (hotspots, worstFrames, gpuFrameTimeNs, ok) =>
                {
                    try
                    {
                        var merged = result.WithHotspots(hotspots, ok, worstFrames, gpuFrameTimeNs, _sampler?.LastGcSite,
                            _sampler?.LastGameGcPerFrame, _sampler?.LastGameFrameTime);

                        // GPU time read from frame data supersedes what the counters captured (same source as the
                        // Profiler's own "GPU ms" column), so the metrics are rebuilt rather than patched.
                        run.metrics = BenchmarkRun.MetricsFrom(merged);
                        run.hotspotsMerged = ok;
                        run.hotspots = BenchmarkRun.HotspotsFrom(merged);
                        SafeWrite(path, run.ToJson());

                        // Publishing the panel's runtime session moved here on purpose: done in Collect it would
                        // publish a session whose hotspot list is empty and whose findings therefore say hotspot
                        // collection was unavailable. Written on EVERY rep, so a session cancelled midway still
                        // leaves the last complete measurement. Still in Play Mode, so the sampled scenes are the
                        // loaded ones.
                        // The scene the sampling actually happened in wins over the one the spec names, and that
                        // order is the correction. The note here used to end "without a plan, nothing changes — the
                        // run boots and stays in one scene, so both readings agree", which is false on the ordinary
                        // shape of game: an entry scene that loads a loading scene that loads the level. The warmup
                        // is five seconds; the game is in the level well before sampling starts. So a first-time
                        // user measuring without a plan sampled the level and had it filed under Init — and then
                        // lost that measurement the moment they discovered the plan and pointed it at the level.
                        //
                        // With a plan the two agree by construction (the runner waits for the target before the
                        // warmup clock starts), so preferring the truth costs that path nothing.
                        if (spec.saveRuntimeSession)
                            RuntimeSessionStore.Save(merged, RuntimeAnalyzer.Analyze(merged), null,
                                SceneNameOf(run.sampledScenePathAtStart)
                                ?? SceneNameOf(spec.scenePath) ?? _sampler?.StartScene);

                        if (ok)
                            Log($"run {run.index + 1} hotspots merged · {run.hotspots.Length} markers");
                    }
                    catch (Exception e)
                    {
                        // Enrichment failing must not invalidate a run that is already safely on disk.
                        LogWarning("could not attach hotspots to the run: " + e.Message);
                    }
                    finally
                    {
                        _mergeDone = true;
                    }
                });
            }
            catch (Exception e)
            {
                LogWarning("could not start the hotspot merge: " + e.Message);
                _mergeDone = true;
            }
        }

        static void StepMerge(double elapsed)
        {
            if (!_mergeDone && elapsed <= MergeTimeout)
            {
                // Leaving Play Mode by hand mid-merge is the user's prerogative; the run is already on disk without
                // hotspots, so stop waiting rather than burning the watchdog.
                if (!EditorApplication.isPlaying) { CancelMerge("Play Mode ended before the hotspot merge finished"); }
                else return;
            }
            else if (!_mergeDone)
            {
                CancelMerge($"the hotspot merge did not finish within {MergeTimeout:0}s");
            }

            DisposeSampler();
            GoTo(Phase.ExitPlay);
        }

        static void CancelMerge(string why)
        {
            try { _sampler?.CancelHotspots(); } catch { /* cancelling a broken merge must not fail the run */ }
            // Not a failure: the run keeps its counters, and hotspotsMerged stays false so nothing downstream can
            // read the missing list as "the hotspot went away".
            LogWarning(why + " — this run has no hotspot attribution");
            _mergeDone = true;
        }

        static void DisposeSampler()
        {
            try { _sampler?.Dispose(); } catch { /* ignore */ }
            _sampler = null;
        }

        static void StepExitPlay(double elapsed)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                GoTo(Phase.Settle);
                return;
            }
            if (elapsed > ExitPlayTimeout) { Fail("timed out leaving Play Mode"); return; }
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        static void StepSettle(BenchmarkSpec spec, double elapsed)
        {
            if (elapsed > SettleTimeout) { Fail("timed out settling between runs"); return; }
            // Let the editor finish its post-Play-Mode reload/import churn, so the next run does not start
            // measuring while the machine is still busy putting itself back together.
            if (elapsed < SettleSeconds || EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            int next = CurrentRunIndex + 1;
            if (next < spec.repetitions)
            {
                SessionState.SetInt(KRunIndex, next);
                // Back to the top rather than straight into Play Mode: the start scene is re-checked every round, so
                // a session cannot silently continue from wherever the last one left the editor.
                GoTo(Phase.OpenScene);
                return;
            }

            Finish();
        }

        // ── Terminal states ───────────────────────────────────

        static void Finish()
        {
            RestoreSettings();
            // Before the phase flips to Done, because that flip is what lets BenchmarkIntent pin this session as the
            // baseline — from EditorApplication.update, with no window involved. A correction applied afterwards
            // would be applied to a session that had already been copied into the baseline under the wrong name.
            ReconcileSampledScene();
            CurrentPhase = Phase.Done;
            EditorApplication.update -= Tick;
            WriteStatus("done", "");
            Log($"finished · session {CurrentSessionId}");
        }

        /// <summary>
        /// Re-files the finished session under the scene it was actually taken in, when the two disagree and the
        /// measurement is clean.
        ///
        /// Writes rather than reports, because the label is load-bearing: <see cref="BenchmarkFingerprint"/>'s scene
        /// GUID is the key every comparison, baseline and drift sample is matched on, so a run left under the wrong
        /// name is a run that cannot be compared with the correct ones later. What it must never do is guess — the
        /// decision comes from <see cref="BenchmarkSceneTruth"/>, which only says "relabel" when every repetition
        /// sampled start-to-end in one and the same other scene.
        ///
        /// A session judged Unusable is left exactly as it is. Nothing here deletes a measurement; the refusal to
        /// build a baseline out of it lives in <see cref="BenchmarkBaseline.Pin"/>, where the other "this is a broken
        /// instrument, not a slow scene" refusals already are.
        /// </summary>
        static void ReconcileSampledScene()
        {
            string id = CurrentSessionId;
            if (string.IsNullOrEmpty(id)) return;

            try
            {
                var session = LoadSession(id);
                var reading = BenchmarkSceneTruth.Read(session);
                if (reading.Verdict != BenchmarkSceneTruth.Verdict.Relabelled) return;
                // Already carries its own record of having been corrected — nothing to do, and re-writing would
                // overwrite where it originally came from with where it is now.
                if (!string.IsNullOrEmpty(session.Runs[0]?.relabelledFromScenePath)) return;

                string dir = SessionDir(id);
                string was = session.Spec?.scenePath ?? "";

                var spec = session.Spec;
                if (spec != null)
                {
                    spec.sceneGuid = reading.SceneGuid;
                    spec.scenePath = reading.ScenePath;
                    SafeWrite(Path.Combine(dir, "spec.json"), spec.ToJson());
                }

                foreach (var r in session.Runs)
                {
                    if (r == null) continue;
                    if (r.fingerprint != null) r.fingerprint.sceneGuid = reading.SceneGuid;
                    r.relabelledFromScenePath = was;
                    SafeWrite(Path.Combine(dir, $"run-{r.index:000}.json"), r.ToJson());
                }

                Log($"re-filed under {reading.SceneName} — sampling ran there, not in " +
                    $"{(string.IsNullOrEmpty(was) ? "the scene on record" : SceneNameOf(was))}");
            }
            catch (Exception e)
            {
                // A correction that fails must not take the measurement with it: what is on disk is still a complete
                // session, merely under the name it already had.
                LogWarning("could not re-file the session under the scene it was sampled in: " + e.Message);
            }
        }

        static void Fail(string reason)
        {
            // Anything we changed on the user's project has to go back, whatever went wrong.
            try { _sampler?.CancelHotspots(); } catch { /* a merge left running would tick against a dead run */ }
            try { _sampler?.Dispose(); } catch { /* disposing a broken sampler must not mask the real failure */ }
            _sampler = null;
            _mergeDone = false;
            RestoreSettings();

            SessionState.SetString(KError, reason ?? "");
            CurrentPhase = Phase.Failed;
            EditorApplication.update -= Tick;
            WriteStatus("failed", reason);
            LogWarning("aborted: " + reason);

            // A benchmark that dies mid-flight can leave the editor in Play Mode; put it back.
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        /// <summary>
        /// Gives back the VSync setting the run borrowed — or arranges for it to be given back once that is possible.
        ///
        /// The deferral is not caution, it is a measured failure. Writing <see cref="QualitySettings.vSyncCount"/>
        /// while still in Play Mode looks like it works and is then thrown away: leaving Play Mode rolls
        /// QualitySettings back to what they were when Play Mode STARTED, and by then Prepare had already set VSync
        /// to 0. So the restore is undone by the very exit that follows it.
        ///
        /// It never showed on the normal path — <see cref="Finish"/> runs in Settle, after Play Mode has ended — but
        /// cancelling runs <see cref="Fail"/> from inside Play Mode, and the run then exits it. Found by cancelling
        /// a real session on a project whose Ultra tier declares vSyncCount: 1 and reading 0 back afterwards. The
        /// Cancel button on the Game view strip is what turned this from a corner into the main path: until it
        /// existed, cancelling meant finding a panel that Play Mode had hidden behind the game.
        /// </summary>
        static void RestoreSettings()
        {
            int vsync = SessionState.GetInt(KVSyncSnapshot, -1);
            if (vsync < 0) return;

            if (MustDeferRestore(EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode))
            {
                // Left in SessionState on purpose: it survives the reload that leaving Play Mode costs, and the
                // hook re-arms itself from the static constructor on the other side of it.
                ArmPendingRestore();
                return;
            }

            QualitySettings.vSyncCount = vsync;
            SessionState.SetInt(KVSyncSnapshot, -1);
        }

        /// <summary>
        /// Whether returning a borrowed project setting has to wait for Play Mode to end.
        ///
        /// A pure function so the rule can be pinned by a test: a write made inside Play Mode is discarded by the
        /// exit that follows, which is silent and leaves the user's project changed. See <see cref="RestoreSettings"/>.
        /// </summary>
        public static bool MustDeferRestore(bool playing, bool willChangePlaymode) => playing || willChangePlaymode;

        static void ArmPendingRestore()
        {
            EditorApplication.update -= RestorePendingSettings;
            EditorApplication.update += RestorePendingSettings;
        }

        /// <summary>
        /// Returns a borrowed setting once the editor is out of Play Mode.
        ///
        /// Deliberately independent of the session: by the time this can run the session is already Failed and its
        /// tick unsubscribed, so anything hanging off the state machine would never fire. Same rule as the progress
        /// strip — something that MUST happen cannot be owned by a thing that may not be alive to do it.
        /// </summary>
        static void RestorePendingSettings()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;

            int vsync = SessionState.GetInt(KVSyncSnapshot, -1);
            if (vsync >= 0)
            {
                QualitySettings.vSyncCount = vsync;
                SessionState.SetInt(KVSyncSnapshot, -1);
                Log($"VSync restored to {vsync}");
            }
            EditorApplication.update -= RestorePendingSettings;
        }

        // ── Helpers ───────────────────────────────────────────

        static void GoTo(Phase phase)
        {
            CurrentPhase = phase;
            SessionState.SetFloat(KPhaseStart, (float)EditorApplication.timeSinceStartup);
        }

        static BenchmarkSpec LoadSpec() => BenchmarkSpec.FromJson(SessionState.GetString(KSpec, ""));

        static BenchmarkFingerprint CaptureFingerprint(BenchmarkSpec spec)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            GameViewSize(out int w, out int h);
            return new BenchmarkFingerprint
            {
                sceneGuid = spec.sceneGuid,
                warmupSeconds = spec.warmupSeconds,
                sampleSeconds = spec.sampleSeconds,
                driveCamera = spec.driveCamera,
                unityVersion = Application.unityVersion,
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                renderPipeline = rp != null ? rp.GetType().Name : "Built-in",
                qualityLevel = QualitySettings.GetQualityLevel(),
                qualityName = QualityName(),
                deepProfile = ProfilerDriver.deepProfiling,
                vSyncCount = QualitySettings.vSyncCount,
                targetFrameRate = Application.targetFrameRate,
                screenWidth = w,
                screenHeight = h,
                gameViewCount = GameViewCount()
            };
        }

        /// <summary>
        /// How many Game views exist. Each open one renders the scene again, which costs frame time while leaving the
        /// backbuffer size unchanged — so without this the cost of docking a second Game view is indistinguishable
        /// from the project having got slower.
        ///
        /// Counts existing windows rather than visible ones: whether a docked tab is frontmost is not reliably
        /// readable, and over-counting only ever causes a comparison to be refused, which is the safe direction.
        /// Returns 0 when the type cannot be resolved, which the fingerprint treats as "not recorded".
        /// </summary>
        static int GameViewCount()
        {
            try
            {
                var t = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (t == null) return 0;
                var all = UnityEngine.Resources.FindObjectsOfTypeAll(t);
                return all != null ? Math.Max(1, all.Length) : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Game View backbuffer size — the thing fill rate actually scales with.
        ///
        /// NOT <see cref="Screen.width"/>: this runs on EditorApplication.update, where Screen reports the
        /// dimensions of whichever editor GUI view happens to be current, so it can hand back the Inspector's
        /// width. Since the fingerprint is compared for exact equality, a value that wobbles like that would make
        /// every run incomparable with every other. UnityStats.screenRes is the Profiler's own source for the
        /// rendered resolution and does not have that problem.
        /// </summary>
        static void GameViewSize(out int width, out int height)
        {
            width = 0; height = 0;
            try
            {
                var res = UnityStats.screenRes; // "1920x1080"
                if (!string.IsNullOrEmpty(res))
                {
                    int x = res.IndexOf('x');
                    if (x > 0
                        && int.TryParse(res.Substring(0, x), out int w)
                        && int.TryParse(res.Substring(x + 1), out int h)
                        && w > 0 && h > 0)
                    {
                        width = w; height = h;
                        return;
                    }
                }
            }
            catch { /* fall through to the last-resort reading below */ }

            // Last resort. Imprecise for the reason above, but a wrong-and-stable value only costs us a
            // comparison; leaving the field at 0 would make two genuinely different resolutions look identical.
            width = Screen.width;
            height = Screen.height;
        }

        static string QualityName()
        {
            try
            {
                var names = QualitySettings.names;
                int i = QualitySettings.GetQualityLevel();
                return (names != null && i >= 0 && i < names.Length) ? names[i] : i.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Makes sure the Game View exists and is frontmost. Without a rendering Game View the render counters
        /// (draw calls, SetPass, triangles) have nothing to count, and frame time measures an idle editor.
        /// </summary>
        static void FocusGameView()
        {
            if (Application.isBatchMode) return; // no editor windows to focus
            try
            {
                var t = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (t == null) return;
                var w = EditorWindow.GetWindow(t, false, null, true);
                w?.Focus();
            }
            catch { /* best effort — a benchmark must not die because a window would not come forward */ }
        }

        static void WriteStatus(string state, string error)
        {
            string id = CurrentSessionId;
            if (string.IsNullOrEmpty(id)) return;
            var json = JsonUtility.ToJson(new StatusDto
            {
                sessionId = id,
                state = state,
                error = error ?? "",
                runsCompleted = CurrentRunIndex + (state == "done" ? 1 : 0),
                finishedAtUtcTicks = DateTime.UtcNow.Ticks
            }, true);
            SafeWrite(Path.Combine(SessionDir(id), "status.json"), json);
        }

        static void SafeWrite(string path, string content)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, content);
            }
            catch (Exception e) { LogWarning($"could not write {path}: {e.Message}"); }
        }

        static void Log(string msg) => Debug.Log($"{LogTag} {msg}");
        static void LogWarning(string msg) => Debug.LogWarning($"{LogTag} {msg}");

        [Serializable]
        sealed class StatusDto
        {
            public string sessionId;
            public string state;
            public string error;
            public int runsCompleted;
            public long finishedAtUtcTicks;
        }
    }
}
