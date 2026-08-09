using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Licensing;
using PerfLint.Llm;
using PerfLint.Runtime;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// PerfLint main panel.
    /// W5: Report UX — two-level collapse (domain → rule), severity/fixable/search filters, Info hidden by default, per-rule batch fixing.
    /// Later: W7 adds "Explain" (LLM); W8 adds Free/Pro feature gating.
    /// </summary>
    public sealed class PerfLintWindow : EditorWindow
    {
        private Label _fixableLabel;   // "80 one-click-fixable · 1.4s" subtext
        private Label _savingsLabel;   // "Est. potential savings: up to ~X build · ~Y memory" — hidden when no rule produced an estimate
        private VisualElement _savingsRow;      // savings label — the one-click optimize buttons live in the Autopilot now
        private Label _optimizedLabel;          // "Optimized ~X for you (est.)" — session tally, verified by rescan deltas
        private long _optimizedMemBytes;        // session accumulators behind _optimizedLabel
        private long _optimizedBuildBytes;
        private Label _critPill;       // rounded severity-count badges (Critical / Warning / Info)
        private Label _warnPill;
        private Label _infoPill;
        private VisualElement _pillRow; // hidden until the first scan populates the counts
        private VisualElement _roslynBox;
        private Label _roslynNotice;
        private Button _roslynButton;
        private VisualElement _sceneScopeBox;   // persistent Info notice: scan is project-wide, but a few scene-level checks only reflect the open scene(s)
        private Label _sceneScopeNotice;
        private VisualElement _stalePluginBox; // "you're running a pre-update PerfLint build" notice (compile-broken projects never reload the domain)
        private Label _stalePluginLabel;
        private VisualElement _staleBanner; // Info banner after a report is restored from disk (non-blocking): the report is already visible; hints that a full rescan is available
        private Label _staleLabel;
        private Label _filterStatus;
        private Button _clearFocusButton;   // only shown while a focus is narrowing the list — see ClearRuleFocus
        private ScrollView _results;
        private Button _scanButton;
        private Button _fixAllButton;
        private Button _licenseButton;
        private ScanResult _lastResult;

        // Last runtime (Play Mode) sampling session, restored from disk. Kept SEPARATE from _lastResult on purpose:
        // every mutating path — ScanRunner.RescanRules / RescanFile, Fix All, OptimizePlan — rebuilds findings from
        // the scanners, and a RUN.* finding has no scanner to rebuild it, so anything merged into _lastResult would be
        // silently dropped by the next incremental rescan. The merge therefore happens at DISPLAY time only.
        private RuntimeSessionStore.Session _runtimeSession;

        /// <summary>
        /// What the panel, the score and the exports should show: the static scan plus the last runtime measurement,
        /// when one exists and still describes the scenes currently open.
        ///
        /// The scene check is the same comparability rule the benchmark fingerprint enforces — a measurement taken in
        /// another scene must not be folded into the figure for this one. When it doesn't apply, the session is still
        /// kept (the header says so); it just doesn't get to move the numbers.
        /// </summary>
        /// <summary>
        /// The static scan merged with an applicable runtime session — for the EXPORT, and nothing else now.
        ///
        /// This panel used to render the merged result, which is how it ended up holding both kinds of thing at once:
        /// a reference view of the project's assets and settings, with measurements of a play session mixed into the
        /// same list, filters and counts. Tim's word for it was 不合适, and the split is now by nature — static here,
        /// runtime in the Runtime Profiler, which finally restores its own session and can hold them.
        ///
        /// The exported report still gets both, deliberately: it is the artefact you hand to somebody else, and there
        /// the complete evidence is the point rather than a crowded window.
        /// </summary>
        private ScanResult DisplayResult() =>
            RuntimeSessionStore.Merge(_lastResult, _runtimeSession, RuntimeSessionStore.ScenesInScope());

        /// <summary>What the list, the counts and the filters describe: the static scan, on its own.</summary>
        private ScanResult ListResult() => _lastResult;

        /// <summary>
        /// Says which evidence the headline score was computed from.
        ///
        /// This exists to defuse a perverse incentive: folding runtime findings in means that sampling your game can
        /// LOWER your score, which reads as "diagnosing made things worse" and teaches people not to measure. Naming
        /// the scope turns a mysterious drop into an expected one — the score covers more of the truth than it did
        /// before. It also stops a static-only score from being mistaken for a verdict on how the game actually runs.
        /// </summary>
        private string RuntimeScopeNote()
        {
            if (RuntimeSessionApplies())
            {
                double mins = (DateTime.UtcNow - _runtimeSession.CapturedAtUtc).TotalMinutes;
                string when = mins < 1 ? L.Tr("just now", "刚刚")
                    : mins < 60 ? L.Tr($"{mins:0}m ago", $"{mins:0} 分钟前")
                    : L.Tr($"{mins / 60:0}h ago", $"{mins / 60:0} 小时前");
                return L.Tr($" · includes runtime measurement ({when})", $" · 含运行时实测（{when}）");
            }

            // A session exists but was taken elsewhere — say so rather than silently ignoring it, or the user is left
            // wondering why the sampling they just did had no effect on anything.
            if (_runtimeSession != null && _runtimeSession.Findings.Count > 0)
                return L.Tr(" · static only (runtime measurement was taken in another scene)",
                            " · 仅静态口径（运行时实测采自其他场景）");

            return L.Tr(" · static only — sample in Play Mode to include runtime evidence",
                        " · 仅静态口径——在 Play Mode 采样可计入运行时证据");
        }

        // ── "Do this next" card ───────────────────────────────

        private VisualElement _nextStepsCard;

        const string PrefGoalFps = "PerfLint.Goal.Fps";

        /// <summary>Read through the shared owner, so the two windows cannot disagree about what is being aimed at.</summary>
        private PerfGoal CurrentGoal => PerfGoalPrefs.Current;

        /// <summary>The measured facts behind the ranking, or none when no applicable sampling session exists.</summary>
        private PerfMeasurement CurrentMeasurement() =>
            RuntimeSessionApplies() ? _runtimeSession.ToMeasurement() : PerfMeasurement.None;

        /// <summary>
        /// Thin drawn accent bar. Never an emoji — the 2021/2022 editor fonts have no glyph for those.
        ///
        /// No flexGrow: inside a row container that grows along the MAIN (horizontal) axis, which turned a 3px rule
        /// into a full-width block. The bar stretches vertically on its own because a row's default alignItems is
        /// Stretch — the cross axis is the one we want here.
        /// </summary>
        private static VisualElement Chip(Color c, float w = 3, float h = 0)
        {
            var v = new VisualElement { style = { width = w, backgroundColor = c, flexShrink = 0, marginRight = 8 } };
            if (h > 0) v.style.height = h;
            return v;
        }

        private void RenderNextSteps(ScanResult display)
        {
            if (_nextStepsCard == null) return;
            _nextStepsCard.Clear();

            if (display == null || display.Findings.Count == 0)
            {
                _nextStepsCard.style.display = DisplayStyle.None;
                return;
            }

            var goal = CurrentGoal;
            var measurement = CurrentMeasurement();
            var steps = NextSteps.Rank(display.Findings, goal, measurement);
            if (steps.Count == 0)
            {
                _nextStepsCard.style.display = DisplayStyle.None;
                return;
            }
            _nextStepsCard.style.display = DisplayStyle.Flex;

            // Row 1: the goal, editable in place. Thresholds are the user's choice, not ours to guess.
            var goalRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            goalRow.Add(new Label(L.Tr("My target", "我的目标"))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, marginRight = 8 }
            });

            // Button + GenericMenu rather than PopupField: PopupField<T> lives in UnityEditor.UIElements on 2021.3
            // and moved to UnityEngine.UIElements later, so referencing it breaks the package's minimum version.
            // GenericMenu is plain UnityEditor and behaves identically everywhere (caught by the version matrix).
            var fpsButton = new Button { text = $"{goal.TargetFps} FPS" };
            fpsButton.clicked += () =>
            {
                var menu = new GenericMenu();
                foreach (int rate in PerfGoalPrefs.FpsChoices)
                {
                    int captured = rate;
                    menu.AddItem(new GUIContent($"{captured} FPS"), captured == goal.TargetFps,
                        () => { EditorPrefs.SetInt(PrefGoalFps, captured); GoalChanged(); });
                }
                menu.ShowAsContext();
            };
            goalRow.Add(fpsButton);
            _nextStepsCard.Add(goalRow);

            // Row 2: where the project stands against that target — the context every step below is judged against.
            _nextStepsCard.Add(new Label(NextSteps.Headline(goal, measurement))
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, marginBottom = 6, fontSize = 11 }
            });

            // "Measure again and compare" does everything the plain measure button does and answers a question as
            // well, so the two must not be offered side by side — two buttons a word apart, one strictly better,
            // is a decision the user should never have to make.
            bool compareAvailable = CompareAvailable();
            _nextStepsCard.Add(BuildMeasureRow(measurement, compareAvailable && MeasurementBlockedReason() == null));

            // Urgency has to match the verdict. Once the target is met, presenting the top item the same way as when
            // the frame budget is blown claims a pressure that the measurement just disproved.
            var status = measurement.StatusAgainst(goal);
            _nextStepsCard.Add(new Label(status == FrameStatus.Meeting
                    ? L.Tr("Nothing here is blocking your target — worth doing when you get to it",
                           "没有阻挡目标的项——有空再做即可")
                    : L.Tr("Do this next", "下一步做这个"))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, opacity = 0.75f, marginBottom = 3 }
            });

            _nextStepsCard.Add(BuildPrimaryStep(steps[0]));

            if (steps.Count > 1)
            {
                _nextStepsCard.Add(new Label(L.Tr("After that", "接下来"))
                {
                    style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8, marginBottom = 2, fontSize = 11, opacity = 0.75f }
                });
                for (int i = 1; i < steps.Count; i++) _nextStepsCard.Add(BuildSecondaryStep(steps[i]));
            }

            // Verification sits AFTER the recommendation, because that is the order the user does it in: read what
            // to do, do it, then prove it worked. Above the recommendation it was four buttons standing between the
            // user and the one thing this card exists to tell them.
            _nextStepsCard.Add(BuildBenchmarkSection(goal));
        }

        /// <summary>
        /// Redraw after the user picks a different platform or target frame rate.
        ///
        /// Must go through the header, not just this card: the headline ("meeting your 60 FPS target, 11.3 ms to
        /// spare") and the budget ring are both computed from the goal, so re-rendering only the card left the big
        /// number on screen judging the project against a target the user had just changed away from. RenderHeader
        /// ends by rendering this card, so the card still updates.
        /// </summary>
        private void GoalChanged()
        {
            var display = ListResult();
            if (display != null) RenderHeader(display);
            else RenderNextSteps(null);
        }

        // The SessionState keys, and the decision about what a finished measurement is FOR, live in
        // BenchmarkIntent — once, shared with the Autopilot window. They used to be duplicated here and there,
        // and only this window ever read the intent back, so the other one silently never pinned a baseline.
        const string IntentBaseline = BenchmarkIntent.Baseline;
        const string IntentCompare = BenchmarkIntent.Compare;
        const string IntentPlain = BenchmarkIntent.Plain;

        /// <summary>Sampling length for the one-click measurement. Long enough to be steady, short enough that nobody minds pressing it.</summary>
        const float MeasureSampleSeconds = 20f;
        const float MeasureWarmupSeconds = 5f;

        /// <summary>
        /// Repetitions for a baseline. Three rather than two because the baseline is the side that has to supply the
        /// noise band: with a single run there is no spread at all and every later comparison can only answer "a
        /// number changed, no idea whether that means anything".
        /// </summary>
        const int BaselineRepetitions = 3;

        /// <summary>Repetitions for the "after" side. Two is enough — the baseline already carries the noise band.</summary>
        const int CompareRepetitions = 2;

        /// <summary>
        /// Rough wall-clock for a run set, including the Play Mode round-trip either side of each repetition.
        /// Deliberately coarse: "~74s" reads as a figure someone computed and is therefore wrong to the second,
        /// while "~70s" reads as the estimate it actually is.
        /// </summary>
        static string DurationEstimate(int repetitions)
        {
            int seconds = Mathf.RoundToInt(repetitions * (MeasureWarmupSeconds + MeasureSampleSeconds + 12f));
            if (seconds < 90)
            {
                int rounded = Mathf.Max(10, Mathf.RoundToInt(seconds / 10f) * 10);
                return L.Tr($"~{rounded}s", $"约 {rounded} 秒");
            }
            double mins = Math.Round(seconds / 30.0) / 2.0; // nearest half minute
            return L.Tr($"~{mins:0.#} min", $"约 {mins:0.#} 分钟");
        }

        /// <summary>Run count with the plural spelled out — "3 run(s)" is a placeholder someone forgot to finish.</summary>
        static string RunCountLabel(int n) => L.Tr(n == 1 ? "1 run" : $"{n} runs", $"{n} 轮");

        /// <summary>
        /// The "measure this scene" row.
        ///
        /// Exists because getting a trustworthy measurement by hand means remembering to turn VSync off, to skip the
        /// first seconds, to keep the Game view rendering, and to sample long enough — and getting any of it wrong
        /// produces a number that looks fine and isn't. A VSync-capped sample read as "you can't hit 60 FPS" on a
        /// machine that was doing 185. BenchmarkRunner already does all of that setup and puts it back afterwards;
        /// this just points a button at it.
        /// </summary>
        private VisualElement BuildMeasureRow(PerfMeasurement measurement, bool compareAvailable)
        {
            var box = new VisualElement();
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

            if (BenchmarkRunner.IsRunning)
            {
                box.style.marginBottom = 8;
                box.Add(row);

                // Two opposite instructions, so the phase decides which one is shown. Telling someone to keep still
                // while the run is waiting for them to play to the level would stall it until the watchdog fires.
                var p = BenchmarkRunner.CurrentProgress;
                string line = p.Repetitions > 1
                    ? L.Tr($"{p.Headline} · run {p.RunNumber}/{p.Repetitions}", $"{p.Headline} · 第 {p.RunNumber}/{p.Repetitions} 轮")
                    : p.Headline;
                row.Add(new Label(p.Phase == BenchmarkRunner.Phase.AwaitScene
                        ? L.Tr($"{line} — keep playing; sampling starts by itself",
                               $"{line} —— 继续玩就行，场景加载出来会自动开始采样")
                        : L.Tr($"{line} — stay in the Game view", $"{line} —— 请勿离开游戏窗口"))
                {
                    style = { fontSize = 11, opacity = 0.85f, flexGrow = 1, whiteSpace = WhiteSpace.Normal }
                });
                var cancel = new Button(CancelMeasurement) { text = L.Tr("Cancel", "取消") };
                row.Add(cancel);
                return box;
            }

            // Parked behind a compile or an import: say so instead of looking idle, or the click reads as ignored.
            if (HasPendingMeasurement)
            {
                box.style.marginBottom = 8;
                box.Add(row);
                row.Add(new Label(L.Tr("Waiting for your changes to finish importing, then measuring…",
                                       "正在等待改动导入/编译完成，随后开始测量……"))
                {
                    style = { fontSize = 11, opacity = 0.85f, flexGrow = 1, whiteSpace = WhiteSpace.Normal }
                });
                row.Add(new Button(CancelMeasurement) { text = L.Tr("Cancel", "取消") });
                return box;
            }

            // Suppressed when the baseline section below offers "measure again and compare": that button already
            // refreshes these figures, so keeping this one here would be the same action worded two ways.
            string blocked = MeasurementBlockedReason();
            if (blocked != null)
            {
                box.style.marginBottom = 8;
                box.Add(new Label(blocked)
                {
                    style = { fontSize = 11, opacity = 0.8f, whiteSpace = WhiteSpace.Normal }
                });
            }
            else if (!compareAvailable)
            {
                box.style.marginBottom = 8;
                box.Add(row);

                // Prominent when there is nothing usable to go on; a quiet refresh once there is.
                bool needsOne = !measurement.HasData || measurement.FrameRateCapped;
                var button = new Button(() => StartMeasurement(IntentPlain, 1))
                {
                    text = needsOne
                        ? L.Tr($"Measure this scene ({MeasureSampleSeconds:0}s)", $"测量当前场景（{MeasureSampleSeconds:0} 秒）")
                        : L.Tr("Measure again", "重新测量")
                };
                // Prominent only when there is nothing usable to go on; otherwise it is one action among several
                // and must not compete with Scan Project, which is this window's primary.
                if (needsOne) PerfLintStyle.AsPrimary(button);
                else PerfLintStyle.AsSecondary(button);
                button.style.marginRight = 8;
                row.Add(button);

                row.Add(new Label(MeasurementModeNote()
                                  ?? L.Tr("Enters Play Mode, turns VSync off for the measurement, and puts your settings back.",
                                          "会进入 Play Mode，测量期间临时关闭 VSync，结束后还原你的设置。"))
                {
                    style = { fontSize = 10, opacity = 0.55f, whiteSpace = WhiteSpace.Normal, flexGrow = 1 }
                });
            }

            // A measurement describes the project as it was when it was taken. Once things have changed underneath it,
            // continuing to present it as current is the quiet way this whole feature becomes untrustworthy.
            string stale = StaleMeasurementNote();
            if (stale != null)
            {
                box.style.marginBottom = 8;
                box.Add(new Label(stale)
                {
                    style = { fontSize = 10, opacity = 0.7f, whiteSpace = WhiteSpace.Normal, marginTop = 3 }
                });
            }

            return box;
        }

        /// <summary>
        /// Why no measurement can be started right now, or null when one can.
        ///
        /// Asked BEFORE any measure button is drawn, not after it is clicked. The Deep Profile case is the one that
        /// makes this necessary rather than merely nicer: the top recommendation in this very card can be
        /// "enable Deep Profile to pinpoint the source", and a user who follows that advice would then find the only
        /// measurement button in front of them refusing to run. Saying so in place turns a wall into an instruction.
        /// </summary>
        private static string MeasurementBlockedReason()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                return L.Tr("Leave Play Mode to measure — a measurement drives Play Mode itself.",
                            "要测量请先退出 Play Mode——测量本身会自己进出 Play Mode。");

            // Deep Profile no longer blocks. It ruins wall-clock time and the comparison drops every timing row for
            // it, but it is the only way to get per-method markers — so it is a trade to be stated, not refused. What
            // it is NOT allowed to do is quietly become a frame-rate verdict; that is handled by FrameStatus.Inflated.
            return null;
        }

        /// <summary>What a measurement taken right now will and won't be able to answer. Delegated so the caption above a button and the runner behind it cannot disagree.</summary>
        private static string MeasurementModeNote() => BenchmarkRunner.MeasurementModeNote();

        /// <summary>
        /// Offers to re-measure straight after fixes were applied.
        ///
        /// This is the single most valuable thing in the loop, and not for convenience. The bar a verdict has to clear
        /// is how far the numbers drift, and drift grows with the gap: measured on one machine, ten hours moved frame
        /// time 1-2% while a comparison taken minutes after a change has only the ~0.4% of run-to-run spread to beat.
        /// Left to find the button themselves, the user measured ten hours later and the answer was unprovable. Asked
        /// right now, the same change is provable.
        ///
        /// Asked rather than done: it drives Play Mode, which is intrusive enough to need a yes.
        /// </summary>
        private void OfferReMeasureAfterFix(int fixCount)
        {
            if (fixCount <= 0 || _baseline == null) return;
            if (BenchmarkRunner.IsRunning || HasPendingMeasurement) return;
            if (!BaselineDescribesSceneToMeasure() || MeasurementBlockedReason() != null) return;

            if (!EditorUtility.DisplayDialog(
                    L.Tr("Measure now?", "现在复测？"),
                    L.Tr($"{fixCount} fix(es) applied. Measuring right now is what makes the result provable: the longer you wait, the more this machine drifts on its own, and a real improvement has to beat that drift before PerfLint can call it one.\n\nTakes about {DurationEstimate(CompareRepetitions)}, and starts once the editor finishes importing.",
                         $"已应用 {fixCount} 项修复。现在立刻复测才能让结果站得住：等得越久，这台机器自己漂得越多，而真实改善必须超过这个漂移才能被判定为改善。\n\n约需{DurationEstimate(CompareRepetitions)}，会在编辑器导入完成后自动开始。"),
                    L.Tr("Measure now", "立刻复测"), L.Tr("Later", "稍后")))
                return;

            StartMeasurement(IntentCompare, CompareRepetitions, confirmed: true);
        }

        /// <summary>Names the changes recorded since the displayed measurement was taken, or null when there are none.</summary>
        private string StaleMeasurementNote()
        {
            if (!RuntimeSessionApplies()) return null;
            int n = ProjectEditJournal.CountSince(_runtimeSession.CapturedAtUtc);
            if (n <= 0) return null;

            string what = ProjectEditJournal.SummarySince(_runtimeSession.CapturedAtUtc);
            // No "change(s)" and no parentheses inside parentheses: the summary already reads as a list, so it goes
            // after a colon rather than being wrapped in brackets that then have to nest.
            string count = n == 1 ? L.Tr("1 recorded change", "1 处已记录的改动")
                                  : L.Tr($"{n} recorded changes", $"{n} 处已记录的改动");
            return L.Tr($"⚠ Measured before {count}: {what}. Measure again to see their effect.",
                        $"⚠ 本次测量早于其后的{count}：{what}。重新测量才能看到它们的效果。");
        }

        // ── Baseline & before/after ───────────────────────────
        //
        // Cached rather than read on every render: RenderNextSteps runs twice a second while a measurement is in
        // flight, and re-reading a baseline plus a session directory off disk at that rate is a lot of IO for data
        // that only changes when a measurement completes.

        /// <summary>
        /// The verify-loop state, shared with the Autopilot window rather than reimplemented here.
        ///
        /// Shared because loading it has a side effect: a comparison with no recorded fixes is filed as a drift
        /// reading. Two windows with their own copies of that rule would be two places for it to drift out of
        /// agreement about which comparison counts as an observation.
        /// </summary>
        private BenchmarkVerifyState _verify = new BenchmarkVerifyState();

        /// <summary>Signature of what the baseline section is currently drawn from, so a focus can tell whether it needs redrawing.</summary>
        private string _benchmarkStateKey;

        private BenchmarkBaseline.Pinned _baseline => _verify.Baseline;
        private BenchmarkSession _afterSession => _verify.After;

        /// <summary>Re-reads the verify state. Returns true when what is on screen would now differ.</summary>
        private bool ReloadBenchmarkState()
        {
            _verify = BenchmarkVerifyState.Load();
            bool changed = !string.Equals(_verify.Signature, _benchmarkStateKey, StringComparison.Ordinal);
            _benchmarkStateKey = _verify.Signature;
            return changed;
        }

        private VisualElement BuildBenchmarkSection(PerfGoal goal)
        {
            var box = new VisualElement();
            if (BenchmarkRunner.IsRunning || HasPendingMeasurement) return box;

            // Divider, added last (see the tail of this method) so it only appears when there is something below it.
            // Without one, "Baseline: … / Clear" sits flush against the "After that" list and reads as a third
            // recommendation with a button on the right, which is what the first screenshot of it looked like.
            if (_baseline == null)
            {
                // Offered only once there is a measurement to build on — before that, "Measure this scene" is the
                // one thing to do, and a second measurement button next to it just splits the decision.
                if (!RuntimeSessionApplies() || MeasurementBlockedReason() != null) return box;

                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
                row.Add(new Button(() => StartMeasurement(IntentBaseline, BaselineRepetitions))
                {
                    text = L.Tr($"Set a baseline ({BaselineRepetitions} runs, {DurationEstimate(BaselineRepetitions)})",
                                $"建立基线（{BaselineRepetitions} 轮，{DurationEstimate(BaselineRepetitions)}）"),
                    style = { marginRight = 8 }
                });
                row.Add(new Label(L.Tr("Records how this scene runs now, so PerfLint can prove whether your next change actually helped.",
                                       "记录当前场景的表现，之后 PerfLint 才能证明你的改动到底有没有用。"))
                {
                    style = { fontSize = 10, opacity = 0.55f, whiteSpace = WhiteSpace.Normal, flexGrow = 1 }
                });
                box.Add(row);
                return WithDivider(box);
            }

            // ── A baseline exists ──
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            double ms = _baseline.FrameMsMedian;
            string when = Ago(_baseline.PinnedAtUtc);
            string runs = RunCountLabel(_baseline.RunCount);
            string figure = double.IsNaN(ms)
                ? L.Tr("frame time unavailable", "无帧时间")
                : L.Tr($"{ms:0.00} ms/frame", $"每帧 {ms:0.00} ms");
            header.Add(new Label(L.Tr($"Baseline: {_baseline.SceneName} · {figure} · {runs} · {when}",
                                      $"基线：{_baseline.SceneName} · {figure} · {runs} · {when}"))
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, flexGrow = 1, whiteSpace = WhiteSpace.Normal }
            });
            header.Add(new Button(() =>
            {
                BenchmarkBaseline.Clear();
                ReloadBenchmarkState();
                RenderNextSteps(DisplayResult());
            })
            { text = L.Tr("Clear", "清除") });
            box.Add(header);

            if (!_baseline.HasNoiseBand)
                box.Add(new Label(L.Tr($"⚠ This baseline has a single run, so there is no run-to-run spread to judge a change against. Set it again with {BaselineRepetitions} runs to get verdicts instead of raw numbers.",
                                       $"⚠ 该基线只有一轮，没有 run-to-run 波动范围可作判据。用 {BaselineRepetitions} 轮重新建立基线，才能给出结论而不只是原始数字。"))
                {
                    style = { fontSize = 10, opacity = 0.75f, whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
                });

            // Having repetitions is not the same as having repetitions that agree. This says which figures the
            // baseline cannot answer for, at the point where it is offered — not after the work, on the row.
            string unstable = _baseline.StabilityWarning;
            if (unstable != null)
                box.Add(new Label("⚠ " + unstable)
                {
                    style = { fontSize = 10, opacity = 0.75f, whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
                });

            // A baseline is a measurement OF A SCENE. Offering "measure again and compare" while a different scene is
            // open would spend a minute of the user's time to arrive at "different scene — can't compare", so the
            // state is caught here instead of after the measurement. This is the same class of mistake as shipping a
            // one-click action for a package the engine will refuse to remove.
            bool sceneMatches = BaselineDescribesSceneToMeasure();

            // Same rule for a condition that makes ANY measurement meaningless: say it here rather than behind a
            // click. Keeping the baseline summary visible matters — it is still the thing being kept.
            string blocked = MeasurementBlockedReason();
            if (blocked != null)
            {
                box.Add(new Label(blocked)
                {
                    style = { fontSize = 10, opacity = 0.75f, whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
                });
                return WithDivider(box);
            }

            // Cold start: with no drift observed and nothing measured since the baseline, the most valuable thing the
            // user can do is measure again WITHOUT changing anything. It costs the same 70 seconds as any comparison
            // and it is what makes every later verdict mean something, so it takes the button rather than being a
            // suggestion in grey text underneath one.
            bool needsCalibration = !_verify.Drift.HasData && _afterSession == null;

            var actions = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            if (sceneMatches)
                actions.Add(new Button(() => StartMeasurement(IntentCompare, CompareRepetitions))
                {
                    text = needsCalibration
                        ? L.Tr($"Measure again, changing nothing ({DurationEstimate(CompareRepetitions)})",
                               $"什么都不改，再测一次（{DurationEstimate(CompareRepetitions)}）")
                        : L.Tr($"Measure again and compare ({DurationEstimate(CompareRepetitions)})",
                               $"重新测量并对比（{DurationEstimate(CompareRepetitions)}）"),
                    style = { marginRight = 8 }
                });
            actions.Add(new Button(() => StartMeasurement(IntentBaseline, BaselineRepetitions))
            { text = L.Tr("Replace baseline", "重设基线") });
            actions.Add(new Label(L.Tr("Enters Play Mode, turns VSync off for the measurement, and puts your settings back.",
                                       "会进入 Play Mode，测量期间临时关闭 VSync，结束后还原你的设置。"))
            {
                style = { fontSize = 10, opacity = 0.55f, whiteSpace = WhiteSpace.Normal, flexGrow = 1, marginLeft = 8 }
            });
            box.Add(actions);

            if (!sceneMatches)
            {
                box.Add(new Label(L.Tr($"Your baseline was measured in {_baseline.SceneName}, which isn't the scene that's open. Open it again to compare, or re-baseline this scene.",
                                       $"该基线测自场景 {_baseline.SceneName}，与当前打开的场景不同。重新打开它才能对比，或对当前场景重设基线。"))
                {
                    style = { fontSize = 10, opacity = 0.7f, whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
                });
                return WithDivider(box);
            }

            // Rebuilt here rather than cached with the sessions, so switching the target frame rate in the row above
            // immediately re-judges the goal line instead of leaving it describing the goal you just changed away
            // from. Pure computation over a handful of runs — nothing is read from disk.
            if (_afterSession != null)
            {
                // Through the state object rather than assembling the arguments here. The two calls had drifted: this
                // one passed neither UserEdits nor the rules fixed since the baseline, so the main panel and the
                // Autopilot could report the same two measurements differently — a hand-edited script read as drift
                // here and as a result there, and the highlighted figures were not the ones the round was aimed at.
                box.Add(BuildComparisonBlock(_verify.BuildReport(goal)));
                return WithDivider(box);
            }

            // A baseline with nothing to compare against yet is two buttons and no story. The first sentence states
            // the state outright rather than leaving an empty gap: a gap looks identical whether nothing has been
            // measured or a comparison failed to appear, and those need telling apart.
            box.Add(new Label(needsCalibration
                ? L.Tr("Nothing has been measured since this baseline. Before you change anything, measure once more: with nothing edited, whatever difference shows up IS this machine's drift, and every later verdict has to beat it. Without that, a 1% reading cannot be told from a real change.",
                       "自建立基线以来还没有新的测量。在动手改之前先再测一次：什么都没改的情况下，出现的任何差异就是本机的漂移，之后的每一次判定都必须超过它。没有这一步，1% 的读数无法与真实改动区分。")
                : L.Tr("Nothing has been measured since this baseline. Make your changes, then measure again — the two are compared under identical conditions, and a difference smaller than this machine's own variation is reported as no measurable change rather than as a win.",
                       "自建立基线以来还没有新的测量。改完之后再测一次——两次测量会在同等条件下对比；小于本机自身波动的差异会如实报为「无可测出的变化」，而不是算作改善。"))
            {
                style = { fontSize = 10, opacity = 0.7f, whiteSpace = WhiteSpace.Normal }
            });

            return WithDivider(box);
        }

        /// <summary>
        /// Rules the baseline block off from the recommendation list above it. Prepended rather than declared up
        /// front so that a section which ends up empty does not leave a stray line behind.
        /// </summary>
        private static VisualElement WithDivider(VisualElement box)
        {
            if (box.childCount == 0) return box;
            box.Insert(0, new VisualElement
            {
                style =
                {
                    height = 1,
                    backgroundColor = PerfLintStyle.Hair,
                    marginTop = 10,
                    marginBottom = 8
                }
            });
            return box;
        }

        /// <summary>
        /// Whether the "measure again and compare" path is on offer right now. Read before the measure row is built,
        /// because that row hides its own button when this is true.
        /// </summary>
        private bool CompareAvailable() =>
            _baseline != null && !BenchmarkRunner.IsRunning && !HasPendingMeasurement && BaselineDescribesSceneToMeasure();

        /// <summary>
        /// Whether the pinned baseline describes the scene the editor currently has open. Compared by GUID, like the
        /// fingerprint: renaming a scene file must not silently invalidate a baseline that is still about that scene.
        /// </summary>
        private bool BaselineDescribesSceneToMeasure() => _verify.BaselineDescribesSceneToMeasure();

        // Outcome colours. Green for a proved win, amber for "couldn't prove it" (a real answer, not an error), red
        // for a regression, neutral grey for a drift reading — which is information, not a result.
        private static Color OutcomeTint(BenchmarkComparison.Outcome o) => o switch
        {
            BenchmarkComparison.Outcome.Proved => PerfLintStyle.Good,
            BenchmarkComparison.Outcome.Worse => PerfLintStyle.Bad,
            BenchmarkComparison.Outcome.Unproven => PerfLintStyle.Amber,
            // A clean null comparison is the calibration working, not a failure to prove anything.
            BenchmarkComparison.Outcome.Calibrated => PerfLintStyle.Good,
            _ => PerfLintStyle.Dimmer
        };

        /// <summary>
        /// The before/after result.
        ///
        /// Leads with one sentence, then three bars, and puts everything else behind two disclosures. The previous
        /// version showed thirteen rows with a caveat repeated inline on three of them, which was an instrument panel:
        /// for a reader who does not already know which counters matter, ten rows of "no measurable change" reads as
        /// "the tool failed" rather than as an honest answer. The rigour did not go anywhere — it decides what the one
        /// sentence is allowed to say.
        /// </summary>
        private VisualElement BuildComparisonBlock(BenchmarkComparison.Report report)
        {
            var box = new VisualElement { style = { marginBottom = 8 } };
            var tint = OutcomeTint(report.Result);

            // Headline, with a drawn colour bar rather than an icon — the editor fonts on 2021/2022 have no emoji.
            var head = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 4 } };
            head.Add(Chip(tint, 3));
            head.Add(new Label(report.Headline)
            {
                style = { fontSize = 12, whiteSpace = WhiteSpace.Normal, flexGrow = 1,
                          unityFontStyleAndWeight = FontStyle.Bold }
            });
            box.Add(head);

            if (!report.HasComparison)
            {
                // A refusal with no way forward is a dead end. Both exits are real: restore what changed, or accept
                // where you are now as the new "before".
                box.Add(new Label(L.Tr("Put the earlier conditions back and measure again, or replace the baseline to start from where you are now.",
                                       "把之前的条件改回去再测一次，或直接以当前状态重设基线。"))
                {
                    style = { fontSize = 10, opacity = 0.7f, whiteSpace = WhiteSpace.Normal, marginLeft = 11 }
                });
                return box;
            }

            // Work this measurement structurally cannot see, said before the figures: "no measurable change" after a
            // build-size round is a property of the question, not an answer about the work.
            if (report.RoundWasInvisible)
                box.Add(new Label(report.BlindRoundNote)
                {
                    style = { fontSize = 10.5f, color = PerfLintStyle.Amber, whiteSpace = WhiteSpace.Normal,
                              marginLeft = 11, marginBottom = 8 }
                });

            // What may be believed, and what to do about what may not. The Unproven case needs this most.
            if (!string.IsNullOrEmpty(report.Advice))
                box.Add(new Label(report.Advice)
                {
                    style = { fontSize = 10.5f, opacity = 0.75f, whiteSpace = WhiteSpace.Normal,
                              marginLeft = 11, marginBottom = 8 }
                });

            foreach (var r in report.Highlights) box.Add(BuildComparisonBar(r));

            // Provenance compressed to one line. It used to be four stacked paragraphs of grey text above the table.
            string provenance = string.Join(" · ", new[]
            {
                report.GapLine, report.ChangesLine == null ? null
                    : L.Tr($"changed in between: {report.ChangesLine}", $"期间改动：{report.ChangesLine}")
            }.Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrEmpty(provenance))
                box.Add(new Label(provenance)
                {
                    style = { fontSize = 10, opacity = 0.55f, whiteSpace = WhiteSpace.Normal, marginTop = 8 }
                });

            box.Add(BuildHotspotFoldout(report));
            box.Add(BuildFiguresFoldout(report));
            box.Add(BuildJudgementFoldout(report));
            return box;
        }

        /// <summary>
        /// The same comparison, call path by call path.
        ///
        /// Separate from the counter table because it answers a different question — the counters say whether the
        /// frame got cheaper, this says which code stopped costing — and because the hit-rate column is the one figure
        /// in the whole comparison that describes the project rather than this machine.
        /// </summary>
        private VisualElement BuildHotspotFoldout(BenchmarkComparison.Report report)
        {
            var hs = report.HotspotResult;

            if (hs == null || !hs.HasRows)
            {
                // Never an empty box: "we did not collect this" must not look the same as "nothing was going on".
                var why = new Label(string.IsNullOrEmpty(hs?.Blocker)
                    ? L.Tr("No call-path comparison for these two measurements.", "这两次测量没有可用的调用路径对比。")
                    : L.Tr($"No call-path comparison: {hs.Blocker}.", $"没有调用路径对比：{hs.Blocker}。"))
                {
                    style = { fontSize = 10, opacity = 0.5f, whiteSpace = WhiteSpace.Normal, marginTop = 4 }
                };
                return why;
            }

            var fold = new Foldout
            {
                text = L.Tr($"Where the time went — {hs.Rows.Count} call paths, {hs.MovedCount} moved",
                            $"时间花在哪里 —— {hs.Rows.Count} 条调用路径，其中 {hs.MovedCount} 条有变化"),
                value = hs.MovedCount > 0
            };
            fold.style.fontSize = 10;
            fold.style.marginTop = 4;

            fold.Add(new Label(L.Tr("Milliseconds are this machine's. How often a path runs is your code's — that column is the one that carries to a device.",
                                    "毫秒数是这台机器的口径；一条路径多久跑一次则是你代码的属性——那一列才是能带到设备上的。"))
            {
                style = { fontSize = 10, opacity = 0.55f, whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
            });

            foreach (var r in hs.Rows) fold.Add(BuildHotspotRow(r));
            return fold;
        }

        private VisualElement BuildHotspotRow(HotspotComparison.Row r)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

            bool good = r.Improved || r.Hit == HotspotComparison.HitChange.Fell;
            bool bad = r.Regressed || r.Hit == HotspotComparison.HitChange.Rose;
            row.Add(Chip(good ? PerfLintStyle.Good
                       : bad ? PerfLintStyle.Bad
                       : PerfLintStyle.Dimmer, 3, 11));

            var name = new Label(r.Marker)
            {
                style = { width = 175, flexShrink = 0, fontSize = 10.5f, overflow = Overflow.Hidden }
            };
            // Clicking a hotspot that maps to a script is the whole point of attributing it in the first place.
            if (r.IsScript)
            {
                string path = r.ScriptPath;
                name.tooltip = path;
                name.style.color = PerfLintStyle.Accent;
                name.RegisterCallback<MouseDownEvent>(_ => ScannerUtil.OpenScript(path, null));
            }
            row.Add(name);

            row.Add(new Label(r.PairText) { style = { width = 130, flexShrink = 0, fontSize = 10.5f } });
            row.Add(new Label(r.DeltaText)
            { style = { width = 52, flexShrink = 0, fontSize = 10.5f, unityTextAlign = TextAnchor.MiddleRight, marginRight = 10 } });

            // Absence is a drop out of a truncated list, never a measured zero — said in the row, not only in a note.
            string tail = r.Presence switch
            {
                HotspotComparison.Presence.DroppedOut => L.Tr("dropped off the list (not measured as zero)", "掉出榜单（并非测到零）"),
                HotspotComparison.Presence.Appeared => L.Tr("new to the list", "新进入榜单"),
                _ => r.HitText ?? L.Tr("no hit rate recorded", "未记录命中率")
            };
            row.Add(new Label(tail)
            {
                style = { fontSize = 10.5f, flexGrow = 1, opacity = r.Moved || r.Hit == HotspotComparison.HitChange.Fell ? 0.95f : 0.6f }
            });
            return row;
        }

        /// <summary>
        /// One figure as a before/after pair of bars.
        ///
        /// Bars rather than two numbers and a percentage because the question is "did it get better", and a shorter bar
        /// answers that without the reader having to work out which direction is good for this counter.
        /// </summary>
        private VisualElement BuildComparisonBar(BenchmarkComparison.MetricRow r)
        {
            var wrap = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };

            var name = new VisualElement { style = { width = 150, flexShrink = 0 } };
            name.Add(new Label(r.ShortLabel) { style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold } });
            name.Add(new Label(r.PairText) { style = { fontSize = 10, opacity = 0.6f } });
            wrap.Add(name);

            // Only a movement somebody caused may wear the colour of a good or bad result. A drift reading moved in the
            // better direction without anybody doing anything, and green there says "something good just happened".
            bool good = r.Improved && r.CountsAsResult;
            bool bad = r.Regressed && r.CountsAsResult;
            var neutral = PerfLintStyle.Dimmer;
            var accent = good ? PerfLintStyle.Good : bad ? PerfLintStyle.Bad : neutral;

            var mid = new VisualElement { style = { flexGrow = 1, marginRight = 10 } };
            if (r.BarIsLegible)
            {
                mid.Add(Track(r.BeforeFraction, PerfLintStyle.Fade(PerfLintStyle.Dimmer, 0.75f)));
                mid.Add(Track(r.AfterFraction, accent));
            }
            else
            {
                // Two bars differing by a couple of percent of their width say nothing while looking like they should.
                mid.Add(new Label(L.Tr("difference too small to draw", "差异过小，画不出来"))
                {
                    style = { fontSize = 10, opacity = 0.4f }
                });
            }
            wrap.Add(mid);

            var verdict = new VisualElement { style = { width = 132, flexShrink = 0 } };
            verdict.Add(new Label(string.IsNullOrEmpty(r.DeltaText) ? "—" : r.DeltaText)
            {
                style = { fontSize = 11, unityTextAlign = TextAnchor.MiddleRight,
                          unityFontStyleAndWeight = r.Moved ? FontStyle.Bold : FontStyle.Normal,
                          color = good ? PerfLintStyle.Good
                                : bad ? PerfLintStyle.Bad
                                : PerfLintStyle.Dim }
            });
            verdict.Add(new Label(r.VerdictText)
            {
                style = { fontSize = 10, opacity = 0.6f, unityTextAlign = TextAnchor.MiddleRight,
                          whiteSpace = WhiteSpace.Normal }
            });
            wrap.Add(verdict);

            return wrap;
        }

        /// <summary>A bar in a track. Drawn, so it cannot depend on a glyph the editor font may not have.</summary>
        private static VisualElement Track(double fraction, Color fill)
        {
            var track = new VisualElement
            {
                style = { height = 7, marginBottom = 2, backgroundColor = PerfLintStyle.Track }
            };
            track.Add(new VisualElement
            {
                style = { height = 7, width = Length.Percent(Mathf.Clamp01((float)fraction) * 100f), backgroundColor = fill }
            });
            return track;
        }

        /// <summary>Every figure, grouped by what it is a property of. The grouping is what replaced a caveat repeated per row.</summary>
        private VisualElement BuildFiguresFoldout(BenchmarkComparison.Report report)
        {
            var fold = new Foldout
            {
                text = L.Tr($"All {report.Rows.Count} figures — {report.MovedCount} moved",
                            $"全部 {report.Rows.Count} 项 —— 其中 {report.MovedCount} 项有变化"),
                value = false
            };
            fold.style.fontSize = 10;
            fold.style.marginTop = 4;

            foreach (var group in report.ByScope())
            {
                fold.Add(new Label($"{BenchmarkMetricKeys.ScopeTitle(group.Key)} — {BenchmarkMetricKeys.ScopeWhy(group.Key)}")
                {
                    style = { fontSize = 10, opacity = 0.55f, whiteSpace = WhiteSpace.Normal, marginTop = 6, marginBottom = 2 }
                });
                foreach (var r in group.Value) fold.Add(BuildFigureRow(r));
            }

            // The one caveat that is genuinely about a pair of rows rather than a group, so it lives under the table.
            foreach (var r in report.Rows)
                if (r.Key == BenchmarkMetricKeys.GcPerFrameBytes && !string.IsNullOrEmpty(r.Note))
                {
                    fold.Add(new Label(r.Note)
                    {
                        style = { fontSize = 10, opacity = 0.5f, whiteSpace = WhiteSpace.Normal, marginTop = 6 }
                    });
                    break;
                }

            return fold;
        }

        /// <summary>One table row: fixed columns, single line, no inline note. Uniform height by construction.</summary>
        private VisualElement BuildFigureRow(BenchmarkComparison.MetricRow r)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.Add(Chip(r.Improved && r.CountsAsResult ? PerfLintStyle.Good
                       : r.Regressed && r.CountsAsResult ? PerfLintStyle.Bad
                       : PerfLintStyle.Dimmer, 3, 11));
            row.Add(new Label(r.ShortLabel) { style = { width = 145, flexShrink = 0, fontSize = 10.5f, opacity = 0.75f } });
            row.Add(new Label(r.PairText) { style = { width = 195, flexShrink = 0, fontSize = 10.5f } });
            row.Add(new Label(string.IsNullOrEmpty(r.DeltaText) ? "" : r.DeltaText)
            { style = { width = 56, flexShrink = 0, fontSize = 10.5f, unityTextAlign = TextAnchor.MiddleRight, marginRight = 10 } });
            row.Add(new Label(r.VerdictText)
            {
                style = { fontSize = 10.5f, flexGrow = 1, opacity = r.Moved ? 1f : 0.6f,
                          unityFontStyleAndWeight = r.Moved ? FontStyle.Bold : FontStyle.Normal }
            });
            return row;
        }

        /// <summary>Where the bar a verdict had to clear came from, and the readings behind it. Not on the first screen — it explains the answer rather than being it.</summary>
        private VisualElement BuildJudgementFoldout(BenchmarkComparison.Report report)
        {
            var fold = new Foldout { text = L.Tr("How this was judged", "这个结论是怎么得出的"), value = false };
            fold.style.fontSize = 10;

            string provenance = string.Join(" ", new[] { report.GapLine, report.CalibrationLine }
                .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrEmpty(provenance))
            {
                var line = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, marginBottom = 3 } };
                line.Add(new Label(provenance)
                {
                    style = { fontSize = 10, opacity = report.DriftCalibrated ? 0.6f : 0.8f,
                              whiteSpace = WhiteSpace.Normal, flexGrow = 1 }
                });

                // A drift sample can be poisoned by a condition we did not know to record — a second Game view
                // rendering the scene again was worth 10% on one machine, and until that became a fingerprint field
                // it was banked as permanent machine drift. Learned figures have to be discardable, or one bad
                // sample silences the verdicts for good.
                if (report.DriftCalibrated)
                {
                    var reset = new Button(() =>
                    {
                        if (!EditorUtility.DisplayDialog(
                                L.Tr("Forget measured drift", "清除已测漂移"),
                                L.Tr("Discards what PerfLint learned about how much this scene's numbers move on their own. Worth doing if something about the machine or the editor layout changed — a second Game view, for instance, renders the scene twice and inflates the figure.\n\nThe next comparison with nothing changed will measure it again.",
                                     "清除 PerfLint 已学到的「本场景数字自行漂移多少」。若机器或编辑器布局有变化，建议清除——例如多开一个 Game 视图会把场景渲染两遍、把这个数字抬高。\n\n下一次「什么都不改」的对比会重新测量它。"),
                                L.Tr("Forget", "清除"), L.Tr("Cancel", "取消")))
                            return;

                        BenchmarkDrift.Clear(_baseline?.Session?.Fingerprint?.sceneGuid);
                        ReloadBenchmarkState();
                        RenderNextSteps(DisplayResult());
                    })
                    { text = L.Tr("Forget all", "全部清除"), style = { flexShrink = 0, marginLeft = 6 } };
                    line.Add(reset);
                }

                fold.Add(line);
                if (report.DriftCalibrated) fold.Add(BuildDriftSamples());
            }

            return fold;
        }

        /// <summary>
        /// The individual drift readings the band was derived from, each droppable.
        ///
        /// The band is a maximum, so ONE sample decides it, and shown as a single percentage there is no way to tell
        /// a machine that wanders 10% from one reading contaminated by a condition we did not know to record. Real
        /// case: three samples read +10.0%, +0.8% and −1.7% — the first was a second Game view rendering the scene
        /// again, and on its own it gated every verdict at ±10% while the true figure was under 2%.
        /// </summary>
        private VisualElement BuildDriftSamples()
        {
            // Scoped to the baseline in force: readings taken against a replaced one are about a different reference
            // point and would be listed as if they set the current bar.
            var samples = BenchmarkDrift.Samples(_verify.SceneGuid, _verify.BaselineTicks);
            var fold = new Foldout
            {
                text = L.Tr($"Drift readings ({samples.Count})", $"漂移读数（{samples.Count} 条）"),
                value = false
            };
            fold.style.fontSize = 10;
            fold.style.marginBottom = 3;

            foreach (var s in samples)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

                double frame = BenchmarkDrift.ValueOf(s, BenchmarkMetricKeys.FrameTimeMs);
                var worst = BenchmarkDrift.Worst(s);

                string gap = s.gapMinutes < 90
                    ? L.Tr($"{s.gapMinutes:0} min gap", $"间隔 {s.gapMinutes:0} 分钟")
                    : L.Tr($"{s.gapMinutes / 60:0.#} h gap", $"间隔 {s.gapMinutes / 60:0.#} 小时");

                string frameText = double.IsNaN(frame)
                    ? L.Tr("frame time not recorded", "未记录帧时间")
                    : L.Tr($"frame time {(frame < 0 ? "−" : "+")}{Math.Abs(frame):0.0}%",
                           $"帧时间 {(frame < 0 ? "−" : "+")}{Math.Abs(frame):0.0}%");

                string worstText = worst.HasValue
                    ? L.Tr($" · largest: {BenchmarkMetricKeys.Label(worst.Value.Key)} {worst.Value.Value:0.0}%",
                           $" · 最大：{BenchmarkMetricKeys.Label(worst.Value.Key)} {worst.Value.Value:0.0}%")
                    : "";

                row.Add(new Label($"{Ago(s.AtUtc)} · {gap} · {frameText}{worstText}")
                {
                    style = { fontSize = 10, opacity = 0.7f, flexGrow = 1, whiteSpace = WhiteSpace.Normal }
                });

                string id = s.afterSessionId;
                row.Add(new Button(() =>
                {
                    BenchmarkDrift.Remove(id);
                    ReloadBenchmarkState();
                    RenderNextSteps(DisplayResult());
                })
                { text = L.Tr("Drop", "丢弃"), style = { flexShrink = 0 } });

                fold.Add(row);
            }

            fold.Add(new Label(L.Tr("A reading far out of line with the others usually means a condition changed rather than the machine drifting — a second Game view, another program on the CPU. Drop it and the band tightens.",
                                    "某一条与其他明显不同，通常意味着当时有条件变了、而不是机器在漂移——比如多开了一个 Game 视图、或另有程序在占 CPU。丢弃它，噪声带就会收紧。"))
            {
                style = { fontSize = 10, opacity = 0.5f, whiteSpace = WhiteSpace.Normal, marginTop = 2 }
            });

            return fold;
        }

        private static string Ago(DateTime utc)
        {
            double mins = (DateTime.UtcNow - utc).TotalMinutes;
            if (mins < 1) return L.Tr("just now", "刚刚");
            if (mins < 60) return L.Tr($"{mins:0}m ago", $"{mins:0} 分钟前");
            if (mins < 60 * 24) return L.Tr($"{mins / 60:0}h ago", $"{mins / 60:0} 小时前");
            return L.Tr($"{mins / 1440:0}d ago", $"{mins / 1440:0} 天前");
        }

        // ── Driving a measurement ─────────────────────────────

        private bool HasPendingMeasurement => BenchmarkIntent.HasPendingSpec;

        private void CancelMeasurement()
        {
            BenchmarkRunner.Cancel();
            BenchmarkIntent.Clear();
            
            
            RenderNextSteps(DisplayResult());
        }

        /// <param name="confirmed">
        /// True when the caller already asked. Used by the offer made straight after applying fixes, which would
        /// otherwise put two dialogs in a row in front of the same yes.
        /// </param>
        private void StartMeasurement(string intent, int repetitions, bool confirmed = false)
        {
            // Built from the scene plan, not from the open scene, and by the SAME code the Autopilot uses. Two panels
            // offer this button; if they disagreed about which scene a measurement is about, the result would be
            // baselines that refuse to compare with each other and no screen able to say why.
            var spec = BenchmarkScenePlan.BuildSpec(MeasureWarmupSeconds, MeasureSampleSeconds, repetitions,
                saveRuntimeSession: true, out var problem);
            if (spec == null)
            {
                EditorUtility.DisplayDialog(
                    problem == BenchmarkScenePlan.LaunchProblem.PlanSceneMissing
                        ? L.Tr("A scene in the plan is gone", "计划里的场景已不存在")
                        : L.Tr("Nothing to measure", "无可测量内容"),
                    problem == BenchmarkScenePlan.LaunchProblem.PlanSceneMissing
                        ? L.Tr("One of the scenes set for measuring no longer exists. Pick it again in the Autopilot before measuring.",
                               "设定用于测量的场景之一已不存在。请先在 Autopilot 里重新选择再测量。")
                        : L.Tr("Open and save a scene first — a measurement describes the scene it was taken in, so an unsaved scene has nothing to compare against later.",
                               "请先打开并保存一个场景——测量结果只描述它所在的那个场景，未保存的场景之后无从对比。"),
                    "OK");
                return;
            }

            string what = intent == IntentBaseline
                ? L.Tr("Set a baseline", "建立基线")
                : intent == IntentCompare ? L.Tr("Measure again and compare", "重新测量并对比")
                : L.Tr("Measure this scene", "测量当前场景");

            // The plan changes what the run will do, so it changes what the confirmation has to promise: a run that
            // waits for a scene may need the user to play to it, which is the opposite of "don't touch the editor".
            string targetName = BenchmarkScenePlan.NameOf(spec.targetScenePath);
            string body = spec.WaitsForScene
                ? L.Tr($"PerfLint will enter Play Mode {repetitions} time(s), wait each time until the game has loaded {targetName}, then warm up {MeasureWarmupSeconds:0}s and sample {MeasureSampleSeconds:0}s — {DurationEstimate(repetitions)} of measuring plus however long it takes to reach it.\n\nIf getting there needs you to play, go ahead and play; sampling starts by itself once the scene is loaded. The strip across the top of the Game view says which of the two you are in.\n\nVSync is turned off for the measurement and restored afterwards.",
                       $"PerfLint 会进入 Play Mode {repetitions} 次，每次都等游戏加载出 {targetName} 之后，再预热 {MeasureWarmupSeconds:0} 秒、采样 {MeasureSampleSeconds:0} 秒——测量本身{DurationEstimate(repetitions)}，另加你走到那里花的时间。\n\n{targetName} 要过菜单、选关才能进的话，照常玩就是了，加载出来会自动开始采样。Game 视图顶部的横条会告诉你现在是哪一步。\n\n测量期间会临时关闭 VSync 并在结束后还原。")
                : L.Tr($"PerfLint will enter Play Mode {repetitions} time(s), waiting {MeasureWarmupSeconds:0}s for things to settle and sampling {MeasureSampleSeconds:0}s each time — {DurationEstimate(repetitions)} in total.\n\nRepeating it is what produces the run-to-run spread; without that spread there is no way to tell a real improvement from noise.\n\nVSync is turned off for the measurement and restored afterwards, because a capped frame rate measures your display, not your game.\n\nDon't use the editor while it runs.",
                       $"PerfLint 会进入 Play Mode {repetitions} 次，每次先等 {MeasureWarmupSeconds:0} 秒让画面稳定、再采样 {MeasureSampleSeconds:0} 秒——总计{DurationEstimate(repetitions)}。\n\n重复多轮是为了得到 run-to-run 波动范围；没有它就无法区分真实改善与噪声。\n\n测量期间会临时关闭 VSync 并在结束后还原：帧率被钳制时测的是你的显示器，不是你的游戏。\n\n运行期间请不要操作编辑器。");

            if (!confirmed && !EditorUtility.DisplayDialog(what, body,
                    L.Tr("Measure", "开始测量"), L.Tr("Cancel", "取消")))
                return;

            // Opening the start scene would discard unsaved work, so the question is put before the run rather than
            // from inside the state machine — where a modal dialog would stall the loop raising it.
            if (BenchmarkScenePlan.WouldDiscardUnsavedWork(spec)
                && !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            BenchmarkIntent.Arm(intent);
            

            // A fix almost always leaves the editor importing or compiling, and starting the "after" measurement in
            // the middle of that would measure the editor doing housework rather than the change. Park it instead of
            // refusing: the user pressed the button, and their intent survives a recompile.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                BenchmarkIntent.Park(spec);
                RenderNextSteps(DisplayResult());
                return;
            }

            string refusal = BenchmarkRunner.Begin(spec);
            if (refusal != null)
            {
                BenchmarkIntent.Clear();
                
                EditorUtility.DisplayDialog(L.Tr("Can't measure right now", "现在无法测量"), refusal, "OK");
                return;
            }

            RenderNextSteps(DisplayResult());
        }

        /// <summary>
        /// Watches for a measurement finishing, and releases one that was parked behind a compile. Polling rather
        /// than a callback: the run spans two domain reloads, which destroy this window and any delegate it
        /// registered — the flags live in SessionState for the same reason.
        /// </summary>
        private void PollBenchmark()
        {
            if (!BenchmarkIntent.Awaiting) return;

            if (BenchmarkRunner.IsRunning)
            {
                RenderNextSteps(DisplayResult()); // keep the progress line moving
                return;
            }

            if (HasPendingMeasurement)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return; // still settling

                var spec = BenchmarkIntent.TakeParkedSpec();
                
                if (spec == null)
                {
                    BenchmarkIntent.Clear();
                    
                    return;
                }

                string refusal = BenchmarkRunner.Begin(spec);
                if (refusal != null)
                {
                    BenchmarkIntent.Clear();
                    
                    ShowNotification(new GUIContent(L.Tr("Can't measure: " + refusal, "无法测量：" + refusal)));
                }
                RenderNextSteps(DisplayResult());
                return;
            }

            // Consumed rather than read-then-erased, so that whichever window polls first acts on the completion and
            // the other cannot act on it a second time.
            if (!BenchmarkIntent.TryConsumeFinished(out string intent)) return;

            string error = BenchmarkRunner.LastError;
            bool failed = BenchmarkRunner.CurrentPhase == BenchmarkRunner.Phase.Failed && !string.IsNullOrEmpty(error);
            if (failed)
                ShowNotification(new GUIContent(L.Tr("Measurement stopped: " + error, "测量中止：" + error)));
            else
                ShowNotification(new GUIContent(L.Tr("Measured", "测量完成")));

            if (BenchmarkIntent.ShouldPin(intent)) PinCompletedSessionAsBaseline();

            ReloadRuntimeSession();
            ReloadBenchmarkState();
            if (_lastResult != null) { RenderHeader(ListResult()); RenderResults(); }
        }

        /// <summary>
        /// Promotes the run set that just finished to the pinned baseline.
        ///
        /// Runs even when the session was cancelled or timed out partway: whatever repetitions did complete are
        /// already on disk and are a usable baseline — the refusals inside <see cref="BenchmarkBaseline.Pin"/> are
        /// about measurements that are WRONG (Deep Profile, a VSync cap), not about ones that are merely short.
        /// </summary>
        private void PinCompletedSessionAsBaseline()
        {
            string refusal = BenchmarkIntent.PinFinishedAsBaseline();
            if (refusal != null)
                EditorUtility.DisplayDialog(L.Tr("Baseline not set", "未能建立基线"), refusal, "OK");
        }

        private VisualElement BuildPrimaryStep(NextStep step)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            row.Add(Chip(step.OffCriticalPath ? PerfLintStyle.Dimmer : PerfLintStyle.Accent));

            var body = new VisualElement { style = { flexGrow = 1 } };
            body.Add(new Label(step.Finding.GroupTitleOrTitle)
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 13, marginBottom = 4 }
            });

            void Field(string label, string value)
            {
                var r = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 1 } };
                r.Add(new Label(label) { style = { width = 78, flexShrink = 0, opacity = 0.6f, fontSize = 11 } });
                r.Add(new Label(value) { style = { whiteSpace = WhiteSpace.Normal, flexGrow = 1, fontSize = 11 } });
                body.Add(r);
            }

            Field(L.Tr("Why now", "为什么现在"), step.WhyNow);
            Field(L.Tr("Expected", "预计改善"), step.Expected);
            Field(L.Tr("Risk", "风险"), step.Risk);
            Field(L.Tr("Undo", "可否撤销"), step.Undo);

            var actions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };
            if (step.Finding.Ping != null)
            {
                var locate = new Button(() => step.Finding.Ping()) { text = L.Tr("Locate", "定位") };
                locate.style.marginRight = 4;
                actions.Add(locate);
            }
            var show = new Button(() => FocusRuleInList(step.Finding.RuleId)) { text = L.Tr("Show in list", "在列表中查看") };
            actions.Add(show);
            body.Add(actions);

            row.Add(body);
            return row;
        }

        private VisualElement BuildSecondaryStep(NextStep step)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 2 } };
            row.Add(Chip(step.OffCriticalPath ? PerfLintStyle.Dimmer : PerfLintStyle.Accent, 3, 14));

            var label = new Label(step.Finding.GroupTitleOrTitle)
            {
                style = { whiteSpace = WhiteSpace.Normal, flexGrow = 1, fontSize = 11 }
            };
            // The honest label: still worth doing, just not what is costing you right now.
            if (step.OffCriticalPath)
                label.text += L.Tr("  — not on the critical path right now", "  — 目前不在关键路径上");
            row.Add(label);

            var show = new Button(() => FocusRuleInList(step.Finding.RuleId)) { text = L.Tr("Show", "查看") };
            row.Add(show);
            return row;
        }

        /// <summary>
        /// Shows/hides the in-box hint according to what the box ACTUALLY holds right now.
        ///
        /// Deliberately not driven by the value-changed callback alone — two live paths never fire one:
        ///   · a jump sets _search BEFORE CreateGUI runs, so BuildFilterBar constructs the field already non-empty
        ///     (reported: Autopilot "Locate" → FocusOnRule("ASSET.DUP001"), hint drawn on top of the rule id);
        ///   · re-setting the field to the value it already has is a no-op for UIElements.
        /// So every place that writes the box calls this afterwards.
        /// </summary>
        private void SyncSearchPlaceholder()
        {
            if (_searchPlaceholder == null) return; // filter bar not built yet — BuildFilterBar syncs on creation
            string text = _searchField != null ? (_searchField.value ?? string.Empty) : _search;
            _searchPlaceholder.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Filters the findings list down to one rule, reusing the existing search box so the state is visible and clearable.</summary>
        private void FocusRuleInList(string ruleId)
        {
            _search = ruleId;
            if (_searchField != null) _searchField.value = ruleId;
            SyncSearchPlaceholder();
            RenderResults();
        }

        /// <summary>
        /// Opens the report narrowed to one rule, Info included, with the rule's group expanded — the landing for
        /// another window's "do this in the full panel" button.
        ///
        /// It exists because that button used to call plain OpenWindow(): the reader was sent here to act on ONE
        /// finding and arrived at the top of six hundred, which is not a hand-off, it is a dead end. Three details
        /// are load-bearing: Info must be enabled because the severity filter runs BEFORE the search (an Info-level
        /// rule would filter to an empty list while the search box displays its id); the foldout must be expanded
        /// because Info groups default collapsed; and state is set before touching controls so a freshly created
        /// window — whose CreateGUI has not run yet — builds itself already focused instead of NRE-ing on _results.
        /// </summary>
        /// <param name="query">
        /// Optional filter text to use INSTEAD of the rule id — for landing on one finding rather than all of a
        /// rule's. The rule's group is expanded either way, so a path query lands on the row already open.
        /// </param>
        public void FocusOnRule(string ruleId, string query = null)
        {
            if (string.IsNullOrEmpty(ruleId)) return;

            ShowEverySeverity();
            _foldoutExpanded["R:" + ruleId] = true;
            SaveFoldoutState();
            _search = string.IsNullOrEmpty(query) ? ruleId : query;

            if (_searchField != null) _searchField.value = _search;
            SyncSearchPlaceholder();
            if (_results != null) RenderResults(); // same-value sets fire no callback; a fresh window renders in CreateGUI
        }

        /// <summary>
        /// Un-hides every severity, because a jump that lands on "No matches under the current filter" is not a jump.
        ///
        /// This used to enable Info alone, on the reasoning that Info is the one hidden by default. That misses the
        /// case that actually happens: the severity toggles persist in EditorPrefs, which on Windows is a single hive
        /// shared by every project on the machine — so somebody who unticks "Warning" while reading findings in one
        /// project has silently turned it off everywhere. Observed live: Locate on PERF.TEX002 (a Warning rule) set
        /// the search correctly and landed on "Showing 0 / 543", because Warning had been switched off in a different
        /// project days-of-clicking earlier. The caller asked to be shown this rule; showing it wins over a filter
        /// state it cannot see.
        ///
        /// Setting the toggle values is what persists the change, through their own callbacks — which also re-render,
        /// so callers do not have to.
        /// </summary>
        private void ShowEverySeverity()
        {
            _showCritical = _showWarning = _showInfo = true;
            if (_criticalToggle != null) _criticalToggle.value = true;
            if (_warningToggle != null) _warningToggle.value = true;
            if (_infoToggle != null) _infoToggle.value = true;
            EditorPrefs.SetBool(PrefCritical, true);
            EditorPrefs.SetBool(PrefWarning, true);
            EditorPrefs.SetBool(PrefInfo, true);
        }

        /// <summary>Whether a restored runtime session is present AND describes the scenes currently loaded.</summary>
        private bool RuntimeSessionApplies() =>
            RuntimeSessionStore.Applies(_runtimeSession, RuntimeSessionStore.ScenesInScope());

        /// <summary>
        /// Re-reads the persisted runtime session. Sampling happens in a different window, so the main panel has to
        /// pick the result up on focus rather than being told about it.
        /// Returns true when what the panel should display has changed.
        /// </summary>
        private bool ReloadRuntimeSession()
        {
            var previous = _runtimeSession;
            _runtimeSession = RuntimeSessionStore.Load();

            bool had = previous != null && previous.Findings.Count > 0;
            bool has = _runtimeSession != null && _runtimeSession.Findings.Count > 0;
            if (had != has) return true;
            if (!has) return false;
            return previous.CapturedAtUtc != _runtimeSession.CapturedAtUtc;
        }

        // "Enabling script analysis" busy state. Clicking the one-click enable copies the Roslyn DLLs, adds the
        // PERFLINT_ROSLYN define and triggers a recompile + domain reload — during which the editor stays responsive
        // and the panel would otherwise look unchanged, so users think nothing happened and keep clicking. We lock the
        // panel behind a full-cover overlay and unlock it automatically once the module compiles in (or it times out).
        // The intent must survive the domain reload (which destroys this window), so it lives in SessionState.
        private VisualElement _enablingOverlay;
        private Label _enablingLabel;
        private bool _pollingEnabling;
        private int _enablingTick;
        private const string KRoslynEnabling = "PerfLint.Roslyn.Enabling";
        private const string KRoslynEnablingDeadline = "PerfLint.Roslyn.EnablingDeadline";
        // The loaded-scene set at the last scan (sorted, '|'-joined), so the scope notice can warn "you switched
        // scenes since the last scan — re-scan" for the scene-level rules. SessionState so it survives domain reload
        // alongside the persisted result. Absent (== KScannedScenesUnset) means no scan has run this session.
        private const string KScannedScenes = "PerfLint.Window.ScannedScenes";
        private const string KScannedScenesUnset = "__perflint_unset__";

        // State after a report is restored from disk (surviving domain reload / window reopen): restored findings carry no
        // Fix/Action instances (those aren't serializable). Only these rules previously had one-click fixes; clicking
        // "Refresh this rule" rescans that rule on demand to bring back findings with instances. Once a rule is rescanned or a
        // full scan runs, it's removed from this set. An empty set means the current results are entirely "live".
        private readonly HashSet<string> _restoredFixableRuleIds = new HashSet<string>();

        // Filter state — persisted across window close/reopen (and editor restarts) via EditorPrefs, so a user who turns
        // Info on (or Warning off) doesn't have it silently reset every time they reopen the window. Info stays hidden by
        // default (first run) to cut advisory-level noise. Keys are in EditorPrefs (per-machine, not per-project).
        private const string PrefCritical = "PerfLint.Filter.Critical";
        private const string PrefWarning = "PerfLint.Filter.Warning";
        private const string PrefInfo = "PerfLint.Filter.Info";
        private const string PrefOnlyFixable = "PerfLint.Filter.OnlyFixable";
        // Defaults here; the persisted values are loaded in OnEnable — EditorPrefs.GetBool is NOT allowed from a
        // ScriptableObject field initializer (Unity throws), so it must not run at construction time.
        private bool _showCritical = true;
        private bool _showWarning = true;
        private bool _showInfo = false;
        private bool _onlyFixable = false;
        private string _search = string.Empty;
        private TextField _searchField; // Promoted to a field: lets the "line-by-line analysis" jump set the search term externally
        // The grey hint drawn INSIDE the search box (Unity 2021/2022 TextField has no built-in placeholder, so it is an
        // absolutely-positioned Label). Held as a field because its visibility is not a typing concern: the box gets filled
        // programmatically by every jump, and those paths owe it a sync — see SyncSearchPlaceholder.
        private Label _searchPlaceholder;
        private Toggle _infoToggle;     // Same: the jump needs Info enabled (line-level clues are mostly Info)
        private Toggle _criticalToggle; // Held for the same reason: a jump must be able to un-hide what it jumps to
        private Toggle _warningToggle;
        // Set by FocusOnScript when a "Line-level analysis" jump from a runtime CPU hotspot found NO static issues in that script:
        // the hotspot is CPU-bound compute, not allocation. Lets the empty state explain that instead of a bare "no matches" dead-end. Cleared once the user navigates away.
        private string _focusedScriptNoFindings;
        // Whether that jump came from an ALLOCATION finding rather than a CPU hotspot. The two need opposite empty
        // states and only the caller knows which it is: "no allocation pattern here, so the hotspot is compute-bound"
        // is exactly wrong for RUN.GC001, which arrives having MEASURED an allocation on a specific line.
        // Seen live: GC001 attributed DynamicLightController.cs:98, and the panel it opened replied that the script
        // has no allocation problems and is probably compute-bound.
        private bool _focusedScriptFromAllocation;
        // Set by FocusOnScriptGcRules (runtime RUN.GC001 "Locate" jump): narrows the report to the per-frame allocation rule family
        // (PERF.GC* / PERF.UPD*) so the user lands directly on the actionable allocation findings. Cleared when the user types in the search box.
        private System.Func<Finding, bool> _ruleFocus;
        private string _ruleFocusLabel;

        // Remember each Foldout's expanded/collapsed state (keys: domain "D:..." / rule "R:..."), restored across rebuilds —
        // otherwise RenderResults rebuilding resets every group to its default expand/collapse, reopening what the user manually folded.
        private readonly Dictionary<string, bool> _foldoutExpanded = new Dictionary<string, bool>();

        // Max instance rows rendered per rule, to avoid stuffing tens of thousands of VisualElements at once in a huge project.
        private const int MaxRowsPerRule = 100;

        [MenuItem("Tools/PerfLint/Scan Project %#l")] // Ctrl/Cmd + Shift + L
        public static void Open() => OpenWindow();

        /// <summary>
        /// Opens the panel and starts a full scan — the entry the Autopilot's empty state uses.
        ///
        /// A first-run Autopilot has nothing to rank and used to say so with one button that merely opened this
        /// window, leaving the user to find the Scan button themselves. The sentence was "scan the project first";
        /// the button should therefore scan the project.
        ///
        /// Deferred rather than called straight after Show(): RunScan touches controls that exist only once
        /// CreateGUI has run, and CreateGUI runs when the window is actually displayed, which is not guaranteed to
        /// have happened by the time GetWindow returns. Bounded retries rather than a loop — a window that never
        /// builds is a bug to see in the log, not a delayCall that reschedules itself forever.
        /// </summary>
        public static void OpenAndScan()
        {
            var win = OpenWindow();
            ScanWhenBuilt(win, 30);
        }

        static void ScanWhenBuilt(PerfLintWindow win, int attemptsLeft)
        {
            EditorApplication.delayCall += () =>
            {
                if (win == null) return;
                if (win._scanButton == null || win._results == null)
                {
                    if (attemptsLeft > 0) { ScanWhenBuilt(win, attemptsLeft - 1); return; }
                    Debug.LogWarning("[PerfLint] " + L.Tr(
                        "The panel did not finish building, so the scan was not started. Press Scan Project.",
                        "面板未完成构建，扫描未启动。请手动点击「扫描工程」。"));
                    return;
                }
                win.RunScan();
            };
        }

        /// <summary>Open the main panel and return the window instance (for calling FocusOnScript after a "line-by-line analysis" jump).</summary>
        public static PerfLintWindow OpenWindow()
        {
            var win = GetWindow<PerfLintWindow>();
            win.titleContent = new GUIContent("PerfLint");
            // Min width 640: the title can wrap and buttons can wrap, but at extreme narrowness the two group-header buttons
            // (e.g. AI Fix all + Explain) + the scrollbar still get clipped; rather than keep fighting layout at tiny widths,
            // set a usable lower bound so group-header buttons stay fully visible (takes effect for floating windows).
            win.minSize = new Vector2(640, 380);
            win.Show();
            return win;
        }

        private void OnEnable()
        {
            LicenseService.Changed += RefreshLicenseButton;
            PerfLintScriptFixVerifier.FixRolledBack += OnAiChangeRolledBack;
            // Register as the live-result authority: while open, asset-edit incremental updates come to this window (which
            // holds Fix instances) instead of overwriting the Fix-less on-disk baseline. See PerfLintAutoRescan.
            PerfLintAutoRescan.WindowRefresh = IncrementalRefresh;
            // Expose the live result (with Fix instances) to the Pipeline/MCP optimize commands, so an agent driving an
            // OPEN editor gets instant, correctly-tiered plans instead of the Fix-less on-disk baseline. See PerfLintLiveResult.
            PerfLintLiveResult.Provider = LiveResultForExternal;
            // Load persisted filter state here (NOT in field initializers — EditorPrefs is disallowed from a ScriptableObject
            // constructor). OnEnable runs before CreateGUI/BuildFilterBar, so the toggles render with the restored values.
            _showCritical = EditorPrefs.GetBool(PrefCritical, true);
            _showWarning = EditorPrefs.GetBool(PrefWarning, true);
            _showInfo = EditorPrefs.GetBool(PrefInfo, false);
            _onlyFixable = EditorPrefs.GetBool(PrefOnlyFixable, false);
        }

        private void OnDisable()
        {
            LicenseService.Changed -= RefreshLicenseButton;
            PerfLintScriptFixVerifier.FixRolledBack -= OnAiChangeRolledBack;
            if (PerfLintAutoRescan.WindowRefresh == (System.Func<bool>)IncrementalRefresh) PerfLintAutoRescan.WindowRefresh = null;
            if (PerfLintLiveResult.Provider == (System.Func<ScanResult>)LiveResultForExternal) PerfLintLiveResult.Provider = null;
            StopEnablingPoll(); // don't leak the EditorApplication.update subscription if the window is closed mid-enable
        }

        /// <summary>Live result accessor published to <see cref="PerfLintLiveResult"/> while open — the Pipeline/MCP optimize
        /// commands read this so an agent gets the window's Fix-carrying result (instant, correctly tiered) instead of scanning.
        /// Guard: after a domain reload the report is restored from disk with the Fix/Action instances stripped (tracked in
        /// <see cref="_restoredFixableRuleIds"/>). Handing that out would collapse EVERY finding into OptimizePlan's manual
        /// tier — the optimize commands would falsely report "nothing to optimize" while real waste sits under an "Enable fix".
        /// So we only publish a fully-live result; while any rule is still restored we return null → the command does a fresh
        /// scan (which rebuilds the instances) instead of trusting a Fix-less baseline.</summary>
        private ScanResult LiveResultForExternal() => _restoredFixableRuleIds.Count == 0 ? _lastResult : null;

        /// <summary>
        /// Background auto-pump hand-off (registered in <see cref="OnEnable"/> while the window is open): consume the pending
        /// changed files into the LIVE result (keeping Fix instances), re-render, and persist. This is what makes an asset
        /// edit — which triggers no domain reload — show up in an already-open report without a full rescan.
        /// </summary>
        /// <summary>Returns whether this window took the work. False when it has no live result to refresh — see PerfLintAutoRescan.WindowRefresh.</summary>
        private bool IncrementalRefresh()
        {
            if (_lastResult == null || _results == null) return false;
            Vector2 scroll = _results.scrollOffset;
            var updated = PerfLintIncrementalRescan.Apply(_lastResult, out bool changed);
            if (!changed) return true;   // taken: the queue was drained, there was simply nothing to redraw
            _lastResult = updated;
            ScanResultStore.Save(_lastResult);
            RenderHeader(ListResult());
            RenderResults();
            RestoreScrollAfterLayout(scroll);
            return true;
        }

        /// <summary>
        /// An AI change (fix or whole-file migration) failed compile verification and its file was rolled back.
        /// The rollback happens WITHOUT a domain reload (compilation failed), so this open window must un-show the
        /// "already fixed/migrated" state itself — in a compile-broken project no successful reload will ever come
        /// to reconcile it via RescanFlag (real case: Viking Village AI Migrate rollback left the finding hidden
        /// while the error was back on disk).
        /// </summary>
        private void OnAiChangeRolledBack(string assetPath, string errorSummary)
        {
            ShowNotification(new GUIContent(L.Tr("AI change rolled back (compile failed — see Console)", "AI 修改编译未通过，已回滚（详见 Console）")));
            if (_lastResult == null || string.IsNullOrEmpty(assetPath) || _results == null) return;
            Vector2 scroll = _results.scrollOffset;
            _lastResult = ScanRunner.RescanFile(assetPath, _lastResult);
            ScanResultStore.Save(_lastResult);
            RenderHeader(ListResult());
            RenderResults();
            RestoreScrollAfterLayout(scroll);
        }

        private void RefreshLicenseButton()
        {
            if (_licenseButton == null) return;
            bool pro = Entitlements.IsPro;
            _licenseButton.text = pro ? "Pro ●" : "Free";
            _licenseButton.tooltip = LicenseService.StatusLine();
            // Null rather than a colour when Free: an inline colour would outrank .pl-secondary and take the
            // button's hover with it.
            _licenseButton.style.color = pro ? new StyleColor(PerfLintStyle.Good) : new StyleColor(StyleKeyword.Null);
        }

        private void CreateGUI()
        {
            LoadFoldoutState(); // restore folded/expanded groups across domain reload (e.g. after an AI Fix recompile)

            var root = rootVisualElement;
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;

            // ── Top toolbar ──────────────────────────────
            var toolbar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 }
            };
            // The primary, and the only one on this row. It was an accent blue picked here to match a marketing
            // visual, which is how the product ended up with four different "primary blues" across five windows.
            _scanButton = PerfLintStyle.Primary("Scan Project", RunScan);
            _scanButton.style.flexGrow = 1;
            toolbar.Add(_scanButton);

            _fixAllButton = PerfLintStyle.Toolbar("Fix All", FixAllInResult);
            _fixAllButton.style.marginLeft = 6;
            _fixAllButton.SetEnabled(false);
            toolbar.Add(_fixAllButton);

            var exportButton = PerfLintStyle.Toolbar(L.Tr("Export CSV", "导出 CSV"), ExportCsv);
            exportButton.style.marginLeft = 6;
            toolbar.Add(exportButton);

            var reportButton = PerfLintStyle.Toolbar(L.Tr("Export Report", "导出报告"), ExportHtml);
            reportButton.style.marginLeft = 6;
            reportButton.tooltip = L.Tr("Export a self-contained, shareable HTML health report (offline, nothing uploaded)",
                                        "导出自包含、可分享的 HTML 健康报告（离线、不上传任何内容）");
            toolbar.Add(reportButton);

            var ignoreButton = PerfLintStyle.Toolbar(L.Tr("Ignore", "忽略"), PerfLintScanSettingsWindow.Open);
            ignoreButton.style.marginLeft = 6;
            toolbar.Add(ignoreButton);

            // The simple front door. First in this group because it is where somebody who does not already know which
            // counters matter should start; this panel is the place they come back to for detail.
            var autopilotButton = PerfLintStyle.Toolbar(L.Tr("Autopilot", "向导"), () => PerfLintAutopilotWindow.Open());
            autopilotButton.style.marginLeft = 6;
            autopilotButton.tooltip = L.Tr(
                "One screen: where you are, and the single next thing to do. Detail stays here.",
                "只有一屏：你现在在哪、下一步做什么。细节仍留在本面板。");
            toolbar.Add(autopilotButton);

            var runtimeButton = PerfLintStyle.Toolbar(L.Tr("Runtime", "运行时"), PerfLintRuntimeWindow.Open);
            runtimeButton.style.marginLeft = 6;
            runtimeButton.tooltip = L.Tr(
                "Runtime (Play Mode) performance profiling: locate stutter / per-frame GC / CPU hotspots",
                "运行时（Play Mode）性能分析：定位卡顿 / 每帧 GC / CPU 热点");
            toolbar.Add(runtimeButton);

            var llmButton = PerfLintStyle.Toolbar("LLM", PerfLintLlmSettingsWindow.Open);
            llmButton.style.marginLeft = 6;
            toolbar.Add(llmButton);

            var cliButton = PerfLintStyle.Toolbar("CLI", PerfLintCliHelpWindow.Open);
            cliButton.style.marginLeft = 6;
            cliButton.tooltip = L.Tr(
                "Run PerfLint from the terminal / CI — 'unity command perflint_*' against this editor, or headless batchmode",
                "从终端 / CI 运行 PerfLint —— 用 'unity command perflint_*' 对着本编辑器跑，或无头 batchmode");
            toolbar.Add(cliButton);

            // Users switch language from Tools ▸ PerfLint ▸ Language; this injects the dev-only inline shortcut, and
            // is a no-op in release (see L.InjectDevLangSwitch). CreateGUI appends without clearing, so a flip must
            // wipe root before rebuilding to avoid stacking a second copy of the whole panel.
            L.InjectDevLangSwitch(toolbar, () => { root.Clear(); CreateGUI(); });

            _licenseButton = PerfLintStyle.AsToolbar(new Button(PerfLintLicenseWindow.Open));
            _licenseButton.style.marginLeft = 6;
            toolbar.Add(_licenseButton);
            RefreshLicenseButton();
            // Catches whatever L.InjectDevLangSwitch added above, so the row cannot end in a stock editor button.
            PerfLintStyle.ToolbarButtons(toolbar);
            root.Add(toolbar);

            // ── Health score card ──────────────────────────────
            // Two-row card: row 1 = score / grade / severity pills / ring; row 2 = the savings + optimized lines,
            // full-width so their (long, wrapping) text can never squeeze the pills into a vertical stack under
            // the ring (real layout break once the savings line grew buttons and the scene-scoped clause).
            // The shared card. It used to be a white overlay at 0.03 with a 0.07 rule, which is an addition to
            // whatever is behind it -- correct on the dark editor it was eyeballed against, invisible on a light one.
            var header = PerfLintStyle.Card();
            header.style.marginBottom = 6;
            var headerTop = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
            };

            // The header has no protagonist any more, and that is the point. It summarises the LIST — how many
            // findings, how fresh, what could be saved — and points at where the conclusions live.
            var gradeCol = new VisualElement { style = { marginRight = 18, flexShrink = 0, justifyContent = Justify.Center } };
            _fixableLabel = new Label(L.Tr("Click Scan Project to start", "点击 Scan Project 开始"))
            {
                style = { fontSize = 11, opacity = 0.6f, whiteSpace = WhiteSpace.Normal, marginTop = 3 }
            };
            gradeCol.Add(_fixableLabel);
            headerTop.Add(gradeCol);

            // Rounded severity-count badges; hidden until the first scan fills in real numbers.
            _pillRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, flexGrow = 1, minWidth = 0, display = DisplayStyle.None } };
            _critPill = MakePill(SeverityColor(Severity.Critical));
            _warnPill = MakePill(SeverityColor(Severity.Warning));
            _infoPill = MakePill(SeverityColor(Severity.Info));
            _pillRow.Add(_critPill);
            _pillRow.Add(_warnPill);
            _pillRow.Add(_infoPill);
            headerTop.Add(_pillRow);

            // Where the conclusion lives. Without this the split reads as a feature having been taken away rather
            // than moved, and the person who wanted "so what do I do" has nothing to follow.
            var toAutopilot = new Button(() => PerfLintAutopilotWindow.Open())
            {
                text = L.Tr("What should I do? →", "该做什么？ →"),
                tooltip = L.Tr("Opens the Autopilot: where you are, what to do this round, and whether it worked. This window is the full evidence behind it.",
                               "打开 Autopilot：你现在在哪、这一轮做什么、做完有没有用。本窗口是它背后的完整证据。")
            };
            PerfLintStyle.AsSecondary(toAutopilot);
            toAutopilot.style.flexShrink = 0;
            toAutopilot.style.marginLeft = 8;
            headerTop.Add(toAutopilot);
            header.Add(headerTop);

            // Row 2 (full card width): estimated optimization effect, aggregated from per-finding savings estimates.
            // Green = opportunity, not alarm; "up to ~" wording is load-bearing (every input is a ceiling estimate).
            // The per-dimension optimize buttons only appear when that dimension actually has an executable plan
            // (never a button that can't do anything). Label shrinks/wraps; buttons never shrink.
            _savingsRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginTop = 6, display = DisplayStyle.None }
            };
            _savingsLabel = new Label
            {
                style =
                {
                    fontSize = 11, whiteSpace = WhiteSpace.Normal, flexShrink = 1,
                    color = PerfLintStyle.Good
                }
            };
            // The two optimize buttons moved to the Autopilot's round, where execution lives. The savings LINE stays:
            // it is a summary of the list, which is what this window is for.
            _savingsRow.Add(_savingsLabel);
            header.Add(_savingsRow);
            // Session tally of what one-click optimize has verifiably reclaimed (before-minus-after across rescans).
            _optimizedLabel = new Label
            {
                style =
                {
                    fontSize = 11, whiteSpace = WhiteSpace.Normal, marginTop = 2,
                    color = PerfLintStyle.Good, unityFontStyleAndWeight = FontStyle.Bold,
                    display = DisplayStyle.None
                }
            };
            header.Add(_optimizedLabel);

            root.Add(header);

            // ── "Do this next" lives in the Autopilot now ────────
            //
            // _nextStepsCard is deliberately never built. It was one container holding the entire conclusion
            // apparatus — the target selector, the goal sentence, the measure row, the budget status, the four-field
            // recommendation, the "After that" list and the whole baseline section — which is exactly the set the
            // Autopilot's three screens now own. Leaving it here would mean two windows answering "what should I do"
            // and eventually answering it differently.
            //
            // RenderNextSteps still exists and returns immediately on the null card, so the twenty-odd call sites
            // that refresh it after a scan, a fix or a measurement stay correct without becoming a deletion campaign
            // across the file. The measurement STATE stays too: applying a fix here still offers to re-measure, and
            // that path needs the baseline it no longer draws.

            // ── Deep script analysis (Roslyn) degradation notice + one-click enable ──────────────
            // Without the Roslyn module, script analysis is only text-level (LOG001 etc.); GC / per-frame allocation / heavy CPU loop rules are all silent.
            // Hidden by default; UpdateRoslynNotice() shows/hides it based on detection — otherwise users would wrongly think "the scripts are clean".
            _roslynBox = PerfLintStyle.Note(PerfLintStyle.NoteWarning);
            _roslynBox.style.display = DisplayStyle.None;
            _roslynBox.style.marginBottom = 4;
            _roslynNotice = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    color = PerfLintStyle.Dim,
                    fontSize = 11
                }
            };
            _roslynButton = PerfLintStyle.AsSecondary(new Button(OnRoslynButton) { text = L.Tr("Enable script analysis", "一键启用脚本分析") });
            _roslynButton.style.marginTop = 4;
            _roslynButton.style.alignSelf = Align.FlexStart;
            _roslynBox.Add(_roslynNotice);
            _roslynBox.Add(_roslynButton);
            root.Add(_roslynBox);
            UpdateRoslynNotice();

            // ── Scan-scope notice ────────────────────────────────────────────────────────────────
            // Most rules scan the whole project (AssetDatabase); a few (ISceneScoped: Static Batching,
            // GPU Instancing overlap, Skinned Instancing, Mesh LOD) only see the CURRENTLY loaded scene(s).
            // Without this, a user scanning with an empty/light scene gets a silently partial bill for those
            // rules (exactly how the SBATCH001 miss happened). Persistent Info line; reads the live scene name(s)
            // and enumerates the ISceneScoped scanners so the rule list never drifts. Refreshed on focus (the
            // open scene can change between visits).
            _sceneScopeBox = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            _sceneScopeBox.style.marginBottom = 4;
            _sceneScopeNotice = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    color = PerfLintStyle.Dim,
                    fontSize = 11
                }
            };
            _sceneScopeBox.Add(_sceneScopeNotice);
            root.Add(_sceneScopeBox);
            UpdateSceneScopeNotice();

            // ── Stale plugin build notice ─────────────────────────────────────────────────────────
            // A compile-broken project never domain-reloads, so a PerfLint update (package update, or live-linked
            // source in dev) can sit on disk while this window keeps running the pre-update build — and every
            // "re-test after the fix" silently tests the old code. Shown only in that exact state (source newer
            // than this domain's load + compilation failed); refreshed on focus, when the state can change.
            _stalePluginBox = PerfLintStyle.Note(PerfLintStyle.NoteWarning);
            _stalePluginBox.style.display = DisplayStyle.None;
            _stalePluginBox.style.marginBottom = 4;
            _stalePluginLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Dim } };
            _stalePluginBox.Add(_stalePluginLabel);
            root.Add(_stalePluginBox);
            UpdateStalePluginNotice();

            // ── Restore info banner (shown after a report is restored from disk; non-blocking) ──────────────
            // The report is persisted and survives domain reload / window reopen, so we no longer blank the report or force an 86s full rescan here.
            // It just informs: the report is from the last scan and may be slightly stale; Locate/AI Fix work immediately, one-click fixes use "Refresh" on a rule,
            // or do a full rescan. Hidden by default.
            _staleBanner = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            _staleBanner.style.display = DisplayStyle.None;
            _staleBanner.style.flexDirection = FlexDirection.Row;
            _staleBanner.style.alignItems = Align.Center;
            _staleBanner.style.marginBottom = 4;
            // Wrap the text in a shrinkable container (flexGrow=1 + minWidth=0); do NOT set flexGrow on the text itself — setting
            // flexGrow directly on the Label makes it refuse to shrink under text measurement and pushes the right-side "Rescan all"
            // button out of the window (clipped even when the window is wide). Wrapping in a container is the same robust pattern used for instance rows.
            var staleTextWrap = new VisualElement { style = { flexGrow = 1, minWidth = 0 } };
            _staleLabel = new Label(L.Tr(
                "Report restored from the last scan (may be slightly stale). Locate and AI Fix work; for one-click fixes use 'Refresh' on a rule, or rescan all.",
                "报告由上次扫描恢复（可能略旧）。Locate 与 AI Fix 可用；一键修复点规则上的「刷新」，或全量重扫。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Dim, fontSize = 11 }
            };
            staleTextWrap.Add(_staleLabel);
            _staleBanner.Add(staleTextWrap);
            var refreshButton = PerfLintStyle.AsCompact(new Button(RunScan) { text = L.Tr("Rescan all", "全量重扫") });
            refreshButton.style.marginLeft = 6;
            refreshButton.style.flexShrink = 0;
            _staleBanner.Add(refreshButton);
            root.Add(_staleBanner);

            // ── Filter bar ──────────────────────────────────
            root.Add(BuildFilterBar());

            // The status line and, beside it, the way out of a focused view.
            //
            // A jump from another window focuses this list on one rule, and the focus is a PREDICATE, not text — so
            // the search box stays empty and there is nothing to clear. The line said "(type in search to clear)",
            // which asks the reader to guess a gesture, on a control that looks untouched. Tim found it exactly that
            // way: two findings shown out of 273, no visible reason and no visible exit.
            var statusRow = new VisualElement
            { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 } };
            _filterStatus = new Label("") { style = { opacity = 0.6f, fontSize = 10 } };
            statusRow.Add(_filterStatus);

            _clearFocusButton = new Button(ClearRuleFocus)
            {
                text = L.Tr("Show all", "显示全部"),
                tooltip = L.Tr("Clears the focus and shows every finding again.", "取消聚焦，重新显示全部结论。"),
                style = { fontSize = 10, marginLeft = 6, paddingTop = 0, paddingBottom = 0,
                          paddingLeft = 6, paddingRight = 6, display = DisplayStyle.None }
            };
            PerfLintStyle.AsCompact(_clearFocusButton);
            // Re-applied after the class, because .pl-compact sets its own padding and this one is deliberately
            // tighter still -- it sits on a status LINE, not in a row of actions.
            _clearFocusButton.style.fontSize = 10;
            _clearFocusButton.style.paddingTop = 0;
            _clearFocusButton.style.paddingBottom = 0;
            _clearFocusButton.style.paddingLeft = 6;
            _clearFocusButton.style.paddingRight = 6;
            statusRow.Add(_clearFocusButton);
            root.Add(statusRow);

            root.Add(MakeDivider());

            // ── Results list ────────────────────────────────
            _results = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            // Reserve width on the content's right for the vertical scrollbar: otherwise the scrollbar floats over the content and clips part of the rightmost buttons (Explain / Fix).
            _results.contentContainer.style.paddingRight = 14;
            root.Add(_results);

            // ── Privacy footer (trust selling point, always visible) ──────────────
            root.Add(new Label(L.Tr(
                "Scans run locally and are never uploaded · AI Fix sends only the snippet you choose — via PerfLint's zero-log AI service, or direct to your own provider (Advanced)",
                "扫描本地完成、永不上传 · AI 修复仅发送你选择的那段代码——经 PerfLint 零日志 AI 服务转发，或直连你自己的服务商（高级）"))
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    unityFontStyleAndWeight = FontStyle.Italic,
                    opacity = 0.6f, marginTop = 6, fontSize = 10
                }
            });

            // After a domain reload / window reopen the in-memory results are empty → restore the last scan from disk, avoiding a blank report and an 86s forced full rescan.
            RestoreLastResultIfAny();

            // If a one-click "Enable script analysis" is still in flight (this CreateGUI is very likely the rebuild after
            // the recompile it triggered), re-show the busy overlay and resume polling — or finish it if it's already done.
            ResumeRoslynEnablingIfPending();

            // Same idea for a measurement in flight: this CreateGUI may well be the rebuild after the domain reload
            // that entering Play Mode caused, so the poll has to be re-armed here rather than assumed still alive.
            root.schedule.Execute(PollBenchmark).Every(500);
        }

        /// <summary>
        /// Restore the last scan results after the window is built:
        ///   1. Results already in memory (GUI rebuilt within the same session) → redraw directly, no disk read.
        ///   2. Otherwise restore the baseline from disk; restored findings carry no Fix/Action instances (not serializable), recorded in _restoredFixableRuleIds,
        ///      revived on demand by the "Refresh" button on a rule group. Locate / AI Fix don't depend on these instances and work right after restore.
        ///   3. If there are files just modified by AI Fix (staged by the verifier across reloads) → incrementally rescan those files so their findings become live with accurate line numbers —
        ///      this is exactly what replaces "force a full rescan after AI Fix": touch only the few changed files, sub-second.
        /// </summary>
        private void RestoreLastResultIfAny()
        {
            // Sampling lives in the runtime panel and persists itself; pick it up before anything renders.
            ReloadRuntimeSession();
            ReloadBenchmarkState();

            if (_lastResult != null) { RenderHeader(ListResult()); RenderResults(); return; }

            var restored = ScanResultStore.Load();
            if (restored == null) return; // Never scanned / file corrupted → stay in the not-scanned state

            _lastResult = restored.Result;
            _restoredFixableRuleIds.Clear();
            if (restored.FixableRuleIds != null)
                foreach (var id in restored.FixableRuleIds) _restoredFixableRuleIds.Add(id);

            // Changed files (modified by AI Fix + scripts/assets the user manually edited/deleted/moved): incrementally rescan
            // to make their findings live (replacing a full rescan). Both sources are registered in PerfLintPendingRescan,
            // written by the change tracker / verifier before domain reload, and consumed here via the shared apply step.
            _lastResult = PerfLintIncrementalRescan.Apply(_lastResult, out bool refreshedAny);
            if (refreshedAny) ScanResultStore.Save(_lastResult);

            SessionState.EraseBool(PerfLintScriptFixVerifier.RescanFlag);

            // Too many changes to incrementally rescan one by one (branch switch / large batch reimport) → wholesale stale, prompt the user for a full rescan.
            bool stale = PerfLintPendingRescan.ConsumeStale();

            // Info banner: prompt when there are still "previously fixable but not rescanned" rules, or when wholesale stale (otherwise the report is live enough — don't disturb).
            if (_staleBanner != null)
            {
                bool show = _restoredFixableRuleIds.Count > 0 || stale;
                _staleBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                if (_staleLabel != null)
                    _staleLabel.text = stale
                        ? L.Tr("The project changed a lot; the report may be stale — a full rescan is recommended.",
                               "项目有较多改动，报告可能已过期，建议全量重扫。")
                        : L.Tr("Report restored from the last scan (may be slightly stale). Locate and AI Fix work; for one-click fixes use 'Refresh' on a rule, or rescan all.",
                               "报告由上次扫描恢复（可能略旧）。Locate 与 AI Fix 可用；一键修复点规则上的「刷新」，或全量重扫。");
            }

            RenderHeader(ListResult());
            RenderResults();
        }

        private VisualElement BuildFilterBar()
        {
            var bar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginBottom = 2 }
            };

            _criticalToggle = MakeToggle("Critical", _showCritical, v => { _showCritical = v; EditorPrefs.SetBool(PrefCritical, v); RenderResults(); });
            bar.Add(_criticalToggle);
            _warningToggle = MakeToggle("Warning", _showWarning, v => { _showWarning = v; EditorPrefs.SetBool(PrefWarning, v); RenderResults(); });
            bar.Add(_warningToggle);
            _infoToggle = MakeToggle("Info", _showInfo, v => { _showInfo = v; EditorPrefs.SetBool(PrefInfo, v); RenderResults(); });
            bar.Add(_infoToggle);
            bar.Add(MakeToggle(L.Tr("Fixable only", "只看可修复"), _onlyFixable, v => { _onlyFixable = v; EditorPrefs.SetBool(PrefOnlyFixable, v); RenderResults(); }));

            var search = new TextField { value = _search };
            _searchField = search;
            search.style.flexGrow = 1;
            search.style.minWidth = 120;
            search.style.marginLeft = 6;
            // Placeholder hint
            var placeholder = new Label(L.Tr("Filter rule / title / path…", "筛选规则/标题/路径…"))
            {
                pickingMode = PickingMode.Ignore,
                style = { position = Position.Absolute, left = 4, top = 2, opacity = 0.4f, fontSize = 11 }
            };
            _searchPlaceholder = placeholder;
            search.Add(placeholder);
            // The box can already carry a query the moment it is built (a jump sets _search before CreateGUI runs),
            // and that path fires no value-changed callback at all — so the hint has to be resolved here, not only there.
            SyncSearchPlaceholder();
            search.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? string.Empty;
                // Navigating away from the focused script drops the "compute-bound hotspot" empty-state explanation (it's specific to that jump).
                if (_search != _focusedScriptNoFindings) _focusedScriptNoFindings = null;
                // Typing a real query means the user is filtering on their own → drop the rule-family focus (PERF.GC*/UPD*) from the RUN.GC001 jump.
                if (!string.IsNullOrEmpty(_search)) { _ruleFocus = null; _ruleFocusLabel = null; }
                SyncSearchPlaceholder();
                RenderResults();
            });
            bar.Add(search);

            return bar;
        }

        private static Toggle MakeToggle(string label, bool initial, Action<bool> onChange)
        {
            var t = new Toggle(label) { value = initial };
            t.style.marginRight = 12;
            t.style.flexShrink = 0;

            // By default a BaseField's label has a min-width and stretches, pushing the checkbox to the right.
            // Tighten the label so the checkbox sits right next to the text.
            // Use labelElement (populated in the constructor): on Unity 2021.3 t.Q<Label>() is null right after construction,
            // so this styling was silently skipped there; labelElement is available immediately across versions.
            var lbl = t.labelElement ?? t.Q<Label>();
            if (lbl != null)
            {
                lbl.style.minWidth = 0;
                lbl.style.flexGrow = 0;
                lbl.style.marginRight = 4;
                lbl.style.paddingRight = 0;
            }

            t.RegisterValueChangedCallback(evt => onChange(evt.newValue));
            return t;
        }

        private void RunScan()
        {
            // A full scan is a fresh start: drop any transient view focus (e.g. the RUN.GC001 → PERF.GC*/UPD* narrowing),
            // otherwise the report stays stuck filtered to one rule family and the user can't get back to the whole list.
            _ruleFocus = null;
            _ruleFocusLabel = null;
            _focusedScriptNoFindings = null;
            // The "optimized for you" tally is bound to one report generation: a full scan starts a new one, and a
            // stale brag line hovering over a fresh report reads as a claim about it (user-reported confusion).
            _optimizedMemBytes = 0;
            _optimizedBuildBytes = 0;
            if (_staleBanner != null) _staleBanner.style.display = DisplayStyle.None; // Once refreshed, clear the stale prompt
            _scanButton.SetEnabled(false);
            _fixAllButton.SetEnabled(false);
            _results.Clear();
            ScanResult result = null;

            try
            {
                var context = new ScanContext(
                    cancellationToken: CancellationToken.None,
                    reportProgress: (name, p) =>
                        EditorUtility.DisplayProgressBar("PerfLint", $"Scanning: {name}", p));

                result = ScanRunner.Run(context);
            }
            catch (OperationCanceledException)
            {
                // User canceled, return silently.
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _scanButton.SetEnabled(true);
            }

            if (result != null)
            {
                _lastResult = result;
                _restoredFixableRuleIds.Clear(); // A full scan produces live findings, so the restored state is all invalidated
                ScanResultStore.Save(_lastResult);
                // Record which scene(s) this scan actually saw, so the scope notice can later tell the user when they've
                // switched scenes and the scene-level findings (SBATCH001 etc.) have gone stale.
                SessionState.SetString(KScannedScenes, SceneKey(CurrentLoadedSceneNames()));
                UpdateSceneScopeNotice();
                ReloadRuntimeSession(); // a fresh scan re-renders everything — fold in any runtime measurement too
                RenderHeader(ListResult());
                RenderResults();
            }
        }

        /// <summary>
        /// Show/hide the "you're running a pre-update PerfLint build" notice. Only fires when the package source
        /// on disk is newer than the running build AND the project's compile errors block the reload that would
        /// normally pick it up — the state where every re-test silently tests old code. Refreshed on focus.
        /// </summary>
        private void UpdateStalePluginNotice()
        {
            if (_stalePluginBox == null) return;
            bool stale = Core.PerfLintStaleBuildGuard.IsDomainStale();
            _stalePluginBox.style.display = stale ? DisplayStyle.Flex : DisplayStyle.None;
            if (stale)
                _stalePluginLabel.text = L.Tr(
                    "⚠ PerfLint or this project's packages changed on disk, but compile errors prevent Unity from reloading scripts — " +
                    "this session is still running the pre-change code (loaded packages may not match what the compiler now checks against). " +
                    "Fix the compile errors, or restart the editor to load the current state.",
                    "⚠ PerfLint 或本工程的包已在磁盘上变更，但编译错误阻止了 Unity 重载脚本——当前会话仍在运行变更前的代码" +
                    "（内存中的包可能与编译器实际校验的版本不一致）。请先修复编译错误，或重启编辑器加载最新状态。");
        }

        private void OnFocus()
        {
            UpdateStalePluginNotice();
            UpdateSceneScopeNotice(); // the open scene can change while the window is unfocused

            // A sampling session may have completed in the runtime panel while this window was in the background.
            // Re-render on a genuine change only — an unconditional rebuild here would throw away the user's
            // scroll position and expanded groups every time they click back into the window.
            //
            // Both halves are asked, and neither may short-circuit the other: opening a different scene changes what
            // the baseline section may offer without touching the sampling session at all, so testing only the
            // session would leave a "measure again and compare" button pointing at a scene it cannot compare.
            bool runtimeChanged = ReloadRuntimeSession();
            bool benchmarkChanged = ReloadBenchmarkState();
            if ((runtimeChanged || benchmarkChanged) && _lastResult != null)
            {
                RenderHeader(ListResult());
                RenderResults();
            }
        }

        /// <summary>
        /// Refresh the persistent "scan scope" notice: scan is project-wide, but the ISceneScoped rules only reflect
        /// the currently loaded scene(s). Reads the live scene name(s) and enumerates the discovered ISceneScoped
        /// scanners so the rule list stays in sync as rules are added/removed. Static string builder — no allocation-heavy path.
        /// </summary>
        internal static string BuildSceneScopeText(IReadOnlyList<string> loadedSceneNames, IReadOnlyList<string> sceneScopedRuleNames)
        {
            string rules = sceneScopedRuleNames != null && sceneScopedRuleNames.Count > 0
                ? string.Join(", ", sceneScopedRuleNames)
                : L.Tr("Static Batching, GPU Instancing, Mesh LOD", "Static Batching、GPU Instancing、Mesh LOD");

            bool anyScene = loadedSceneNames != null && loadedSceneNames.Count > 0;
            string scenes = anyScene ? string.Join(", ", loadedSceneNames) : null;

            string head = L.Tr("Scan covers the whole project. A few scene-level checks (",
                               "扫描覆盖整个工程。少数场景级检查（") + rules + L.Tr(") only reflect ", "）只反映");
            string tail = anyScene
                ? L.Tr($"the open scene(s): {scenes}. Open your heaviest scene and re-scan for a complete picture.",
                       $"当前打开的场景：{scenes}。打开最重的场景重扫可得完整结果。")
                : L.Tr("the currently loaded scene(s) — no scene is open, so these checks find nothing. Open your heaviest scene and re-scan.",
                       "当前加载的场景——现在没有打开任何场景，故这几项检查什么都扫不到。请打开最重的场景重扫。");
            return head + tail;
        }

        /// <summary>
        /// The "you switched scenes since the last scan — re-scan" warning. Pure so the stale-detection wording is
        /// unit-testable. Names both the scanned and now-open scene(s) plus the affected scene-level rules.
        /// </summary>
        internal static string BuildSceneChangedText(
            IReadOnlyList<string> scannedScenes, IReadOnlyList<string> currentScenes, IReadOnlyList<string> ruleNames)
        {
            string rules = ruleNames != null && ruleNames.Count > 0
                ? string.Join(", ", ruleNames)
                : L.Tr("Static Batching, GPU Instancing, Mesh LOD", "Static Batching、GPU Instancing、Mesh LOD");
            string scanned = scannedScenes != null && scannedScenes.Count > 0 ? string.Join(", ", scannedScenes) : L.Tr("(none)", "(无)");
            string now = currentScenes != null && currentScenes.Count > 0 ? string.Join(", ", currentScenes) : L.Tr("(none)", "(无)");
            return L.Tr(
                $"⚠ Scene changed since the last scan (scanned: {scanned} · now open: {now}). The scene-level checks ({rules}) still reflect the old scene — re-scan to update them.",
                $"⚠ 上次扫描后场景已切换（扫描时：{scanned} · 当前：{now}）。场景级检查（{rules}）仍是旧场景的结果——请重扫更新。");
        }

        /// <summary>Currently loaded scene name(s), for display.</summary>
        private static List<string> CurrentLoadedSceneNames()
        {
            var loaded = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                loaded.Add(string.IsNullOrEmpty(sc.name) ? L.Tr("(untitled)", "(未命名)") : sc.name);
            }
            return loaded;
        }

        /// <summary>Order-insensitive comparison/storage key for a loaded-scene set.</summary>
        private static string SceneKey(IReadOnlyList<string> names)
            => string.Join("|", names.OrderBy(n => n, StringComparer.Ordinal));

        private void UpdateSceneScopeNotice()
        {
            if (_sceneScopeNotice == null) return;

            var loaded = CurrentLoadedSceneNames();
            var ruleNames = ScanRunner.DiscoverScanners()
                .OfType<ISceneScoped>()
                .Cast<IScanner>()
                .Select(s => s.Name)
                .ToList();

            string scannedKey = SessionState.GetString(KScannedScenes, KScannedScenesUnset);
            bool hasScan = scannedKey != KScannedScenesUnset;

            if (hasScan && scannedKey != SceneKey(loaded))
            {
                // Scene-level findings on screen are for a scene that's no longer open — flag it amber and prompt a re-scan.
                var scannedNames = string.IsNullOrEmpty(scannedKey)
                    ? new List<string>()
                    : scannedKey.Split('|').ToList();
                _sceneScopeNotice.text = BuildSceneChangedText(scannedNames, loaded, ruleNames);
                // Ink, not amber: the block it sits in has just turned amber, and a paragraph in the block's
                // own hue on the block's own fill is the same fact said three times at low contrast. Emphasis
                // here is brightness -- this state is the one you must read -- and the category is the block.
                _sceneScopeNotice.style.color = PerfLintStyle.Ink;
                // Swap the class, not the inline fill: the block carries a rule as well as a wash, and setting only
                // the background would leave an amber block ruled in blue.
                _sceneScopeBox.RemoveFromClassList(PerfLintStyle.NoteAccent);
                _sceneScopeBox.AddToClassList(PerfLintStyle.NoteWarning);
            }
            else
            {
                _sceneScopeNotice.text = BuildSceneScopeText(loaded, ruleNames);
                _sceneScopeNotice.style.color = PerfLintStyle.Dim;
                _sceneScopeBox.RemoveFromClassList(PerfLintStyle.NoteWarning);
                _sceneScopeBox.AddToClassList(PerfLintStyle.NoteAccent);
            }
        }

        /// <summary>Show/hide the top "script analysis degraded" notice + one-click enable button based on whether the Roslyn module is compiled in. Refreshed by CreateGUI and after every scan.</summary>
        private void UpdateRoslynNotice()
        {
            if (_roslynBox == null) return;
            bool deep = ScanRunner.IsDeepScriptAnalysisAvailable();
            _roslynBox.style.display = deep ? DisplayStyle.None : DisplayStyle.Flex;
            if (deep) return;

            bool canOneClick = RoslynSetup.CanOneClickInstall;
            _roslynNotice.text =
                L.Tr("⚠ Deep script analysis is not enabled: only text-level checks (e.g. Debug.Log) run for now; ",
                     "⚠ 脚本深度分析未启用：当前仅做文本级检测（如 Debug.Log）；") +
                L.Tr("script GC / per-frame allocation / heavy CPU loop rules (GC001–004, UPD001–003, CPU001) are not running. ",
                     "脚本 GC / 每帧分配 / CPU 重循环（GC001–004、UPD001–003、CPU001）规则未运行。") +
                (canOneClick
                    ? L.Tr("Click 'Enable' below (auto-adds the Microsoft.CodeAnalysis DLLs + the PERFLINT_ROSLYN define and recompiles).",
                           "点下方一键启用（自动放入 Microsoft.CodeAnalysis DLL + 加 PERFLINT_ROSLYN 宏并重编译）。")
                    : L.Tr("This package has no bundled Roslyn DLLs; follow SETUP-ROSLYN.md to install via NuGetForUnity, then enable.",
                           "本包未内置 Roslyn DLL，请按 SETUP-ROSLYN.md 用 NuGetForUnity 安装后再启用。"));
            _roslynButton.text = canOneClick ? L.Tr("Enable script analysis", "一键启用脚本分析") : L.Tr("View setup steps", "查看启用步骤");
        }

        private void OnRoslynButton()
        {
            if (!RoslynSetup.CanOneClickInstall)
            {
                // No bundled DLLs: open the manual-steps doc.
                var doc = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                    "Packages/com.perflint.unity/Editor/Scripting/SETUP-ROSLYN.md");
                if (doc != null) AssetDatabase.OpenAsset(doc);
                else EditorUtility.DisplayDialog(L.Tr("Enable script analysis", "启用脚本分析"),
                    L.Tr("Follow Editor/Scripting/SETUP-ROSLYN.md in the package: install Microsoft.CodeAnalysis.CSharp via NuGetForUnity and add the Scripting Define `PERFLINT_ROSLYN`.",
                         "请按包内 Editor/Scripting/SETUP-ROSLYN.md：用 NuGetForUnity 安装 " +
                         "Microsoft.CodeAnalysis.CSharp，并加 Scripting Define `PERFLINT_ROSLYN`。"), "OK");
                return;
            }

            // The button itself is the "one-click enable" intent; no second confirmation needed (the action is reversible: removing the define turns it off). Execute directly.
            var (ok, msg, conflicts) = RoslynSetup.Install();
            if (ok)
            {
                // Install kicked off a recompile + domain reload. Instead of a modal dialog the user clicks past
                // (after which the panel looks idle but is mid-compile), lock the panel behind a busy overlay that
                // clears itself once the module compiles in. Log the detail (e.g. skipped dependencies) for the record.
                Debug.Log("[PerfLint] " + msg);
                BeginRoslynEnabling();
            }
            else if (conflicts != null && conflicts.Length > 0)
            {
                // Version conflict: offer a "Locate conflicting DLLs" button that, when clicked, selects and highlights these old-version dependencies in the Project window.
                bool locate = EditorUtility.DisplayDialog(L.Tr("Enable failed", "启用失败"), msg, L.Tr("Locate conflicting DLLs", "定位冲突 DLL"), L.Tr("Close", "关闭"));
                if (locate) RoslynSetup.LocateInProject(conflicts);
            }
            else
            {
                EditorUtility.DisplayDialog(L.Tr("Enable failed", "启用失败"), msg, "OK");
            }
        }

        // ── "Enabling script analysis" busy state ────────────────────────────────────────────────
        // Lifecycle: BeginRoslynEnabling (on click) → recompile + domain reload destroys the window →
        // CreateGUI re-entry re-shows the overlay and resumes polling → PollEnabling detects the module
        // compiled in (or a timeout) → FinishRoslynEnabling unlocks the panel.

        private const double EnablingTimeoutSeconds = 120.0; // generous: copy + recompile + domain reload

        private void BeginRoslynEnabling()
        {
            SessionState.SetBool(KRoslynEnabling, true);
            SessionState.SetFloat(KRoslynEnablingDeadline, (float)(EditorApplication.timeSinceStartup + EnablingTimeoutSeconds));
            ShowEnablingOverlay();
            StartEnablingPoll();
        }

        /// <summary>Pure decision for "give up waiting": only fail once compilation has settled AND the deadline has passed AND the module still isn't available. Kept static + internal so the premature-failure regression can be unit-tested without a domain reload.</summary>
        internal static bool RoslynEnablingTimedOut(bool deepAvailable, bool compiling, double now, double deadline) =>
            !deepAvailable && !compiling && deadline > 0.0 && now > deadline;

        private void PollEnabling()
        {
            // Success: the gated PerfLint.Scripting assembly compiled in. The availability probe is cached and reset on
            // domain reload, so in practice this turns true right after CreateGUI re-runs in the post-reload domain.
            if (ScanRunner.IsDeepScriptAnalysisAvailable())
            {
                FinishRoslynEnabling(success: true);
                return;
            }

            double deadline = SessionState.GetFloat(KRoslynEnablingDeadline, 0f);
            if (RoslynEnablingTimedOut(false, EditorApplication.isCompiling, EditorApplication.timeSinceStartup, deadline))
            {
                FinishRoslynEnabling(success: false);
                return;
            }

            // Animate the trailing dots (~ every 0.5s of editor ticks) so the overlay reads as alive, not frozen.
            if (_enablingLabel != null && (++_enablingTick % 30) == 0)
            {
                int dots = (_enablingTick / 30) % 4;
                _enablingLabel.text = L.Tr("Enabling script analysis", "正在启用脚本分析") + new string('.', dots);
            }
        }

        private void FinishRoslynEnabling(bool success)
        {
            SessionState.EraseBool(KRoslynEnabling);
            SessionState.EraseFloat(KRoslynEnablingDeadline);
            StopEnablingPoll();
            HideEnablingOverlay();
            UpdateRoslynNotice(); // success → the notice box hides itself; failure → it stays with the Enable button for a retry
            if (!success)
                EditorUtility.DisplayDialog(
                    L.Tr("Enable script analysis", "启用脚本分析"),
                    L.Tr("Enabling took longer than expected, or compilation failed. Check the Console for errors, then try again or follow the manual setup steps.",
                         "启用耗时超出预期，或编译失败。请查看 Console 报错后重试，或按手动步骤启用。"),
                    "OK");
        }

        private void StartEnablingPoll()
        {
            if (_pollingEnabling) return;
            _pollingEnabling = true;
            EditorApplication.update += PollEnabling;
        }

        private void StopEnablingPoll()
        {
            if (!_pollingEnabling) return;
            _pollingEnabling = false;
            EditorApplication.update -= PollEnabling;
        }

        private void ShowEnablingOverlay()
        {
            if (_enablingOverlay == null)
            {
                _enablingOverlay = new VisualElement
                {
                    // Absolute full-cover element: with default pickingMode it intercepts every pointer event, so the
                    // panel beneath is effectively locked, and the translucent fill dims it to read as "busy".
                    style =
                    {
                        position = Position.Absolute,
                        top = 0, left = 0, right = 0, bottom = 0,
                        backgroundColor = new Color(0f, 0f, 0f, 0.55f),
                        alignItems = Align.Center,
                        justifyContent = Justify.Center,
                    }
                };
                var card = new VisualElement
                {
                    style =
                    {
                        maxWidth = 420,
                        paddingTop = 16, paddingBottom = 16, paddingLeft = 20, paddingRight = 20,
                        backgroundColor = PerfLintStyle.SurfaceRaised,
                        borderTopLeftRadius = 6, borderTopRightRadius = 6,
                        borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                        alignItems = Align.Center,
                    }
                };
                _enablingLabel = new Label(L.Tr("Enabling script analysis", "正在启用脚本分析"))
                {
                    style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 13, color = PerfLintStyle.Ink }
                };
                var sub = new Label(L.Tr(
                    "Copying the Roslyn analyzers and recompiling — the editor may reload. The panel unlocks automatically when it's done.",
                    "正在拷入 Roslyn 分析器并重新编译——编辑器可能会重载。完成后面板会自动解锁。"))
                {
                    style = { whiteSpace = WhiteSpace.Normal, marginTop = 8, fontSize = 11, color = PerfLintStyle.Dim, unityTextAlign = TextAnchor.MiddleCenter }
                };
                card.Add(_enablingLabel);
                card.Add(sub);
                _enablingOverlay.Add(card);
            }
            if (_enablingOverlay.parent == null) rootVisualElement.Add(_enablingOverlay);
            _enablingOverlay.style.display = DisplayStyle.Flex;
            _enablingOverlay.BringToFront();
        }

        private void HideEnablingOverlay()
        {
            if (_enablingOverlay != null) _enablingOverlay.style.display = DisplayStyle.None;
        }

        /// <summary>After CreateGUI rebuilds the window (notably across the domain reload the enable triggers), resume the busy state if an enable is still in flight — or finish it immediately if the module already compiled in during the reload.</summary>
        private void ResumeRoslynEnablingIfPending()
        {
            if (!SessionState.GetBool(KRoslynEnabling, false)) return;
            if (ScanRunner.IsDeepScriptAnalysisAvailable()) { FinishRoslynEnabling(success: true); return; }
            ShowEnablingOverlay();
            StartEnablingPoll();
        }

        private void RenderHeader(ScanResult result)
        {
            UpdateRoslynNotice();

            // No headline. This window is the reference view; "where am I" is the Autopilot's first screen.
            //
            // Both candidates were rejected on purpose. The frame-rate one moved with the rest of the conclusion.
            // The health score is not taking its place: it saturates, and a project with 400 findings reads 0/F and
            // stays there whatever you fix — measured, and the reason the headline stopped being the score in the
            // first place. A number that reads F on every real project is worse than no number.

            _fixableLabel.text =
                $"{result.AutoFixableCount} {L.Tr("one-click-fixable", "项可一键修复")} · {result.Duration.TotalSeconds:0.0}s";

            // Estimated optimization effect line — shown only when at least one rule produced an honest estimate.
            // Biggest number first so the line never leads with its weakest figure.
            // Savings are computed from the STATIC findings only. RUN.* findings carry no savings estimate, so they
            // would contribute nothing today — but saying so explicitly keeps a future runtime rule that DOES carry
            // an estimate from silently entering a figure this window presents as asset-scoped.
            var staticFindings = _lastResult != null ? _lastResult.Findings : result.Findings;
            var savings = SavingsSummary.Compute(staticFindings);
            if (savings.HasAny)
            {
                var parts = new List<string>(2);
                void AddBuild() { if (savings.BuildBytes > 0) parts.Add(L.Tr($"~{ScannerUtil.Human(savings.BuildBytes)} build size", $"包体约 {ScannerUtil.Human(savings.BuildBytes)}")); }
                void AddMem() { if (savings.MemoryBytes > 0) parts.Add(L.Tr($"~{ScannerUtil.Human(savings.MemoryBytes)} memory", $"内存约 {ScannerUtil.Human(savings.MemoryBytes)}")); }
                if (savings.BuildBytes >= savings.MemoryBytes) { AddBuild(); AddMem(); } else { AddMem(); AddBuild(); }
                // Project-wide by construction (scanners walk all of Assets/) — say so, or a single-scene device
                // snapshot can never match the figure (real museum lesson, 2026-07-17). The scene-scoped clause
                // gives the number a same-scene Memory Profiler A/B CAN validate: firm estimates whose assets are
                // in the open scenes' dependency set.
                string text = L.Tr($"Potential savings found: up to {string.Join(" · ", parts)} (est., project-wide)",
                                   $"发现可优化空间：最多 {string.Join("、", parts)}（估算，全项目资产口径）");
                if (savings.MemoryBytes > 0)
                {
                    long sceneMem = SavingsSummary.ComputeSceneScopedMemory(staticFindings, GetOpenSceneDependencies());
                    if (sceneMem > 0)
                    {
                        text += L.Tr($" — ~{ScannerUtil.Human(sceneMem)} of firm memory savings in the open scene(s)",
                                     $"，其中当前场景相关内存约 {ScannerUtil.Human(sceneMem)}");
                        // No "(~X one-click, the rest manual)" split here. That clause existed to set expectations
                        // for an optimize button that used to sit next to this line; the round that offers those
                        // buttons moved to the Autopilot (see OpenOptimizeDialog), so the split was left describing
                        // a control this window no longer has — and it cost an OptimizePlan.Build over every static
                        // finding on each header render to say it. The expectation it was setting is now set where
                        // it belongs: the optimize dialog itemizes what will run and for how much, immediately
                        // before running it.
                    }
                }
                _savingsLabel.text = text;
                _savingsRow.style.display = DisplayStyle.Flex;
            }
            else
            {
                _savingsRow.style.display = DisplayStyle.None;
            }

            // Session tally of verified one-click reclaims.
            if (_optimizedMemBytes > 0 || _optimizedBuildBytes > 0)
            {
                var done = new List<string>(2);
                if (_optimizedMemBytes > 0) done.Add(L.Tr($"~{ScannerUtil.Human(_optimizedMemBytes)} memory", $"内存约 {ScannerUtil.Human(_optimizedMemBytes)}"));
                if (_optimizedBuildBytes > 0) done.Add(L.Tr($"~{ScannerUtil.Human(_optimizedBuildBytes)} build size", $"包体约 {ScannerUtil.Human(_optimizedBuildBytes)}"));
                _optimizedLabel.text = L.Tr($"Optimized for you: {string.Join(" · ", done)} (est.)",
                                            $"已为您优化：{string.Join("、", done)}（估算）");
                _optimizedLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _optimizedLabel.style.display = DisplayStyle.None;
            }

            // A zero count is good news (e.g. "0 Critical"), so dim it to grey instead of flashing its severity color as if it were an alarm.
            StylePill(_critPill, result.CriticalCount, "Critical", SeverityColor(Severity.Critical));
            StylePill(_warnPill, result.WarningCount, "Warning", SeverityColor(Severity.Warning));
            StylePill(_infoPill, result.InfoCount, "Info", SeverityColor(Severity.Info));
            _pillRow.style.display = DisplayStyle.Flex;

            _fixAllButton.text = result.AutoFixableCount > 0 ? $"Fix All ({result.AutoFixableCount})" : "Fix All";
            _fixAllButton.SetEnabled(result.AutoFixableCount > 0);

            // Ranked from the same merged result the header just described, so the card and the numbers above it can
            // never disagree about what was measured.
            RenderNextSteps(result);
        }

        /// <summary>Redraw the results list based only on filter state, without triggering a rescan — toggling filters is instant.</summary>
        /// <summary>
        /// Restore the scroll position after rebuilding the list. The list is multi-level Foldouts + TextFields, whose layout takes several passes to settle;
        /// in early layout passes the content height isn't ready yet, so setting scrollOffset gets clamped small. So re-set it on every layout change of the content container,
        /// until it sticks to the target (content is tall enough) or the attempt count is exhausted, then unregister the callback and hand control back to the user.
        /// </summary>
        private void RestoreScrollAfterLayout(Vector2 scroll)
        {
            int attempts = 0;
            void OnGeo(GeometryChangedEvent _)
            {
                _results.scrollOffset = scroll;
                attempts++;
                // Stuck to the target (content tall enough) or 12 attempts exhausted (including cases where content really got shorter and the target is unreachable) → unregister the callback.
                if (attempts >= 12 || Mathf.Abs(_results.scrollOffset.y - scroll.y) < 1f)
                    _results.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnGeo);
            }
            _results.contentContainer.RegisterCallback<GeometryChangedEvent>(OnGeo);
            _results.scrollOffset = scroll; // If content is already tall enough, takes effect immediately (fallback for when the geometry event doesn't fire because the height didn't change)
        }

        /// <summary>Get the remembered Foldout expanded state; use the default value if not recorded.</summary>
        private bool GetFoldout(string key, bool defaultValue) =>
            _foldoutExpanded.TryGetValue(key, out var v) ? v : defaultValue;

        // Foldout expand/collapse state is an instance field, so it would be wiped on the domain reload an AI Fix
        // triggers (recompile after applying) — reopening groups the user had folded. Persist it to SessionState
        // (survives domain reload, cleared on Unity restart) so the view stays exactly as the user left it across a fix.
        private const string KFoldoutState = "PerfLint.Window.FoldoutState";

        private void SaveFoldoutState()
        {
            if (_foldoutExpanded.Count == 0) { SessionState.EraseString(KFoldoutState); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _foldoutExpanded)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append('=').Append(kv.Value ? '1' : '0'); // keys are "D:<domain>"/"R:<ruleId>" — no '=' or '\n'
            }
            SessionState.SetString(KFoldoutState, sb.ToString());
        }

        private void LoadFoldoutState()
        {
            _foldoutExpanded.Clear();
            var s = SessionState.GetString(KFoldoutState, "");
            if (string.IsNullOrEmpty(s)) return;
            foreach (var line in s.Split('\n'))
            {
                int eq = line.LastIndexOf('=');
                if (eq > 0) _foldoutExpanded[line.Substring(0, eq)] = line[eq + 1] == '1';
            }
        }

        private void RenderResults()
        {
            _results.Clear();
            if (_lastResult == null) return;

            // The STATIC scan, on its own. Runtime findings had been merged in here so they would share the list,
            // the filters and the domain grouping — which is precisely what made this window two things at once.
            // They live in the Runtime Profiler now; the exported report still carries both.
            var display = ListResult();

            if (display.Findings.Count == 0)
            {
                _filterStatus.text = "";
                _results.Add(new Label(L.Tr("No issues found.", "未发现问题。")) { style = { marginTop = 8 } });
                return;
            }

            var filtered = display.Findings.Where(PassesFilter).ToList();
            bool focused = _ruleFocus != null && !string.IsNullOrEmpty(_ruleFocusLabel);
            _filterStatus.text = $"{L.Tr("Showing", "显示")} {filtered.Count} / {display.Findings.Count}" +
                                 (_showInfo ? "" : L.Tr(" · Info hidden", " · Info 已隐藏")) +
                                 (focused ? L.Tr($" · focused: {_ruleFocusLabel}", $" · 已聚焦：{_ruleFocusLabel}") : "");
            if (_clearFocusButton != null)
                _clearFocusButton.style.display = focused || !string.IsNullOrEmpty(_search)
                    ? DisplayStyle.Flex : DisplayStyle.None;

            if (filtered.Count == 0)
            {
                if (!string.IsNullOrEmpty(_focusedScriptNoFindings) && _search == _focusedScriptNoFindings)
                    _results.Add(_focusedScriptFromAllocation
                        ? BuildNoAllocationFindingsHelp()   // runtime MEASURED an allocation the static patterns don't match
                        : BuildComputeBoundHotspotHelp());  // a CPU hotspot with no allocation in it is compute-bound
                else if (_ruleFocus != null)
                    _results.Add(BuildNoAllocationFindingsHelp());
                // Searching for a RUN.* rule here can never match: this panel holds the STATIC scan, and runtime
                // conclusions live in the Runtime Profiler and the Autopilot. "No matches" is true and useless —
                // it reads as "that rule found nothing" when the truth is "that rule does not live here".
                else if (LooksLikeRuntimeRuleId(_search))
                    _results.Add(BuildRuntimeRuleElsewhereHelp(_search));
                else
                    _results.Add(new Label(L.Tr("No matches under the current filter.", "当前筛选下没有匹配项。")) { style = { marginTop = 8, opacity = 0.7f } });
                return;
            }

            // Two-level grouping: domain → rule.
            foreach (var domainGroup in filtered.GroupBy(f => f.Domain).OrderBy(g => g.Key))
            {
                string dkey = "D:" + domainGroup.Key;
                var domainFoldout = new Foldout
                {
                    text = $"{domainGroup.Key}  ({domainGroup.Count()})",
                    value = GetFoldout(dkey, true)
                };
                domainFoldout.Q<Toggle>()?.AddToClassList("perflint-domain");
                // Give the domain header more presence than the default grey foldout text: bigger, bold, near-white —
                // so the three section cards clearly read as the top level above the rule rows.
                var domainTitle = domainFoldout.Q<Toggle>()?.Q<Label>();
                if (domainTitle != null)
                {
                    domainTitle.style.fontSize = 13;
                    domainTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
                    domainTitle.style.color = PerfLintStyle.Ink;
                }
                domainFoldout.RegisterValueChangedCallback(_ => { _foldoutExpanded[dkey] = domainFoldout.value; SaveFoldoutState(); });

                var ruleGroups = domainGroup
                    .GroupBy(f => f.RuleId)
                    .OrderByDescending(g => g.Max(f => f.Severity))
                    .ThenByDescending(g => g.Count());

                foreach (var ruleGroup in ruleGroups)
                    domainFoldout.Add(BuildRuleFoldout(ruleGroup));

                // Wrap each domain in a rounded block so the report reads as grouped panels rather than a flat tree.
                // The recessed panel rather than a card: a domain is a container for the rows inside it, and a nested
                // block that goes LIGHTER than what it sits on reads as raised -- as something you could click.
                var card = PerfLintStyle.Panel();
                card.style.marginTop = 8;
                card.style.marginBottom = 2;
                card.style.paddingTop = 4;
                card.style.paddingBottom = 6;
                card.style.paddingLeft = 8;
                card.style.paddingRight = 6;
                card.Add(domainFoldout);
                _results.Add(card);
            }
        }

        private VisualElement BuildRuleFoldout(IGrouping<string, Finding> ruleGroup)
        {
            var items = ruleGroup.ToList();
            var sev = items.Max(f => f.Severity);
            // The group header uses the rule-level title (without the per-instance count); falls back to the first item's Title if unset.
            string repTitle = items[0].GroupTitleOrTitle;
            int fixableCount = items.Count(f => f.CanAutoFix);

            // Info rules collapsed by default to cut noise; Critical/Warning expanded by default. The remembered state takes priority over the default.
            string rkey = "R:" + ruleGroup.Key;
            var foldout = new Foldout { value = GetFoldout(rkey, sev != Severity.Info) };
            foldout.style.marginLeft = 8;
            foldout.style.marginTop = 2;
            foldout.RegisterValueChangedCallback(_ => { _foldoutExpanded[rkey] = foldout.value; SaveFoldoutState(); });

            // Custom title row (with severity color dot, count, per-rule batch fix button).
            var titleToggle = foldout.Q<Toggle>();
            // minWidth=0 is key: flex children default to min-width:auto (= content width); without 0 the title refuses to shrink/wrap
            // and, once it fills the whole row, pushes the right-side buttons out of the window (especially in narrow windows). Setting 0 lets the title shrink with the window and wrap.
            // flexWrap: when the window is too narrow and the title has shrunk fully but still can't fit the right-side buttons, let the buttons wrap to the next line as a whole instead of clipping out of the window.
            // In wide windows they still lay out in one line by flex-basis (title flexGrow expands, buttons hug the right).
            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1, minWidth = 0, flexWrap = Wrap.Wrap } };
            titleRow.Add(new Label("●") { style = { color = SeverityColor(sev), marginRight = 6, minWidth = 12, flexShrink = 0 } });
            // Wrap the title in a shrinkable container (flexGrow on the container, not directly on the Label): setting flexGrow directly on the Label makes it refuse to shrink under text measurement
            // and push the right-side buttons out of the window; this is the same robust pattern used for instance rows / banners. minWidth/whiteSpace stay on the Label.
            var titleWrap = new VisualElement { style = { flexGrow = 1, minWidth = 0 } };
            titleWrap.Add(new Label($"{ruleGroup.Key} · {repTitle}  ({items.Count})")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, minWidth = 0, whiteSpace = WhiteSpace.Normal }
            });
            titleRow.Add(titleWrap);
            if (fixableCount > 0)
            {
                var fixRule = new Button(() => ApplyFixes(items.Where(f => f.CanAutoFix).ToList(), ruleGroup.Key))
                {
                    text = $"Fix ({fixableCount})"
                };
                fixRule.style.marginLeft = 4;
                fixRule.style.flexShrink = 0; // When the title wraps, the button keeps its width and isn't squashed/clipped
                // Stop the click from bubbling to the Foldout header, avoiding accidentally toggling expand/collapse.
                fixRule.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                titleRow.Add(fixRule);
            }
            else if (_restoredFixableRuleIds.Contains(ruleGroup.Key))
            {
                // Report restored from disk: this rule previously had a one-click fix, but the Fix instance is non-serializable and was lost. Clicking this rescans only this rule (incremental, not full),
                // bringing back findings with instances, and the "Fix" button then appears. Locate/AI Fix are unaffected and already work.
                string rid = ruleGroup.Key;
                var enableFix = new Button(() => RescanRules(new[] { rid }))
                {
                    text = L.Tr("Enable fix", "启用修复"),
                    tooltip = L.Tr("This rule's results were restored from the last scan; one-click fix needs a rule rescan to enable (rescans only this rule, not everything).",
                                   "此规则结果由上次扫描恢复，一键修复需重扫该规则才能启用（仅重扫这一条规则，不全量）")
                };
                enableFix.style.marginLeft = 4;
                enableFix.style.flexShrink = 0;
                enableFix.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                titleRow.Add(enableFix);
            }

            // AI batch fix: for all of this rule's findings that "point at code with no deterministic fix", generate and apply one by one, saving clicking each individually.
            int aiCount = items.Count(f => f.AiFixable);
            if (aiCount > 0)
            {
                if (LlmSettings.IsConfigured)
                {
                    string rid = ruleGroup.Key;
                    var aiAll = new Button(() => AiFixAllForRule(rid)) { text = $"{L.Tr("AI Fix all", "AI Fix 全部")} ({aiCount})" };
                    aiAll.style.marginLeft = 4;
                    aiAll.style.flexShrink = 0;
                    aiAll.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                    titleRow.Add(aiAll);
                }
                else
                {
                    // This rule has AI-fixable findings, but the LLM isn't configured — show a "go configure" prompt rather than silently hiding the button and leaving the user wondering why there's no AI Fix.
                    var cfg = new Button(() => PerfLintLlmSettingsWindow.Open())
                    {
                        text = $"{L.Tr("Set up AI Fix", "配置 AI Fix")} ({aiCount})",
                        tooltip = L.Tr("This rule supports AI one-click fix, but you must configure an LLM provider and key first. Click to open LLM settings.",
                                       "这条规则可用 AI 一键修复，但需先配置 LLM 服务商与密钥。点此打开 LLM 设置。")
                    };
                    cfg.style.marginLeft = 4;
                    cfg.style.flexShrink = 0;
                    cfg.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                    titleRow.Add(cfg);
                }
            }

            // Explain is at the rule level (one per rule, no longer repeated per row): use the first item as the representative; the explanation applies to the whole rule.
            if (LlmSettings.IsConfigured)
            {
                VisualElement panel = null;
                var explain = new Button { text = "Explain" };
                explain.style.marginLeft = 4;
                explain.style.flexShrink = 0;
                explain.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                explain.clicked += () =>
                {
                    if (!Entitlements.RequireAiCredit(L.Tr("LLM Explain", "LLM 解释"))) return;
                    foldout.value = true;
                    if (panel == null)
                    {
                        panel = BuildExplainPanel(items[0]);
                        foldout.Insert(0, panel); // Place above the instance rows
                    }
                    else
                    {
                        panel.style.display = panel.style.display == DisplayStyle.None
                            ? DisplayStyle.Flex : DisplayStyle.None;
                    }
                };
                titleRow.Add(explain);
            }

            // Addressables duplicate rules (AADUP001 / AARES001) are report-only and driven by the official Analyze
            // dependency simulation — nothing here mutates the project, so a stale list only refreshes on a rescan.
            // The common case is a MANUAL refactor the tool can't do for you: following AARES001's guidance you move a
            // TMP font out of a Resources folder, and until you rescan, AARES001 still lists it and AADUP001 hasn't
            // picked it up as an extractable duplicate. A full Scan is the ~100s-class cost; this button re-runs ONLY
            // the two Addressables duplicate scanners (RescanRules → seconds), so the AARES001 row disappears and the
            // asset reappears under AADUP001 without a full project scan. Scanning is free (no Pro/credit gate).
            if (ruleGroup.Key == "ASSET.AARES001" || ruleGroup.Key == "ASSET.AADUP001")
            {
                var reanalyze = new Button(() => RescanRules(new[] { "ASSET.AADUP001", "ASSET.AARES001" }))
                {
                    text = L.Tr("Re-analyze", "重新分析"),
                    tooltip = L.Tr("Re-run the Addressables duplicate analysis for these two rules only — not a full project scan (seconds, not ~100s). Use it after manually moving assets out of a Resources folder so the AARES001 / AADUP001 lists update.",
                                   "仅重跑这两条 Addressables 重复规则，不做全项目扫描（几秒，非上百秒）。手动把资源移出 Resources 目录后点它，AARES001 / AADUP001 列表随即更新。")
                };
                reanalyze.style.marginLeft = 4;
                reanalyze.style.flexShrink = 0;
                reanalyze.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                titleRow.Add(reanalyze);
            }

            // Put the title row into the Foldout's built-in Toggle "input cell" (the cell with the toggle arrow). It must be added into unity-toggle__input,
            // otherwise titleRow becomes another flex-grow child of the Toggle, each taking half the width with the arrow cell → the title is pushed to the window's centerline
            // and the right-side buttons are also clipped out of the window. Added into the input cell, it follows the arrow, fills the whole row, and the title left-aligns normally and wraps with the window.
            // One tier of button for the whole row, decided once. Up to six are added above under six separate
            // conditions, and styling them one at a time is how a row ends up with two looks.
            PerfLintStyle.CompactActions(titleRow);

            titleToggle?.Q<Label>()?.RemoveFromHierarchy();
            var toggleInput = titleToggle?.Q(className: "unity-toggle__input");
            (toggleInput ?? (VisualElement)titleToggle)?.Add(titleRow);

            // Rule-level "action-type" batch (e.g. "Extract to shared group, all") goes at the top of the expanded area on its own prominent line —
            // in the header line the title's flexGrow fills the space and the button would be pushed out of view on the window's right. Distinct from Fix All: config-changing actions don't go into Fix All.
            // Note: the batch targets ALL of this rule's findings (including those not shown, limited by MaxRowsPerRule), not just the visible rows.
            // Only actions that opt into rule-level batching get a "run all" button. Excludes actions whose per-row
            // targets differ (PKG001 disables a DIFFERENT module per finding — a shared "Disable X all" label would be
            // wrong) or that can't run in a loop (each disable triggers a package re-resolve + domain reload).
            // A single actionable finding gets no batch line either: "run all (1)" is the per-row button with a
            // redundant label — and project-level singleton rules (the PROJ family) would show it on every scan.
            var actionItems = items.Where(f => f.HasAction && f.Action.AllowRuleBatch).ToList();
            if (actionItems.Count > 1)
            {
                string label = actionItems[0].Action.Label;
                var bar = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 18, marginTop = 3, marginBottom = 3 }
                };
                // Actions that pick a target (duplicate merge) batch through a single shared project scan instead of
                // re-scanning per group (500 groups would otherwise be 500 full scans); others run one-by-one.
                bool batchChoice = actionItems[0].Action.SupportsTargetChoice;
                var actAll = new Button(() => { if (batchChoice) RunMergeAllForDuplicates(actionItems); else RunActionsForRule(actionItems); })
                // No emoji in the label: U+26A1 defaults to emoji presentation, which the 2021/2022 editor font
                // cannot render (it came out as an empty box) even though Unity 6 shows it fine.
                { text = $"{label} {L.Tr("all", "全部")} ({actionItems.Count})" };
                bar.Add(PerfLintStyle.AsCompact(actAll));
                foldout.Add(bar);
            }

            // Instance rows (limited).
            int shown = Math.Min(items.Count, MaxRowsPerRule);
            for (int i = 0; i < shown; i++)
                foldout.Add(MakeFindingRow(items[i]));
            if (items.Count > shown)
            {
                string hint = fixableCount > 0
                    ? L.Tr("use the Fix button above to batch-process", "用上方 Fix 批量处理")
                    : L.Tr("narrow with search, or use Export CSV to see all", "用搜索缩小范围，或「导出 CSV」查看全部");
                foldout.Add(new Label($"… {items.Count - shown} {L.Tr("more", "条")} ({hint})")
                {
                    style = { opacity = 0.55f, marginLeft = 18, unityFontStyleAndWeight = FontStyle.Italic }
                });
            }

            return foldout;
        }

        private VisualElement MakeFindingRow(Finding finding)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginLeft = 18,
                    paddingTop = 2, paddingBottom = 2,
                    borderBottomWidth = 1,
                    borderBottomColor = PerfLintStyle.Line
                }
            };

            var text = new VisualElement { style = { flexGrow = 1 } };
            text.Add(new Label(finding.Title) { style = { whiteSpace = WhiteSpace.Normal } });
            if (!string.IsNullOrEmpty(finding.TargetPath))
                text.Add(new Label(finding.TargetPath) { style = { opacity = 0.5f, fontSize = 10, whiteSpace = WhiteSpace.Normal } });
            row.Add(text);

            // Outer column wrapper, created on demand: hosts the expandable Detail and/or the AI-fix panel below the row.
            VisualElement col = null;
            VisualElement Col()
            {
                if (col == null) { col = new VisualElement(); col.Add(row); }
                return col;
            }

            // Expandable Detail. The main panel previously never rendered Finding.Detail AT ALL (it only reached
            // CSV/HTML export and Explain context) — fine for rules whose title carries the substance, but findings
            // like AAGRAN001 keep all their content (counts, guidance) in Detail and looked empty. Click the row text
            // or the caret to toggle. Project-level findings (no target path — usually a single row whose substance
            // IS the detail) start EXPANDED: a collapsed caret proved too easy to miss in smoke testing.
            bool hasTargets = finding.LocateTargets != null && finding.LocateTargets.Count > 0;
            if (!string.IsNullOrEmpty(finding.Detail) || hasTargets)
            {
                bool startOpen = string.IsNullOrEmpty(finding.TargetPath);
                var caret = new Label(startOpen ? "▾" : "▸") { style = { marginRight = 3, opacity = 0.55f, flexShrink = 0 } };
                row.Insert(0, caret);

                // Detail text and per-target Locate rows share one caret and one indent rail: they are two halves of
                // the same explanation — the text says what was found, the rows take you to each thing it names.
                var block = new VisualElement
                {
                    style =
                    {
                        display = startOpen ? DisplayStyle.Flex : DisplayStyle.None,
                        marginLeft = 36, marginTop = 2, marginBottom = 4, paddingLeft = 8,
                        borderLeftWidth = 2, borderLeftColor = PerfLintStyle.Hair
                    }
                };
                if (!string.IsNullOrEmpty(finding.Detail))
                    block.Add(new Label(finding.Detail)
                    {
                        style = { whiteSpace = WhiteSpace.Normal, opacity = 0.8f, fontSize = 11 }
                    });

                // Per-target Locate rows. The runtime panel has rendered these since RUN.GPU002 shipped; the main
                // panel never did, so a finding that named several assets could only ever reveal one of them —
                // "Select group" selects them all but the Project window can only show one folder at a time, so
                // visually it lands on a single asset and looks broken. One row per target, one button each.
                if (hasTargets)
                {
                    foreach (var t in finding.LocateTargets)
                    {
                        var targetRow = new VisualElement
                        {
                            style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 3 }
                        };
                        // Label and its optional second line share the left column so the button stays aligned.
                        var textCol = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
                        textCol.Add(new Label(t.Label)
                        {
                            style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, opacity = 0.8f }
                        });
                        // Indented by layout, not by spaces: UI Toolkit collapses leading whitespace in a wrapping
                        // Label, so a hierarchy written with spaces renders dead flat.
                        if (!string.IsNullOrEmpty(t.Detail))
                            textCol.Add(new Label(t.Detail)
                            {
                                style =
                                {
                                    whiteSpace = WhiteSpace.Normal, fontSize = 10, opacity = 0.55f,
                                    marginLeft = 12, marginTop = 1
                                }
                            });
                        targetRow.Add(textCol);

                        var captured = t;
                        var locateOne = PerfLintStyle.AsCompact(new Button(() => captured.Ping?.Invoke()) { text = "Locate" });
                        locateOne.style.marginLeft = 4;
                        locateOne.style.flexShrink = 0;
                        targetRow.Add(locateOne);
                        block.Add(targetRow);
                    }
                }

                Col().Add(block);
                void ToggleDetail()
                {
                    bool show = block.style.display == DisplayStyle.None;
                    block.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                    caret.text = show ? "▾" : "▸";
                }
                caret.RegisterCallback<ClickEvent>(_ => ToggleDetail());
                text.RegisterCallback<ClickEvent>(_ => ToggleDetail());
            }

            // When multiple assets are involved (e.g. a duplicate group), offer "select all" — a single Locate isn't enough.
            if (finding.HasGroup)
            {
                var sel = new Button(() => SelectGroup(finding.Group)) { text = $"{L.Tr("Select group", "选中组")} ({finding.Group.Count})" };
                sel.style.marginLeft = 4;
                row.Add(sel);
            }
            else if (finding.Ping != null)
            {
                var locate = new Button(() => finding.Ping()) { text = "Locate" };
                locate.style.marginLeft = 4;
                row.Add(locate);
            }

            if (finding.CanAutoFix)
            {
                // The button is always visible (Free sees it → clicking turns into an upgrade nudge); only Pro actually executes.
                var fix = new Button(() => ApplyFix(finding)) { text = "Fix" };
                fix.style.marginLeft = 4;
                row.Add(fix);
            }

            // Action-type actions (e.g. "Extract to shared group"): config-changing, not undoable, not in Fix All; separate button + separate confirmation.
            if (finding.HasAction)
            {
                var act = new Button(() => RunAction(finding)) { text = finding.Action.Label };
                act.style.marginLeft = 4;
                row.Add(act);
            }

            // The place a finding's own text sends you, when that place is a PerfLint screen and not a file. Only the
            // streaming rule qualifies today, and it is the one where acting without going there can change nothing:
            // its detail says the realistic saving is 0 B while the scene's streamable pool sits under the Memory
            // Budget. The button beside it turns the flags on; this one is where you make them matter.
            if (finding.RuleId != null && finding.RuleId.StartsWith("PERF.TEXSTR", StringComparison.Ordinal))
            {
                var tune = new Button(TextureStreamingSection.Reveal)
                {
                    text = L.Tr("Tune the budget", "去调预算"),
                    tooltip = L.Tr("Opens the Runtime Profiler's Texture Streaming section, expanded — Memory Budget and Max Level Reduction live there.",
                                   "打开运行时分析器的 Texture Streaming 区并展开——Memory Budget 与 Max Level Reduction 都在那里。")
                };
                tune.style.marginLeft = 4;
                row.Add(tune);
            }

            // Script-level AI fix: only for findings that "point at code with no deterministic fix" (one by one, each at a different location).
            if (finding.AiFixable && LlmSettings.IsConfigured)
            {
                VisualElement panel = null;
                var aifix = new Button { text = "AI Fix" };
                aifix.style.marginLeft = 4;
                aifix.clicked += () =>
                {
                    if (panel == null) { panel = BuildAiFixPanel(finding); panel.style.marginLeft = 18; Col().Add(panel); }
                    else panel.style.display = panel.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None;
                };
                row.Add(aifix);
            }

            // Whole-file AI migration: structural findings a fragment-level AI Fix can't handle (recipe-bound, Pro).
            // These findings deliberately carry no CodeFile (that would light up AI Fix); the target comes from the
            // recipe's resolver (default: the .cs in TargetPath; shader recipes: the file the compiler error points at).
            var migrateRecipe = MigrateRecipes.ForRule(finding.RuleId);
            var migrateTarget = migrateRecipe != null ? MigrateRecipes.Resolve(migrateRecipe, finding.TargetPath) : null;
            if (migrateTarget != null && LlmSettings.IsConfigured)
            {
                // The cap is knowable right here, off the file on disk — so decide here. Offering the button and
                // only then saying "too large" hands over an action that could never have run: the panel it opens
                // has no Generate button in it at all. Say why where the button would have been instead.
                if (MigrateService.ExceedsLineCap(migrateRecipe, migrateTarget.FilePath, out int migrateLines))
                {
                    row.Add(new Label(L.Tr($"AI Migrate: file too large ({migrateLines} > {migrateRecipe.MaxLines} lines)",
                                           $"AI 迁移：文件过大（{migrateLines} > {migrateRecipe.MaxLines} 行）"))
                    {
                        tooltip = L.Tr(
                            $"A whole-file migration has to come back from the model in one completion, so it is capped at {migrateRecipe.MaxLines} lines — {migrateTarget.FilePath} has {migrateLines}. Migrate it by hand; Explain, on this rule's header row, has the playbook.",
                            $"整文件迁移要求模型在一次回复里返回整个文件，因此上限为 {migrateRecipe.MaxLines} 行——而 {migrateTarget.FilePath} 有 {migrateLines} 行。请手动迁移；迁移路径见本规则标题行上的 Explain。"),
                        style = { marginLeft = 6, alignSelf = Align.Center, fontSize = 11, opacity = 0.6f, flexShrink = 0 }
                    });
                }
                else
                {
                    VisualElement mpanel = null;
                    var migrate = new Button { text = "AI Migrate" };
                    migrate.style.marginLeft = 4;
                    migrate.clicked += () =>
                    {
                        if (mpanel == null) { mpanel = BuildAiMigratePanel(migrateRecipe, migrateTarget); mpanel.style.marginLeft = 18; Col().Add(mpanel); }
                        else mpanel.style.display = mpanel.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None;
                    };
                    row.Add(migrate);
                }
            }

            PerfLintStyle.CompactActions(row);
            return col ?? (VisualElement)row;
        }

        /// <summary>Script-level AI fix panel: clearly state how much code will be sent → generate → review diff → apply (undo relies on version control).</summary>
        private VisualElement BuildAiFixPanel(Finding finding)
        {
            string provider = LlmSettings.ProviderDisplayName;
            int n = ScriptFixService.WindowLineCount(finding);
            // Deterministic rename rules never call the LLM — the privacy line must say so, not claim a send.
            bool deterministic = ScriptFixService.IsDeterministic(finding.RuleId);

            // Amber, and the whole block rather than a 2 px edge: this panel is about to send code somewhere.
            var box = PerfLintStyle.Note(PerfLintStyle.NoteWarning);
            box.style.marginTop = 4;

            var status = new Label(deterministic
                ? L.Tr("This fix is a deterministic rename — computed locally, nothing is sent anywhere.",
                       "此修复为确定性改名——本地计算，不向任何地方发送任何内容。")
                : L.Tr(
                $"AI Fix will send ~{n} lines around the flagged code to {provider} (only this snippet, not the whole file/project).",
                $"AI 修复会把被标记代码附近约 {n} 行发送给 {provider}（仅这一段，不发整文件/项目）。"))
            {
                style = { whiteSpace = WhiteSpace.Normal }
            };
            box.Add(status);

            var diffArea = new VisualElement();

            var gen = PerfLintStyle.AsSecondary(new Button { text = deterministic
                ? L.Tr("Generate fix (deterministic, nothing sent)", "生成修复（确定性改名，不发送）")
                : L.Tr($"Generate fix (send ~{n} lines to {provider})", $"生成修复（发送约 {n} 行给 {provider}）") });
            gen.style.marginTop = 4;
            gen.clicked += () =>
            {
                if (!deterministic && !Entitlements.RequireAiCredit(L.Tr("AI Fix", "AI 修复"))) return;
                gen.SetEnabled(false);
                status.text = L.Tr("Generating…", "生成中…");
                diffArea.Clear();
                ScriptFixService.Propose(finding, p =>
                {
                    gen.SetEnabled(true);
                    if (!p.Ok) { status.text = L.Tr("Failed: ", "失败：") + p.Error; return; }
                    if (p.NoChange)
                    {
                        status.text = L.Tr("AI judged no change is needed here — the original code is fine; this may be a false positive and can be ignored.",
                                           "AI 判断此处无需修改——原始写法已正确，可能是规则误报，可忽略。");
                        return;
                    }
                    status.text = p.Locatable
                        ? L.Tr("Fix generated. Review the diff, then apply:", "已生成修复，请审阅 diff 后应用：")
                        : L.Tr("Fix generated, but the original snippet couldn't be located precisely in the file. Apply manually:", "已生成修复，但无法在文件中精确定位原始片段，请手动应用：");
                    RenderAiFixDiff(diffArea, p);
                });
            };
            box.Add(gen);
            box.Add(diffArea);
            return box;
        }

        /// <summary>
        /// Whole-file AI migration panel (AI Migrate). Two things distinguish it from the AI Fix panel and both are
        /// deliberate: ① the privacy disclosure says the ENTIRE file is sent (AI Fix promises snippet-only — this is
        /// the explicit, per-click exception the user consents to); ② the gate is RequirePro (Migration Assistant is
        /// a Pro entitlement) on top of the usual AI credit.
        /// </summary>
        private VisualElement BuildAiMigratePanel(MigrateRecipe recipe, MigrateTarget target)
        {
            string provider = LlmSettings.ProviderDisplayName;
            string filePath = target.FilePath;
            bool overCap = MigrateService.ExceedsLineCap(recipe, filePath, out int n);

            var box = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            box.style.marginTop = 4;

            box.Add(new Label(recipe.Summary()) { style = { whiteSpace = WhiteSpace.Normal } });

            // Routing hint: this file matches a deeper per-API recipe elsewhere in the list — steer the user there.
            if (!string.IsNullOrEmpty(target.UserNotice))
            {
                box.Add(new Label("⚠ " + target.UserNotice)
                { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2, color = PerfLintStyle.Amber } });
            }

            // Shader recipes may target an INCLUDED file rather than the finding's asset — say which file, explicitly.
            if (!string.IsNullOrEmpty(target.VerifyAssetPath) &&
                !string.Equals(target.VerifyAssetPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                box.Add(new Label(L.Tr(
                    $"Target file: {filePath} (where the compiler error lives — not the .shader itself).",
                    $"目标文件：{filePath}（编译错误所在的文件——不是 .shader 主文件本身）。"))
                { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2, color = PerfLintStyle.Dim } });
            }

            var status = new Label(L.Tr(
                $"AI Migrate sends the ENTIRE file ({n} lines) to {provider} — unlike AI Fix, which sends only a snippet. " +
                "The file is rewritten as a whole; a compile failure auto-rolls back. Commit to version control first.",
                $"AI 迁移会把整个文件（{n} 行）发送给 {provider}——与 AI 修复只发片段不同。" +
                "文件将被整体重写；编译失败会自动回滚。建议先提交版本控制。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, marginTop = 4 }
            };
            box.Add(status);

            // Credits transparency: on the hosted proxy, each request costs 1 credit — INCLUDING every automatic
            // compile-error retry (up to 2 per Apply). A whole-file migration can therefore spend more than one.
            // BYO-key users pay their own tokens and are never counted, so this note only applies to hosted mode.
            if (LlmSettings.Mode == LlmMode.Hosted)
            {
                box.Add(new Label(L.Tr(
                    "Credits: 1 per AI request — and each automatic retry after a failed compile counts too (up to 2 per Apply), so a migration may use a few.",
                    "Credits：每次 AI 请求计 1 个——编译失败后的每次自动重试同样计入（每次应用最多 2 轮），因此一次迁移可能消耗数个。"))
                { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2, color = PerfLintStyle.Dim } });
            }

            var diffArea = new VisualElement();

            // Backstop only: the finding row no longer offers AI Migrate for an over-cap file, so this panel should
            // never be built for one. Kept because the row's check and this one read the same file at different
            // moments — a file that grew past the cap in between must still stop here rather than burn a request.
            if (overCap)
            {
                status.text = L.Tr(
                    $"This file has {n} lines — above the current whole-file migration cap ({recipe.MaxLines}). Migrate it manually (see Explain for the playbook).",
                    $"该文件有 {n} 行，超过当前整文件迁移上限（{recipe.MaxLines} 行）。请手动迁移（迁移路径见 Explain）。");
                return box;
            }

            var gen = PerfLintStyle.AsSecondary(new Button { text = L.Tr($"Generate migration (send whole file, {n} lines, to {provider})", $"生成迁移（发送整个文件 {n} 行给 {provider}）") });
            gen.style.marginTop = 4;
            gen.clicked += () =>
            {
                if (!Entitlements.RequirePro(L.Tr("Migration Assistant", "迁移助手"))) return;
                if (!Entitlements.RequireAiCredit(L.Tr("AI Migrate", "AI 迁移"))) return;
                gen.SetEnabled(false);
                status.text = L.Tr("Generating (whole-file rewrites take longer than snippet fixes)…", "生成中（整文件重写比片段修复耗时更久）…");
                diffArea.Clear();
                MigrateService.Propose(recipe, target, p =>
                {
                    gen.SetEnabled(true);
                    if (!p.Ok) { status.text = L.Tr("Failed: ", "失败：") + p.Error; return; }
                    if (p.NoChange)
                    {
                        status.text = L.Tr("AI judged this file needs no migration — possibly already migrated.", "AI 判断此文件无需迁移——可能已完成迁移。");
                        return;
                    }
                    status.text = L.Tr("Migration generated and validated. Review the changed section, then apply:", "迁移已生成并通过校验。请审阅变更段后应用：");
                    RenderAiMigrateDiff(diffArea, p);
                });
            };
            box.Add(gen);
            box.Add(diffArea);
            return box;
        }

        private void RenderAiMigrateDiff(VisualElement area, MigrateProposal p)
        {
            area.Clear();
            AiFixDiffView.BuildFileDiffBlocks(area, p.Original, p.Migrated);

            var apply = PerfLintStyle.AsSecondary(new Button { text = L.Tr("Apply migration (rewrites the whole file; commit to version control first)", "应用迁移（整体重写该文件，建议先提交版本控制）") });
            apply.style.marginTop = 6;
            apply.clicked += () =>
            {
                // Shader targets verify synchronously (re-import + active compile, retries inline) — async only
                // because retry rounds call the LLM. C# targets keep the compile-scheduler path (domain reload).
                if (p.Recipe != null && p.Recipe.Kind == MigrateKind.Shader)
                {
                    apply.SetEnabled(false);
                    apply.text = L.Tr("Applying & verifying (compiling the shader; retries feed errors back automatically)…",
                                      "应用并验证中（正在编译该 shader；失败会自动喂回错误重试）…");
                    ShaderMigrateService.ApplyWithVerify(p, (ok2, msg2) => OnMigrateApplied(area, p, ok2, msg2,
                        rescanPath: p.VerifyAssetPath ?? p.FilePath));
                    return;
                }

                bool ok = MigrateService.Apply(p, out string msg);
                if (!ok) { EditorUtility.DisplayDialog(L.Tr("Apply failed", "应用失败"), msg, "OK"); return; }
                OnMigrateApplied(area, p, true, msg, rescanPath: p.FilePath);
            };
            area.Add(apply);
        }

        /// <summary>Shared post-apply UI for both migrate paths: success note + single-file rescan, or the failure dialog.</summary>
        private void OnMigrateApplied(VisualElement area, MigrateProposal p, bool ok, string msg, string rescanPath)
        {
            if (!ok)
            {
                EditorUtility.DisplayDialog(L.Tr("Apply failed", "应用失败"), msg, "OK");
                // Rebuild the apply button state by re-rendering the diff (the file was rolled back — the proposal is still valid to retry manually).
                RenderAiMigrateDiff(area, p);
                return;
            }

            ShowNotification(new GUIContent(L.Tr("AI migration applied", "AI 迁移已应用")));
            area.Clear();
            area.Add(new Label("✓ " + msg) { style = { color = PerfLintStyle.Good, whiteSpace = WhiteSpace.Normal } });

            // Shader hot-reload leaves runtime-fed state behind (C#-driven water systems, ambient bindings…) —
            // parts of the scene can render black until the scene reloads. Make the fix one click, with the
            // standard save prompt guarding unsaved changes (real case: Boat Attack water goes black after repair).
            if (p.Recipe != null && p.Recipe.Kind == MigrateKind.Shader)
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
                if (!string.IsNullOrEmpty(scene.path))
                {
                    var reload = new Button
                    {
                        text = L.Tr("Reload scene (restores lighting & shader-driven state, e.g. water)",
                                    "重新加载场景（恢复光照与 shader 关联状态，如水面）")
                    };
                    PerfLintStyle.AsSecondary(reload);
                    reload.style.marginTop = 4;
                    reload.style.alignSelf = Align.FlexStart;
                    reload.clicked += () =>
                    {
                        // Prompts to save when dirty; returns false on cancel — never drop user changes silently.
                        if (UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scene.path);
                    };
                    area.Add(reload);
                }
            }

            // Same in-place refresh as AI Fix: rescan only the affected assets, keep the scroll position.
            // Shader migrations rescan EVERY currently-flagged SHDR004 shader, not just the clicked one — a
            // shared-include fix heals siblings too (Water + WaterTessellated in the smoke test), and their
            // findings would otherwise sit stale until a full rescan.
            if (_lastResult != null && !string.IsNullOrEmpty(rescanPath))
            {
                Vector2 scroll = _results.scrollOffset;
                var rescanPaths = p.Recipe != null && p.Recipe.Kind == MigrateKind.Shader
                    ? ShaderMigrateService.AffectedShaderPaths(_lastResult.Findings, rescanPath)
                    : new System.Collections.Generic.List<string> { rescanPath };
                foreach (var path in rescanPaths)
                    _lastResult = ScanRunner.RescanFile(path, _lastResult);
                ScanResultStore.Save(_lastResult);
                RenderHeader(ListResult());
                RenderResults();
                RestoreScrollAfterLayout(scroll);
            }
        }

        private void RenderAiFixDiff(VisualElement area, ScriptFixProposal p)
        {
            area.Clear();
            AiFixDiffView.BuildDiffBlocks(area, p); // −original/＋fix/＋field/＋using (shared with the batch review window and the runtime panel)

            if (p.Locatable)
            {
                var apply = PerfLintStyle.AsSecondary(new Button { text = L.Tr("Apply fix (writes to file; commit to version control first)", "应用修复（写入文件，建议先提交版本控制）") });
                apply.style.marginTop = 6;
                apply.clicked += () =>
                {
                    bool ok = ScriptFixService.Apply(p, out string msg);
                    if (ok)
                    {
                        ShowNotification(new GUIContent(L.Tr("AI fix applied", "AI 修复已应用")));
                        area.Clear();
                        area.Add(new Label("✓ " + msg) { style = { color = PerfLintStyle.Good, whiteSpace = WhiteSpace.Normal } });

                        // Immediate refresh: rescan only the changed file and replace its warnings, avoiding a full rescan (86s-class).
                        // Doesn't depend on compile/domain reload — applying the fix deliberately doesn't trigger a reload.
                        if (_lastResult != null && !string.IsNullOrEmpty(p.FilePath))
                        {
                            // Preserve scroll position: RenderResults rebuilds the list and snaps the ScrollView to the top; the user wants to stay in place to keep looking at nearby warnings.
                            Vector2 scroll = _results.scrollOffset;
                            _lastResult = ScanRunner.RescanFile(p.FilePath, _lastResult);
                            ScanResultStore.Save(_lastResult);
                            RenderHeader(ListResult());
                            RenderResults();
                            RestoreScrollAfterLayout(scroll);
                        }
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(L.Tr("Apply failed", "应用失败"), msg, "OK");
                    }
                };
                area.Add(apply);
            }
        }

        /// <summary>Build a single finding's AI explanation panel: auto-fire the first explanation, support follow-up questions.</summary>
        private VisualElement BuildExplainPanel(Finding finding)
        {
            var conv = new ExplainConversation(finding);

            var box = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            box.style.marginTop = 4;

            // Read-only multiline TextField: content can wrap and be selected/copied (suited to answers with code snippets).
            var output = new TextField { multiline = true, isReadOnly = true };
            output.style.whiteSpace = WhiteSpace.Normal;
            box.Add(output);

            var inputRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6, display = DisplayStyle.None }
            };
            // flexGrow=1 alone isn't enough: a TextField won't shrink below its intrinsic content width, so a long
            // value pushes the "Ask follow-up" button off the right edge (clipped to "As…"). minWidth=0 lets the field
            // yield space; flexShrink=0 on the button keeps it fully visible. Same fix as the stale-banner "Rescan all" row.
            var field = new TextField { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
            var ask = PerfLintStyle.AsCompact(new Button { text = L.Tr("Ask follow-up", "追问") });
            ask.style.marginLeft = 4;
            ask.style.flexShrink = 0;
            inputRow.Add(field);
            inputRow.Add(ask);
            box.Add(inputRow);

            string transcript = "";

            void Run(string follow)
            {
                if (!string.IsNullOrEmpty(follow)) transcript += L.Tr("\n\n— You: ", "\n\n— 你：") + follow;
                string thinking = L.Tr("…thinking…", "…思考中…");
                output.value = transcript.Length > 0 ? transcript + "\n\n" + thinking : thinking;
                ask.SetEnabled(false);

                conv.Ask(follow, r =>
                {
                    ask.SetEnabled(true);
                    if (r.Success)
                    {
                        transcript += (transcript.Length > 0 ? "\n\n" : "") + r.Text;
                        output.value = transcript;
                        inputRow.style.display = DisplayStyle.Flex;
                    }
                    else
                    {
                        output.value = transcript + (transcript.Length > 0 ? "\n\n" : "") + L.Tr("Error: ", "出错：") + r.Error;
                    }
                });
            }

            ask.clicked += () =>
            {
                string q = field.value;
                if (string.IsNullOrWhiteSpace(q)) return;
                field.value = "";
                Run(q);
            };

            Run(null); // Auto-fire the first explanation
            return box;
        }

        private void ApplyFix(Finding finding)
        {
            if (!Entitlements.RequirePro(L.Tr("One-click fix", "一键修复"))) return;

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Apply Fix", "PerfLint — 应用修复"),
                $"{finding.Title}\n\n{finding.Fix.Preview()}\n\n" + L.Tr("This changes an import setting and reimports the asset. Edit > Undo will NOT revert it — restore from version control, or change the setting back in the Inspector.", "该改动会修改导入设置并重新导入资源。Edit > Undo 撤销不了——请从版本控制恢复，或在 Inspector 里改回来。"),
                L.Tr("Apply", "应用"), L.Tr("Cancel", "取消"));
            if (!confirm) return;

            // Same reason as the batch path: the re-import this causes is ours, and filing it as an unnamed user
            // edit counts one act twice and makes "was anything else done here?" answer yes to our own fix.
            FixResult r;
            using (ProjectEditJournal.SuppressUserEdits()) r = finding.Fix.Apply();

            if (r.Success)
            {
                // Named in the journal so a later before/after can say what the improvement was an improvement FROM.
                ProjectEditJournal.RecordFix(finding.RuleId, 1);
                ShowNotification(new GUIContent(r.Message ?? L.Tr("Fixed", "已修复")));
            }
            else EditorUtility.DisplayDialog(L.Tr("Fix failed", "修复失败"), r.Message, "OK");

            if (r.Success) OfferReMeasureAfterFix(1);

            RescanRules(new[] { finding.RuleId });
        }

        /// <summary>Run a single finding's "action-type action" (config changes etc., not undoable, not in Fix All). Separate confirmation dialog.</summary>
        private void RunAction(Finding finding)
        {
            var act = finding.Action;
            if (act == null) return;
            if (act.RequiresPro && !Entitlements.RequirePro(act.Label)) return;

            // Actions that let the user choose a target (e.g. "which duplicate copy to keep") open a chooser
            // instead of a plain confirm. The chooser runs the merge and we re-scan when it's done.
            if (act.SupportsTargetChoice && finding.HasGroup)
            {
                PerfLintDuplicateMergeWindow.Open(finding, () => RescanRules(new[] { finding.RuleId }));
                return;
            }

            // Anything the action knows only at click time goes FIRST, ahead of the confirmation. The confirm body is
            // written when the finding is created and describes the mechanics ("only adds a mark, low risk"); a
            // pre-flight warning is about whether to do it at all right now, and reading that after pressing Run is
            // reading it after the decision. Not merged into one dialog: Unity truncates DisplayDialog around ~500
            // characters, mid-sentence, and the two texts together clear that easily.
            PreflightWarning preflight = null;
            try { preflight = act.Preflight?.Invoke(); }
            catch { /* a failed check must not block the action */ }
            if (preflight != null && !string.IsNullOrEmpty(preflight.Message))
            {
                string title = L.Tr("PerfLint — Do this first?", "PerfLint — 要不要先做另一件事？");
                if (preflight.HasJump)
                {
                    // Three buttons, with the jump as the primary: a warning that says "handle that other rule first"
                    // and then leaves the user to go find it is most of the way to being ignored.
                    // DisplayDialogComplex returns 0=ok, 1=cancel, 2=alt.
                    int choice = EditorUtility.DisplayDialogComplex(title, preflight.Message,
                        preflight.JumpLabel, L.Tr("Cancel", "取消"), L.Tr("Continue anyway", "仍然继续"));
                    if (choice == 0) { FocusOnRule(preflight.JumpRuleId, preflight.JumpQuery); return; }
                    if (choice == 1) return;
                }
                else if (!EditorUtility.DisplayDialog(title, preflight.Message,
                                                      L.Tr("Continue anyway", "仍然继续"), L.Tr("Cancel", "取消")))
                    return;
            }

            bool confirm = EditorUtility.DisplayDialog(L.Tr("PerfLint — Run Action", "PerfLint — 执行操作"), act.ConfirmMessage, L.Tr("Run", "执行"), L.Tr("Cancel", "取消"));
            if (!confirm) return;

            var r = act.Run();
            if (r.Success) ShowNotification(new GUIContent(r.Message ?? L.Tr("Done", "已完成")));
            else EditorUtility.DisplayDialog(L.Tr("Action failed", "操作失败"), r.Message, "OK");

            RescanRules(new[] { finding.RuleId });
        }

        /// <summary>
        /// Rule-level batch merge for duplicate groups (ASSET.DUP001). Unlike <see cref="RunActionsForRule"/> (one call
        /// per finding), this routes through <see cref="DuplicateAssetMerger.MergeAll"/> which scans the project once
        /// for all groups — so N duplicate groups don't trigger N full scans. Each group keeps its most-referenced copy.
        /// </summary>
        private void RunMergeAllForDuplicates(IReadOnlyList<Finding> findings)
        {
            if (findings == null || findings.Count == 0) return;
            var act = findings[0].Action;
            if (act == null) return;
            if (act.RequiresPro && !Entitlements.RequirePro(act.Label)) return;

            var groups = findings.Where(f => f.HasGroup).Select(f => f.Group).ToList();
            if (groups.Count == 0) return;

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Merge Duplicates", "PerfLint — 合并去重"),
                L.Tr($"Merge {groups.Count} duplicate group(s), keeping the most-referenced copy in each.\n\n",
                     $"合并 {groups.Count} 个重复组，每组保留被引用最多的那份。\n\n") + PerfLintWarnings.Irreversible,
                $"{L.Tr("Merge all", "全部合并")} ({groups.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            var r = DuplicateAssetMerger.MergeAll(groups);
            // Always show a dialog so the user sees the outcome (how many merged, how many left for manual handling).
            EditorUtility.DisplayDialog(
                r.Success ? L.Tr("Merge complete", "合并完成") : L.Tr("Merge failed", "合并失败"),
                r.Message, "OK");

            RescanRules(findings.Select(f => f.RuleId));
        }

        /// <summary>Rule-level batch run of "action-type actions". One confirmation covers all; run one by one then summarize, finally save and rescan in one go.</summary>
        private void RunActionsForRule(IReadOnlyList<Finding> findings)
        {
            if (findings == null || findings.Count == 0) return;
            var first = findings[0].Action;
            if (first == null) return;
            if (first.RequiresPro && !Entitlements.RequirePro(first.Label)) return;

            // Batch-specific confirm body when provided: the per-finding ConfirmMessage names ONE asset (misleading for
            // a 331-item run) and can overflow Unity's dialog length limit (which truncates mid-sentence and appends
            // "see the editor log file"). Fallback keeps the old composition for actions without a batch message.
            string body = !string.IsNullOrEmpty(first.BatchConfirmMessage)
                ? first.BatchConfirmMessage
                : L.Tr($"{first.ConfirmMessage}\n\n(The undo note above applies to each item.)",
                       $"{first.ConfirmMessage}\n\n（以上撤销说明适用于每一项。）");
            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Batch Run", "PerfLint — 批量执行"),
                L.Tr($"Will run '{first.Label}' on {findings.Count} items.\n\n{body}",
                     $"将对 {findings.Count} 个项执行「{first.Label}」。\n\n{body}"),
                $"{L.Tr("Run all", "执行全部")} ({findings.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            // A) The action provides a whole-batch entry point (e.g. Addressables extract: one SaveAssets for the
            //    whole set instead of N, plus a before/after dedup self-check). Hand it every target path in one call.
            if (first.SupportsBatchRun)
            {
                var paths = findings.Select(f => f.TargetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
                var br = first.BatchRun(paths);
                // Always a modal dialog (success too): the previous fading toast meant a successful batch looked like
                // "nothing happened" — the report is the whole point of a 300-item run.
                EditorUtility.DisplayDialog(
                    br.Success ? L.Tr("Batch complete", "批量完成") : L.Tr("Batch finished with issues", "批量执行完成（有问题）"),
                    br.Message ?? "", "OK");
                RescanRules(findings.Select(f => f.RuleId));
                return;
            }

            // B) Generic per-item path. Don't use Start/StopAssetEditing: extraction doesn't involve asset reimport, and
            //    it would defer Addressables' SaveAssets, causing entries not to persist. A progress bar keeps a long run
            //    from looking frozen; the completion dialog always shows (success included).
            int ok = 0, fail = 0;
            string lastErr = null;
            try
            {
                for (int i = 0; i < findings.Count; i++)
                {
                    var f = findings[i];
                    if (f.Action == null) continue;
                    if (EditorUtility.DisplayCancelableProgressBar(first.Label, $"{i + 1}/{findings.Count}", (float)i / findings.Count))
                        break;
                    var r = f.Action.Run();
                    if (r.Success) ok++;
                    else { fail++; lastErr = r.Message; }
                }
                AssetDatabase.SaveAssets();
            }
            finally { EditorUtility.ClearProgressBar(); }

            EditorUtility.DisplayDialog(
                fail == 0 ? L.Tr("Batch complete", "批量完成") : L.Tr("Batch finished with issues", "批量执行完成（有问题）"),
                fail == 0
                    ? L.Tr($"Ran {ok} item(s) successfully.", $"成功执行 {ok} 项。")
                    : L.Tr($"Ran {ok}, failed {fail}.\nFirst error: {lastErr}", $"成功 {ok} 项，失败 {fail} 项。\n首个错误：{lastErr}"),
                "OK");
            RescanRules(findings.Select(f => f.RuleId));
        }

        private void FixAllInResult()
        {
            if (_lastResult == null) return;
            ApplyFixes(_lastResult.Findings.Where(f => f.CanAutoFix).ToList(), L.Tr("All", "全部"));
        }

        // ── One-click optimize (dimension-oriented: memory / build size) ─────────────────────────
        // The savings-line buttons open a plan dialog; its auto tier reuses the Fix All loop and its decision tier
        // dispatches through the SAME per-action flows as the finding rows (each with its own confirmation), so no
        // consent wording is bypassed. The "optimized ~X" figure is before-minus-after of the aggregate estimate,
        // i.e. verified by the incremental rescan rather than tallied from what we merely attempted.

        /// <summary>
        /// Undoes whatever is narrowing the list — a rule focus, a script focus, or a typed search.
        ///
        /// All three narrow the same list and only one of them was reversible by an obvious gesture, so this is one
        /// button for all of them rather than three ways out the reader has to tell apart.
        /// </summary>
        private void ClearRuleFocus()
        {
            _ruleFocus = null;
            _ruleFocusLabel = null;
            _focusedScriptNoFindings = null;
            _search = string.Empty;
            if (_searchField != null) _searchField.value = string.Empty;
            SyncSearchPlaceholder();
            RenderResults();
        }

        /// <summary>
        /// Opens the optimize plan dialog for one dimension. No-op when the current result has no executable savings.
        ///
        /// Internal rather than private because the Autopilot's round offers these now — it is where "which items are
        /// we executing this round" lives. The plan, the dialog, the Pro gate and the executor all stay here: this
        /// window owns the scan the plan is built from and shows the result of running it.
        /// </summary>
        internal void OpenOptimizeDialog(SavingsDimension dimension)
        {
            if (_lastResult == null) return;
            // Memory plans are scene-scoped: only work whose effect a build of the open scene(s) can show.
            var plan = OptimizePlan.Build(_lastResult.Findings, dimension,
                dimension == SavingsDimension.Memory ? GetOpenSceneDependencies() : null);
            if (plan.IsEmpty) { ShowNotification(new GUIContent(L.Tr("Nothing executable for this dimension", "该维度暂无可执行项"))); return; }
            PerfLintOptimizeWindow.Open(this, plan);
        }

        /// <summary>
        /// Brings a decision group built from a RESTORED scan back to something runnable.
        ///
        /// Re-scans just that rule (the delegates are rebuilt by the scanner, they cannot be deserialized) and takes
        /// the freshly built group of the same rule, so the dimension and scene filters stay exactly the plan's own —
        /// re-deriving them here would be a second place for "what is in scope" to drift.
        ///
        /// Returns null when the rule comes back with nothing to run, which is a real outcome and not a failure: the
        /// restored report may simply be out of date and the waste already gone. Says so rather than proceeding into a
        /// dispatch that would return silently.
        /// </summary>
        private OptimizePlan.DecisionGroup ReviveDecisionGroup(OptimizePlan.DecisionGroup stale, SavingsDimension dimension)
        {
            RescanRules(new[] { stale.RuleId });
            if (_lastResult == null) return null;

            var rebuilt = OptimizePlan.Build(_lastResult.Findings, dimension,
                dimension == SavingsDimension.Memory ? GetOpenSceneDependencies() : null);
            var fresh = rebuilt.DecisionGroups.FirstOrDefault(x => x.RuleId == stale.RuleId);

            // Still no live Action after a rescan. Never dispatch that — it is the silent no-op this whole method
            // exists to prevent. But do NOT report it as "there is nothing left to do": the likeliest cause is that
            // the rescan did not complete, and the likeliest reason for THAT is a dialog the user just dismissed.
            // Addressables' analysis refuses to run with unsaved scenes and puts up a modal "Modified Scenes must be
            // saved to continue" — measured on a real project, where it blocked the editor's main thread for six
            // minutes waiting for someone to click it. Cancelling that is a perfectly reasonable answer, and telling
            // the user their 366 findings evaporated because of it would be a lie in the direction of alarm.
            if (fresh == null || fresh.NeedsRevive)
            {
                ShowNotification(new GUIContent(L.Tr($"{stale.Label}: the re-scan didn't return an action for this rule — if you dismissed a \"save scenes\" prompt just now, that's why. Nothing was changed.",
                                                     $"{stale.Label}：重扫没能拿回该规则的操作——如果刚才关掉了「保存场景」提示，那就是原因。本次未做任何改动。")));
                return null;
            }
            return fresh;
        }

        /// <summary>Executes an optimize plan: auto tier in one batch, then each chosen decision group through its normal (confirming) flow.</summary>
        internal void RunOptimizePlan(OptimizePlan plan, IReadOnlyList<OptimizePlan.DecisionGroup> chosenGroups)
        {
            if (plan == null || _lastResult == null) return;
            if (!Entitlements.RequirePro(L.Tr("One-click optimize", "一键优化"))) return;

            // FIRM totals only: the ceiling portion (Mipmap Streaming pool — camera-dependent) must never enter
            // the "optimized ~X for you" claim. Tracked separately so the run can still SAY streaming was enabled.
            var beforeTotals = SavingsSummary.Compute(_lastResult.Findings);
            long before = DimensionFirmTotal(beforeTotals, plan.Dimension);
            long beforeCeiling = plan.Dimension == SavingsDimension.Memory ? beforeTotals.MemoryCeilingBytes : 0;

            if (plan.AutoItems.Count > 0)
            {
                ApplyFixesCore(plan.AutoItems, out _, out _);
                RescanRules(plan.AutoItems.Select(f => f.RuleId));
            }

            if (chosenGroups != null)
            {
                foreach (var chosen in chosenGroups)
                {
                    if (chosen == null || chosen.Findings == null || chosen.Findings.Count == 0) continue;
                    // A group built from a restored scan carries no live Action, and every executor below returns
                    // silently on Action == null — the user would confirm a run that does nothing. Re-scan the rule
                    // first to bring the delegates back, the same revival the auto tier gets from ApplyFixList.
                    var g = chosen.NeedsRevive ? ReviveDecisionGroup(chosen, plan.Dimension) : chosen;
                    if (g == null || g.Findings == null || g.Findings.Count == 0) continue;
                    // Same dispatch as the finding rows — each path shows its own confirmation and rescans its rule.
                    if (g.RuleId == "ASSET.DUP001") RunMergeAllForDuplicates(g.Findings);
                    else if (g.Findings.Count == 1) RunAction(g.Findings[0]);
                    else if (g.Findings[0].Action != null && !g.Findings[0].Action.AllowRuleBatch)
                        // Defensive: actions marked not-batchable (per-item domain reload etc.) run one by one with
                        // per-item consent. No such rule carries a savings estimate today, but if one ever does this
                        // must not silently loop through RunActionsForRule's single-confirm batch.
                        foreach (var f in g.Findings) RunAction(f);
                    else RunActionsForRule(g.Findings);
                }
            }

            var afterTotals = _lastResult != null ? SavingsSummary.Compute(_lastResult.Findings) : beforeTotals;
            long after = DimensionFirmTotal(afterTotals, plan.Dimension);
            long reclaimed = Math.Max(0, before - after);
            // The ceiling that stopped being reported = the opportunity the run switched on (e.g. streaming enabled).
            long afterCeiling = plan.Dimension == SavingsDimension.Memory ? afterTotals.MemoryCeilingBytes : 0;
            long ceilingClaimed = Math.Max(0, beforeCeiling - afterCeiling);
            // Diagnostic breadcrumb: the tally only ever grows here, so any surprising "Optimized for you" figure
            // can be traced to the exact run (and its firm/ceiling split) in the Console.
            Debug.Log($"[PerfLint] Optimize run ({plan.Dimension}): firm {before} → {after} (claimed {reclaimed}), ceiling delta {ceilingClaimed}");
            if (plan.Dimension == SavingsDimension.Memory) _optimizedMemBytes += reclaimed;
            else _optimizedBuildBytes += reclaimed;
            if (_lastResult != null) RenderHeader(ListResult());

            string dimWord = plan.Dimension == SavingsDimension.Memory ? L.Tr("memory", "内存") : L.Tr("build size", "包体");
            string body;
            if (reclaimed > 0)
                body = L.Tr($"Optimized ~{ScannerUtil.Human(reclaimed)} of {dimWord} for you (estimate; the re-scan no longer reports that space).",
                            $"已为您优化约 {ScannerUtil.Human(reclaimed)} {dimWord}（估算口径；重扫后这部分空间不再出现）。");
            else if (ceilingClaimed > 0)
                // Only opportunity-type items ran — nothing failed, there is just no firm reclaim to tally.
                // The old "cancelled or failed" wording here read as an error for a perfectly successful run.
                body = L.Tr("The items you ran are opportunity-type, so no firm figure is tallied.",
                            "本次执行的是机会型优化，无确定性战绩入账。");
            else
                body = L.Tr("No verified change — steps may have been cancelled or failed (see the notifications above).",
                            "没有可验证的变化——操作可能被取消或执行失败（见前面的提示）。");
            // Opportunity-type work is reported separately and NEVER as a reclaim figure: the pool is an upper
            // bound and real savings depend on the camera/scene — a device A/B would disprove a merged claim.
            // Streaming specifics: it only evicts mips once demand exceeds the Memory Budget, so on small scenes
            // the (512MB-default) budget can mean exactly zero effect — say so with the LIVE budget value and point
            // at the tuning deck, or the first same-scene A/B a user takes reads as "it did nothing" (real case).
            // DisplayDialog truncates around ~500 chars, so the full budget explanation only ships in the
            // ceiling-only case; when a firm figure is also present, the ceiling note stays short.
            if (ceilingClaimed > 0 && reclaimed == 0)
                body += L.Tr($"\n\nEnabled (e.g. Mipmap Streaming): pool ceiling ~{ScannerUtil.Human(ceilingClaimed)}. Actual savings depend on camera, scene AND the streaming Memory Budget — currently {QualitySettings.streamingMipmapsMemoryBudget:0} MB; scenes whose texture demand stays under it will see no change. Tune it in Runtime Profiler > Texture Streaming (lower the budget until the over-budget line appears, then back off).",
                             $"\n\n已启用（如 Mipmap Streaming）：池子上限约 {ScannerUtil.Human(ceilingClaimed)}。实际节省取决于相机、场景和串流 Memory Budget——当前 {QualitySettings.streamingMipmapsMemoryBudget:0} MB；纹理需求低于预算的场景将看不到变化。请到 Runtime Profiler > Texture Streaming 调参（把预算往下调到出现超额提示再回退）。");
            else if (ceilingClaimed > 0)
                body += L.Tr($"\n\nAlso enabled opportunity optimizations: pool ceiling ~{ScannerUtil.Human(ceilingClaimed)} — actual savings depend on camera/scene/Memory Budget (tune in Runtime Profiler > Texture Streaming).",
                             $"\n\n另启用了机会型优化：池子上限约 {ScannerUtil.Human(ceilingClaimed)}，实际取决于相机/场景/Memory Budget（Runtime Profiler > Texture Streaming 可调参）。");
            if (reclaimed > 0)
                body += L.Tr("\n\nTo verify on device: take two Memory Profiler snapshots at the SAME scene and moment (before/after), and compare the Texture2D/Mesh categories or the specific assets — not the total (audio/RTs/native fluctuate on their own).",
                             "\n\n真机复测姿势：同一场景同一时点各拍一张 Memory Profiler 快照对比，看 Texture2D/Mesh 类别或具体资产——别看总量（音频/RT/原生分配会自己波动）。");
            EditorUtility.DisplayDialog(L.Tr("PerfLint — Optimize complete", "PerfLint — 优化完成"), body, "OK");
        }

        private static long DimensionFirmTotal(SavingsSummary.Totals t, SavingsDimension d) =>
            d == SavingsDimension.Memory ? t.FirmMemoryBytes : t.BuildBytes;

        // Open scenes' recursive dependency set, cached by the loaded-scene-paths key: GetDependencies over big
        // scenes is too slow for every RenderHeader, but the set only changes when the open scene set changes.
        private string _sceneDepsKey;
        private HashSet<string> _sceneDeps;

        /// <summary>
        /// Everything the open scenes reference. Shared, because a memory plan is scene-scoped and the Autopilot now
        /// offers the same plan — two windows computing this separately is two chances for them to disagree about
        /// what is in scope.
        /// </summary>
        internal static HashSet<string> OpenSceneDependencies()
        {
            var scenePaths = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (sc.isLoaded && !string.IsNullOrEmpty(sc.path)) scenePaths.Add(sc.path);
            }
            string key = string.Join(";", scenePaths);
            if (key == _sharedSceneDepsKey && _sharedSceneDeps != null) return _sharedSceneDeps;

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in scenePaths)
                foreach (var d in AssetDatabase.GetDependencies(p, true))
                    set.Add(d);
            _sharedSceneDepsKey = key;
            _sharedSceneDeps = set;
            return set;
        }

        static string _sharedSceneDepsKey;
        static HashSet<string> _sharedSceneDeps;

        private HashSet<string> GetOpenSceneDependencies() => OpenSceneDependencies();

        /// <summary>Batch-apply fixes to a given fixable set, using Start/StopAssetEditing to batch the reimports.</summary>
        private void ApplyFixes(IReadOnlyList<Finding> fixables, string label)
        {
            if (fixables == null || fixables.Count == 0) return;
            if (!Entitlements.RequirePro(L.Tr("Batch auto-fix", "批量自动修复"))) return;

            string breakdown = string.Join("\n",
                fixables.GroupBy(f => f.RuleId)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"  · {g.Key}: {g.Count()}"));

            // Same shape as FindingActions.ConfirmApply, and for the same reason: the closing sentence is the one
            // people act on, so "with this many the practical undo is version control" must not be said about one
            // import setting, where the Inspector really is the easy way back.
            bool one = fixables.Count == 1;
            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Batch Fix", "PerfLint — 批量修复"),
                (one
                    ? L.Tr($"Will apply the auto-fix for {label} to 1 asset.\n\n", $"将对 1 个资源应用 {label} 的自动修复。\n\n")
                    : L.Tr($"Will apply auto-fix to {fixables.Count} assets ({label}):\n\n{breakdown}\n\n",
                           $"将对 {fixables.Count} 个资源（{label}）应用自动修复：\n\n{breakdown}\n\n")) +
                (one
                    ? L.Tr("This modifies an asset import setting and reimports it. Edit > Undo will NOT revert it — set it back in the Inspector, or restore from version control.",
                           "这会修改一项资源导入设置并触发重新导入。Edit > Undo 撤销不了——在 Inspector 里改回来，或从版本控制恢复。")
                    : L.Tr("These changes modify asset import settings and trigger reimport. Edit > Undo will NOT revert them — each setting can be changed back in the Inspector, but with this many the practical undo is version control.\nCommit your project first.",
                           "这些改动会修改资源导入设置并触发重新导入。Edit > Undo 撤销不了——每一项都可以在 Inspector 里改回来，但数量一多，实际能依靠的是版本控制。\n请先提交你的工程。")),
                one ? L.Tr("Fix", "修复") : $"{L.Tr("Fix", "修复")} ({fixables.Count})",
                L.Tr("Cancel", "取消"));
            if (!confirm) return;

            ApplyFixesCore(fixables, out int success, out int failed);

            ShowNotification(new GUIContent(L.Tr($"Batch fix done: {success} succeeded, {failed} failed", $"批量修复完成：成功 {success}，失败 {failed}")));
            RescanRules(fixables.Select(f => f.RuleId));

            // Asked after the rescan, so the panel already shows the new state when the question appears.
            OfferReMeasureAfterFix(success);
        }

        /// <summary>
        /// The batch-fix execution loop WITHOUT gating/confirmation/rescan — shared by ApplyFixes (which confirms via
        /// its own dialog) and the one-click optimize flow (where the plan dialog is the single confirmation).
        /// Callers are responsible for the Pro gate, user consent, and the follow-up rescan.
        /// </summary>
        private void ApplyFixesCore(IReadOnlyList<Finding> fixables, out int success, out int failed)
        {
            success = 0; failed = 0;
            var failures = new List<string>();
            // Tallied per rule rather than per finding: "200 uncompressed textures" is one decision the user made,
            // and a journal listing it 200 times would bury every other change they made that session.
            var appliedByRule = new Dictionary<string, int>(StringComparer.Ordinal);

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < fixables.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L.Tr("PerfLint — Batch Fix", "PerfLint — 批量修复"),
                            $"{i + 1}/{fixables.Count}  {fixables[i].Title}",
                            (float)i / fixables.Count))
                        break;

                    try
                    {
                        var res = fixables[i].Fix.Apply();
                        if (res.Success)
                        {
                            success++;
                            string rid = fixables[i].RuleId ?? "";
                            appliedByRule[rid] = (appliedByRule.TryGetValue(rid, out int n) ? n : 0) + 1;
                        }
                        else { failed++; failures.Add($"{fixables[i].RuleId}: {res.Message}"); }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures.Add($"{fixables[i].RuleId}: {ex.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            foreach (var kv in appliedByRule) ProjectEditJournal.RecordFix(kv.Key, kv.Value);

            if (failed > 0)
                Debug.LogWarning($"[PerfLint] " + L.Tr($"Batch fix done: {success} succeeded, {failed} failed.\n", $"批量修复完成：成功 {success}，失败 {failed}。\n") +
                                 string.Join("\n", failures.Take(20)));
        }

        /// <summary>
        /// "Batch fix" all of a rule's AI-fixable findings. **Changed to semi-automatic since [0.21.x]**: first generate proposals one by one (without writing files),
        /// then review each diff in the review window and check which to apply, writing the checked ones all at once only after confirmation — handing the "break the code" risk back to the user to confirm on the diff.
        /// (The old version "generated each and wrote to disk automatically", letting semantically wrong fixes land without review.)
        /// </summary>
        private void AiFixAllForRule(string ruleId)
        {
            if (!Entitlements.RequireAiCredit(L.Tr("AI batch fix", "AI 批量修复"))) return;
            if (_lastResult == null) return;

            // Dedupe by (file, line): one line may have several findings of the same rule (e.g. two Camera.main on one line → two UPD003),
            // and AI fixes the whole line in one shot, so applying the second is bound to be redundant / fail to locate. Generate/apply once per line (see AiFixBatch.DedupeByLine).
            var findings = AiFixBatch.DedupeByLine(
                _lastResult.Findings.Where(f => f.RuleId == ruleId && f.AiFixable));
            if (findings.Count == 0) return;

            string provider = LlmSettings.ProviderDisplayName;
            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — AI Batch Fix", "PerfLint — AI 批量修复"),
                L.Tr($"Will generate AI fixes for the {findings.Count} findings of rule {ruleId}, one call per finding (consuming tokens):\n\n", $"将对规则 {ruleId} 的 {findings.Count} 条逐条用 AI 生成修复（每条一次调用、消耗 token）：\n\n") +
                L.Tr($"· Each sends only its code snippet to {provider}; nothing is written yet.\n", $"· 每条只把对应代码片段（仅那一段）发送到 {provider}；此刻不写入任何文件。\n") +
                L.Tr("· After generation you'll review every diff and pick which to apply — only the ones you check are written.", "· 生成后你会逐条看 diff 并勾选要应用的——仅勾选的会被写入。"),
                $"{L.Tr("Generate", "生成")} ({findings.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            AiFixGenerateAll(ruleId, findings, 0, new List<AiFixCandidate>());
        }

        /// <summary>Phase 1: Propose one by one to collect proposals (**without writing files**); open the review window once all are generated or the user cancels.</summary>
        private void AiFixGenerateAll(string ruleId, List<Finding> findings, int i, List<AiFixCandidate> collected)
        {
            if (i >= findings.Count)
            {
                EditorUtility.ClearProgressBar();
                OpenAiFixReview(ruleId, collected);
                return;
            }

            if (EditorUtility.DisplayCancelableProgressBar(
                    L.Tr("PerfLint — AI Batch Fix (generating)", "PerfLint — AI 批量修复（生成中）"),
                    $"{i + 1}/{findings.Count}  {findings[i].Title}", (float)i / findings.Count))
            {
                EditorUtility.ClearProgressBar();
                if (collected.Count > 0) OpenAiFixReview(ruleId, collected); // Already-generated ones can still be reviewed
                else ShowNotification(new GUIContent(L.Tr("AI batch fix canceled", "已取消 AI 批量修复")));
                return;
            }

            var finding = findings[i];
            ScriptFixService.Propose(finding, p =>
            {
                collected.Add(new AiFixCandidate { Finding = finding, Proposal = p });
                AiFixGenerateAll(ruleId, findings, i + 1, collected);
            });
        }

        /// <summary>Phase 2: Open the review window. If there are no applicable items, just explain why instead of opening an empty window.</summary>
        private void OpenAiFixReview(string ruleId, List<AiFixCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;
            if (!candidates.Any(c => AiFixBatch.IsApplicable(c.Proposal)))
            {
                EditorUtility.DisplayDialog(
                    L.Tr("AI Batch Fix", "AI 批量修复"),
                    L.Tr("None of the generated fixes can be applied (couldn't locate / no change needed / generation failed). Handle these manually.",
                         "生成的修复没有一条可应用（无法定位 / 无需改动 / 生成失败）。请手动处理。"),
                    "OK");
                return;
            }
            PerfLintAiFixReviewWindow.Open(ruleId, candidates, selected => ApplyReviewedAiFixes(selected));
        }

        /// <summary>
        /// Phase 3: After the user checks and confirms in the review window, write the selected proposals serially. Suspend background compilation to avoid a domain reload interrupting the loop;
        /// Apply one by one (each Apply re-locates by content, tolerating line-number drift from the previous change) + incremental rescan, with a unified verification at the end.
        /// </summary>
        private void ApplyReviewedAiFixes(List<ScriptFixProposal> selected)
        {
            if (selected == null || selected.Count == 0) return;

            // Multiple in the same file: group by file and apply **bottom-up** within a group (descending expected line) — fix lower lines first so the positions of upper lines still to be fixed don't shift,
            // reducing interference between fixes in the same file. Combined with LocateRegion's "closest to expected line" anchoring, even duplicate lines land in their right places
            // (the old batch rescanned and regenerated per file each time, so it didn't have this problem; after switching to "generate all at once" these two measures are required to keep same-file fixes from crossing wires).
            var ordered = selected.OrderBy(p => p.FilePath ?? "", StringComparer.Ordinal)
                                  .ThenByDescending(p => p.ExpectedLine)
                                  .ToList();

            PerfLintFixCompileScheduler.Suspend();
            int applied = 0, failed = 0;
            string lastErr = null;
            try
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L.Tr("PerfLint — AI Batch Fix (applying)", "PerfLint — AI 批量修复（应用中）"),
                            $"{i + 1}/{ordered.Count}  {ShortName(p.FilePath)}", (float)i / ordered.Count))
                        break;

                    if (ScriptFixService.Apply(p, out string msg))
                    {
                        applied++;
                        _lastResult = ScanRunner.RescanFile(p.FilePath, _lastResult); // Refresh this file, absorbing line-number drift
                    }
                    else { failed++; lastErr = msg; }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                PerfLintFixCompileScheduler.Resume(); // Each Apply already registered its own background verification; on resume it triggers once for all
            }

            ShowNotification(new GUIContent(L.Tr($"AI batch fix: {applied} applied, {failed} failed", $"AI 批量修复：已应用 {applied}，失败 {failed}")));
            if (failed > 0 && lastErr != null)
                EditorUtility.DisplayDialog(L.Tr("Some fixes failed", "部分修复失败"),
                    L.Tr($"{failed} couldn't be applied. Last error: {lastErr}\n\n", $"{failed} 条未能应用。最后错误：{lastErr}\n\n") +
                    L.Tr("This usually means a sibling fix on the same/adjacent line already changed it. Just run 'AI Fix all' again to retry the rest (it regenerates against the current file), or use 'AI Fix' on each remaining one.",
                         "这通常是因为同一行/相邻行的另一条修复已经改过它。直接再点一次「AI Fix 全部」即可重试剩余项（会基于当前文件重新生成），或对剩余每条点「AI Fix」。"),
                    "OK");

            if (_lastResult != null)
            {
                ScanResultStore.Save(_lastResult); // Persist once at the end of the batch, avoiding per-item IO
                Vector2 scroll = _results.scrollOffset;
                RenderHeader(ListResult());
                RenderResults();
                RestoreScrollAfterLayout(scroll);
            }
        }

        private static string ShortName(string path) => string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);

        /// <summary>
        /// After a fix, rescan only the affected "groups" (the scanners owning the rules of the fixed findings) and replace their results —
        /// no more full rescan (86s-class). Preserve filters and scroll position. When there's no ownership table, ScanRunner.RescanRules safely falls back to a full scan.
        /// </summary>
        private void RescanRules(IEnumerable<string> affectedRuleIds)
        {
            if (_lastResult == null) { RunScan(); return; }

            var ids = (affectedRuleIds ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (ids.Count == 0) { RunScan(); return; }

            Vector2 scroll = _results.scrollOffset;
            try
            {
                var ctx = new ScanContext(
                    cancellationToken: CancellationToken.None,
                    reportProgress: (name, p) =>
                        EditorUtility.DisplayProgressBar("PerfLint", $"Rescanning: {name}", p));
                _lastResult = ScanRunner.RescanRules(ids, _lastResult, ctx);
                foreach (var id in ids) _restoredFixableRuleIds.Remove(id); // After rescan this rule has live findings
                // All restored rules have been rescanned → the report is live enough, remove the info banner.
                if (_restoredFixableRuleIds.Count == 0 && _staleBanner != null)
                    _staleBanner.style.display = DisplayStyle.None;
                ScanResultStore.Save(_lastResult);
            }
            catch (OperationCanceledException) { /* User canceled, keep existing results */ }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            RenderHeader(ListResult());
            RenderResults();
            RestoreScrollAfterLayout(scroll);
        }

        /// <summary>Select a group of assets in the Project window (for cases where one finding involves multiple assets, e.g. a duplicate group).</summary>
        private static void SelectGroup(IReadOnlyList<string> paths)
        {
            var objs = new List<UnityEngine.Object>();
            foreach (var p in paths)
            {
                var o = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p);
                if (o != null) objs.Add(o);
            }
            if (objs.Count == 0) return;
            Selection.objects = objs.ToArray();
            EditorGUIUtility.PingObject(objs[0]);
        }

        /// <summary>Export the results under the current filter to CSV — the realistic way to handle the 20k-item scale (sort/batch-process offline in a spreadsheet).</summary>
        private void ExportCsv()
        {
            if (_lastResult == null) { ShowNotification(new GUIContent(L.Tr("Scan first", "请先扫描"))); return; }
            // CSV mirrors the LIST — it is the same rows through the same filters, in a spreadsheet. Feeding it the
            // merged result would filter runtime findings through controls that no longer describe them. The HTML
            // report is the other kind of export, the one you hand to somebody else, and it still carries both.
            var rows = ListResult().Findings.Where(PassesFilter).ToList();
            if (rows.Count == 0) { ShowNotification(new GUIContent(L.Tr("Nothing to export under the current filter", "当前筛选下无可导出项"))); return; }

            string path = EditorUtility.SaveFilePanel(L.Tr("Export PerfLint report", "导出 PerfLint 报告"), "", "perflint-report.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine("Severity,Domain,RuleId,Title,Path,Detail");
            foreach (var f in rows)
                sb.AppendLine(string.Join(",",
                    Csv(f.Severity.ToString()), Csv(f.Domain.ToString()), Csv(f.RuleId),
                    Csv(f.Title), Csv(f.TargetPath), Csv(OneLine(f.Detail))));

            try
            {
                // UTF-8 with BOM, to ensure Excel displays Chinese correctly.
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                ShowNotification(new GUIContent(L.Tr($"Exported {rows.Count} rows to CSV", $"已导出 {rows.Count} 条到 CSV")));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(L.Tr("Export failed", "导出失败"), ex.Message, "OK");
            }
        }

        /// <summary>
        /// Export a self-contained, shareable HTML health report (cold-start acquisition hook). The whole report reflects the **full** scan (the score is the overall headline),
        /// unaffected by the current filter — what gets shared is the project's overall health, not some filtered view. Keep zero telemetry: purely local file write, no upload.
        /// </summary>
        private void ExportHtml()
        {
            if (_lastResult == null) { ShowNotification(new GUIContent(L.Tr("Scan first", "请先扫描"))); return; }

            string defaultName = "perflint-report-" + SanitizeFileName(Application.productName) + ".html";
            string path = EditorUtility.SaveFilePanel(L.Tr("Export PerfLint HTML report", "导出 PerfLint HTML 报告"), "", defaultName, "html");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string html = HtmlReport.Build(DisplayResult(), Application.productName, DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    RuntimeSessionApplies() ? _runtimeSession.ToEvidence() : null);
                File.WriteAllText(path, html, new UTF8Encoding(false)); // HTML already declares charset=utf-8, no BOM needed
                ShowNotification(new GUIContent(L.Tr("HTML report exported", "已导出 HTML 报告")));
                if (EditorUtility.DisplayDialog(
                        L.Tr("Report exported", "报告已导出"),
                        L.Tr($"Saved to:\n{path}\n\nOpen it now?", $"已保存到：\n{path}\n\n现在打开它？"),
                        L.Tr("Open", "打开"), L.Tr("Close", "关闭")))
                    Application.OpenURL("file://" + path.Replace("\\", "/"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(L.Tr("Export failed", "导出失败"), ex.Message, "OK");
            }
        }

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unity";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Replace(' ', '-');
        }

        private static string Csv(string s)
        {
            s ??= "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");

        // ── Filtering and visual helpers ──────────────────────────────
        private bool PassesFilter(Finding f)
        {
            bool sevOk = f.Severity switch
            {
                Severity.Critical => _showCritical,
                Severity.Warning => _showWarning,
                _ => _showInfo
            };
            if (!sevOk) return false;
            if (_onlyFixable && !f.CanAutoFix) return false;
            if (_ruleFocus != null && !_ruleFocus(f)) return false;
            if (!string.IsNullOrEmpty(_search))
            {
                string q = _search.Trim();
                bool hit =
                    (f.RuleId?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (f.Title?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (f.TargetPath?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) return false;
            }
            return true;
        }

        /// <summary>
        /// Jump from the runtime panel's "line-by-line analysis": focus the static report on a given script — set the search filter to that script's path and enable the Info filter
        /// (line-level clues like GC004 are Info by default), so the user immediately sees all of that script's line-level findings without digging through tens of thousands.
        /// This closes the loop of "runtime confirms where it's slow → static locates which lines".
        ///
        /// Analyze only this one script and scan nothing else, to guarantee instant results:
        ///   · Full results already exist → use RescanFile to refresh only this file's findings, leaving the rest as is;
        ///   · No results yet → use ScanFileOnly to scan only this script, producing a standalone result (without triggering an 86s-class full scan).
        /// </summary>
        public void FocusOnScript(string scriptPath, bool fromAllocationFinding = false)
        {
            if (string.IsNullOrEmpty(scriptPath)) return;
            _focusedScriptFromAllocation = fromAllocationFinding;

            _search = scriptPath;
            _showInfo = true;

            // Do file-level analysis only for this script, avoiding a full scan.
            var ctx = new ScanContext(
                cancellationToken: CancellationToken.None,
                reportProgress: (name, p) => EditorUtility.DisplayProgressBar("PerfLint", $"Analyzing: {name}", p));
            try
            {
                _lastResult = _lastResult == null
                    ? ScanRunner.ScanFileOnly(scriptPath, ctx)
                    : ScanRunner.RescanFile(scriptPath, _lastResult);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (_lastResult != null)
            {
                ScanResultStore.Save(_lastResult);
                RenderHeader(ListResult());
            }

            // Sync the UI controls (when already built): setting value triggers their respective callbacks → update state + RenderResults.
            if (_searchField != null) _searchField.value = scriptPath;
            SyncSearchPlaceholder();
            ShowEverySeverity();

            // Decide the empty-state story BEFORE rendering: did file-level analysis surface anything for this exact script?
            // If not, the runtime CPU hotspot is compute-bound (not allocation), and the empty state should say so rather than read like a dead-end.
            // (The _searchField callback above set _focusedScriptNoFindings = null; we set the real value here, then render.)
            bool scriptHasFindings = _lastResult != null && _lastResult.Findings.Any(f =>
                !string.IsNullOrEmpty(f.TargetPath) &&
                f.TargetPath.IndexOf(scriptPath, StringComparison.OrdinalIgnoreCase) >= 0);
            _focusedScriptNoFindings = (_lastResult != null && !scriptHasFindings) ? scriptPath : null;

            // Controls not ready, or the value above didn't change when set (no callback fired) → render once proactively.
            RenderResults();

            if (_lastResult == null)
                ShowNotification(new GUIContent(L.Tr("No file-level analyzer claims this script (not a runtime script?)", "该脚本无文件级行分析器认领（非运行时脚本？）")));
        }

        /// <summary>Switches to the static scan panel and narrows the report to the per-frame allocation rule family (PERF.GC* / PERF.UPD*).
        /// Entry point for runtime RUN.GC001's "Locate": runtime confirmed GC pressure → land directly on the allocation sites + AI Fix. Does NOT trigger a full scan
        /// (that's the user's "Scan Project" button); it filters whatever results exist (a restored last scan, typically).</summary>
        public void FocusOnScriptGcRules() =>
            FocusOnRuleFamily(L.Tr("Script GC / per-frame allocation", "脚本 GC / 每帧分配"), "PERF.GC", "PERF.UPD");

        /// <summary>
        /// Narrows the list to a FAMILY of rules — the shape a runtime finding hands off in.
        ///
        /// A measurement rarely maps to one rule: "2508 draw calls per frame" is answered by the batching and
        /// instancing findings together, and "allocating every frame" by the script GC ones. This was written once
        /// for allocation and then wanted again the moment the draw-call card needed somewhere to go, so it is a
        /// parameter now instead of a second copy.
        /// </summary>
        public void FocusOnRuleFamily(string label, params string[] prefixes)
        {
            if (prefixes == null || prefixes.Length == 0) return;

            _search = string.Empty;
            _focusedScriptNoFindings = null;
            if (_searchField != null) _searchField.value = string.Empty; // empty value won't clear _ruleFocus (callback only clears on a non-empty query)
            SyncSearchPlaceholder();
            ShowEverySeverity();   // families straddle severities — GC003/GC004 are Info, and Warning may be off project-wide
            // Set the focus AFTER syncing controls (the field callback above runs first; it leaves _ruleFocus untouched for an empty value).
            var captured = (string[])prefixes.Clone();
            _ruleFocus = f =>
            {
                if (f.RuleId == null) return false;
                foreach (var p in captured)
                    if (f.RuleId.StartsWith(p, StringComparison.Ordinal)) return true;
                return false;
            };
            _ruleFocusLabel = label;
            RenderResults();

            if (_lastResult == null)
                ShowNotification(new GUIContent(L.Tr("Click \"Scan Project\" first — the matching findings will then show here", "请先点「Scan Project」扫描——相关结论会显示在这里")));
        }

        /// <summary>Empty state for the RUN.GC001 → allocation-rules jump when the scan surfaced no PERF.GC*/UPD* findings:
        /// runtime confirms GC pressure but static syntax analysis found no allocation site, so point at the sources it can't see.</summary>
        private VisualElement BuildNoAllocationFindingsHelp()
        {
            var box = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            box.style.marginTop = 8;
            bool scanned = _lastResult != null;
            box.Add(new Label(scanned
                ? L.Tr("No per-frame allocation patterns (PERF.GC* / PERF.UPD*) found in your scripts.", "你的脚本里未发现每帧分配类模式（PERF.GC* / PERF.UPD*）。")
                : L.Tr("Run \"Scan Project\" first to surface per-frame allocation findings.", "请先点「Scan Project」扫描，才能列出每帧分配类问题。"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal } });
            if (scanned)
                box.Add(new Label(
                    L.Tr("Runtime confirmed GC pressure, but the static syntax analysis matched no allocation site. The allocations likely come from sources it can't see: value-type boxing, allocations inside third-party packages or engine callbacks, closures/lambdas captured per call, or collection growth not in an Update-family method. Record a GC.Alloc sample in the Unity Profiler (CPU module → \"GC Alloc\" column, or Memory Profiler) to pinpoint the exact call.",
                         "运行时确认了 GC 压力，但静态语法分析没匹配到分配点。分配大概率来自它看不到的地方：值类型装箱、第三方包或引擎回调内部的分配、每次调用捕获的闭包/lambda、或不在 Update 系方法里的集合增长。用 Unity Profiler 录一段 GC.Alloc（CPU 模块的「GC Alloc」列，或 Memory Profiler）来定位到具体调用。"))
                { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, marginTop = 4, fontSize = 11 } });
            return box;
        }

        /// <summary>Empty state shown when a runtime CPU-hotspot "Line-level analysis" jump finds no static issues in the script:
        /// explains the hotspot is compute-bound (not allocation) and what to do, instead of a bare "no matches" that reads like a dead-end.</summary>
        /// <summary>Whether the search box holds something shaped like a runtime rule id (RUN.GC001, run.hot001, …).</summary>
        private static bool LooksLikeRuntimeRuleId(string search) =>
            !string.IsNullOrEmpty(search) && search.TrimStart().StartsWith("RUN.", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Empty state for "you searched for a runtime rule in the static panel". Names where that rule actually
        /// lives and opens it, instead of a bare "no matches" that reads like the rule found nothing.
        /// </summary>
        private VisualElement BuildRuntimeRuleElsewhereHelp(string search)
        {
            var box = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            box.style.marginTop = 8;
            box.Add(new Label(L.Tr($"\"{search.Trim()}\" is a runtime rule — it is not part of this scan.",
                                   $"「{search.Trim()}」是运行时规则，不属于这次扫描的结果。"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal } });
            box.Add(new Label(L.Tr(
                "This panel holds the static scan: rules read from your files without running the game (PERF.*, MAT*, TEX*). RUN.* conclusions come from a Play Mode measurement and live in the Runtime Profiler — and the ones worth acting on first are ranked in the Autopilot.",
                "本面板装的是静态扫描：不运行游戏、只读文件得出的规则（PERF.*、MAT*、TEX* 等）。RUN.* 结论来自一次 Play Mode 实测，住在「运行时」面板里——其中最值得先动手的那些，会排在 Autopilot 里。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, marginTop = 4, fontSize = 11 } });

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6, flexWrap = Wrap.Wrap } };
            row.Add(new Button(() => PerfLintRuntimeWindow.Open())
            { text = L.Tr("Open the Runtime Profiler", "打开运行时面板") });
            row.Add(new Button(() => { _searchField.value = ""; SyncSearchPlaceholder(); })
            { text = L.Tr("Clear this search", "清除该筛选"), style = { marginLeft = 4 } });
            box.Add(PerfLintStyle.CompactActions(row));
            return box;
        }

        private static VisualElement BuildComputeBoundHotspotHelp()
        {
            var box = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            box.style.marginTop = 8;
            box.Add(new Label(L.Tr("No allocation / anti-pattern issues found in this script.", "此脚本未发现分配 / 反模式类问题。"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal } });
            box.Add(new Label(
                L.Tr("PerfLint's line-level analysis flags per-frame allocation (GC) and known anti-patterns — it found none here. So this CPU hotspot is most likely **compute-bound**: heavy per-frame work (loops, math, logic) that allocation analysis can't surface and AI Fix can't auto-patch.",
                     "PerfLint 的逐行分析查的是每帧分配（GC）和已知反模式——这里一个都没有。所以这个 CPU 热点大概率是**计算密集型**：每帧干了很重的活（循环、数学、逻辑），分配分析看不到、AI Fix 也无法自动修。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, marginTop = 4, fontSize = 11 } });
            // Item 4 depends on whether Deep Profile is already on: if it is, the hotspot is already method-level — don't tell the user to enable
            // something they've already enabled (which is what they'd just done to get a method-level marker in the first place).
            bool deep = UnityEditorInternal.ProfilerDriver.deepProfiling;
            string item4 = deep
                ? L.Tr("4. Deep Profile is already on, so this hotspot is already pinned to the method. To go deeper, expand this method's call tree in the Unity Profiler's CPU \"Hierarchy\" view to see where the time goes. \"Explain\" can also reason about the method's logic for you.",
                       "4. 你已开启 Deep Profile，所以热点已定位到方法级。要再往下拆，可在 Unity Profiler 的 CPU「Hierarchy」视图展开此方法的调用树看耗时分布。「Explain」也能帮你分析这个方法的逻辑。")
                : L.Tr("4. For a sub-method breakdown, turn on the \"Deep Profile\" toggle at the top of the PerfLint Runtime panel and re-sample. \"Explain\" can also reason about the method's logic for you.",
                       "4. 想看子方法级拆分，用 PerfLint Runtime 面板顶部的「Deep Profile」开关开启后重采样。「Explain」也能帮你分析这个方法的逻辑。");
            box.Add(new Label(
                L.Tr("How to optimize a compute-bound hotspot:\n1. Do less per frame — throttle, spread work across frames (coroutine/job), or make it event-driven instead of polling in Update;\n2. Cache and reuse results that don't change every frame;\n3. Reduce scale/precision — fewer iterations, a coarser data structure, early-outs;\n",
                     "计算型热点怎么优化：\n1. 每帧少干活——加节流、把工作分摊到多帧（协程/Job），或改成事件驱动而非在 Update 里轮询；\n2. 缓存复用那些并非每帧都变的结果；\n3. 降低规模/精度——更少迭代、更粗的数据结构、提前退出；\n") + item4)
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, marginTop = 4, fontSize = 11 } });
            return box;
        }

        private static VisualElement MakeDivider() => new VisualElement
        {
            style = { height = 1, backgroundColor = PerfLintStyle.Hair, marginTop = 4, marginBottom = 4 }
        };


        /// <summary>A rounded "pill" badge (severity-count chip): colored text + a faint same-hue fill and border.</summary>
        private static Label MakePill(Color c)
        {
            var l = new Label
            {
                style =
                {
                    paddingLeft = 9, paddingRight = 9, paddingTop = 2, paddingBottom = 2,
                    marginRight = 6, marginTop = 2, marginBottom = 2,
                    fontSize = 11, color = c, flexShrink = 0,
                    backgroundColor = new Color(c.r, c.g, c.b, 0.12f),
                    borderTopLeftRadius = 11, borderTopRightRadius = 11,
                    borderBottomLeftRadius = 11, borderBottomRightRadius = 11,
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                }
            };
            PerfLintStyle.SetBorderColor(l, new Color(c.r, c.g, c.b, 0.55f));
            return l;
        }

        // Neutral grey for a zero-count pill: "0 Critical" is a good outcome, not an alarm — don't paint it red.
        private static Color PillZeroColor => PerfLintStyle.Dimmer;

        /// <summary>Set a pill's text and recolor it: its severity hue when the count is non-zero, a muted grey when it's zero.</summary>
        private static void StylePill(Label pill, int count, string label, Color activeColor)
        {
            bool zero = count == 0;
            Color c = zero ? PillZeroColor : activeColor;
            pill.text = $"{count} {label}";
            pill.style.color = c;
            pill.style.backgroundColor = new Color(c.r, c.g, c.b, zero ? 0.05f : 0.12f);
            PerfLintStyle.SetBorderColor(pill, new Color(c.r, c.g, c.b, zero ? 0.28f : 0.55f));
        }

        // One severity palette for the product, and it follows the skin. This was the third copy of it, and the
        // three had drifted: Critical was (0.93,0.30,0.30) here and (0.90,0.35,0.33) in the shared card.
        private static Color SeverityColor(Severity s) => PerfLintStyle.SeverityColor(s);

    }
}
