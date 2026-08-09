using System;
using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Licensing;
using PerfLint.Llm;
using PerfLint.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// Runtime (Play Mode) performance analysis panel. Unlike the main panel's "static scan gives a report only",
    /// this samples Profiler data while the game is actually running to pinpoint bottlenecks that only surface at
    /// runtime (stutter / per-frame GC / memory growth / render overhead / CPU hotspots), maps hotspots to specific
    /// scripts, and guides the user to the main panel's line-level analysis + AI Fix.
    ///
    /// Privacy same as the main panel: all data is collected locally inside the Unity process and never uploaded.
    /// Diagnosis is free forever; fix entry points (AI Fix etc. in the main panel) reuse the existing Pro gating.
    /// </summary>
    public sealed class PerfLintRuntimeWindow : EditorWindow
    {
        private readonly RuntimeSampler _sampler = new RuntimeSampler();

        private Button _toggleButton;
        private Button _deepProfileButton;
        private Label _stateLabel;
        private Label _liveLabel;
        private ScrollView _results;
        private RuntimeProfileResult _lastResult;
        private List<Finding> _lastFindings;
        // Files whose LAST static scan has a line-level allocation/GC finding (PERF.GC* / PERF.UPD*). The runtime "Line-level analysis" button only appears
        // for a finding whose script is in this set — otherwise the jump would land on the static panel and surface unrelated findings (e.g. a Debug.Log) or
        // an empty view. Rebuilt from the persisted scan at each RenderResults.
        private HashSet<string> _gcRelevantFiles;
        private IVisualElementScheduledItem _poll;

        [MenuItem("Tools/PerfLint/Runtime Profiler %#k")] // Ctrl/Cmd + Shift + K
        public static void Open()
        {
            var win = GetWindow<PerfLintRuntimeWindow>();
            win.titleContent = new GUIContent("PerfLint Runtime");
            win.minSize = new Vector2(460, 380);
            win.Show();
            // Restore here too, not only in CreateGUI. CreateGUI runs once per window INSTANCE and an instance
            // survives a domain reload with its visual tree intact, so a window that was already open never re-runs
            // it — which is exactly the state anyone who has used this window before is in. This is the entry point
            // the Autopilot sends people to, so it is the one that has to guarantee there is something here.
            win.RestoreLastSession();
        }

        private void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

        /// <summary>
        /// Restores the last session when the window is brought up, not only when its GUI is first built.
        ///
        /// CreateGUI runs once per window INSTANCE, and an instance survives a domain reload with its visual tree
        /// intact — so a window that was already open when the package changed never re-runs it. Caught exactly that
        /// way: the restore worked when invoked directly and did nothing through the UI, because the only window on
        /// screen predated it. OnFocus is also the path a reader takes here, since the Autopilot button that sends
        /// them opens and focuses this window. RestoreLastSession is a no-op once anything is loaded.
        /// </summary>
        private void OnFocus()
        {
            if (_results != null) RestoreLastSession();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            _sampler.CancelHotspots();
            if (_sampler.IsRunning) _sampler.Dispose();
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            // When exiting Play Mode while still sampling, automatically stop and analyze the data collected so far.
            if (change == PlayModeStateChange.ExitingPlayMode && _sampler.IsRunning)
                StopSampling();
            RestoreLastSession();
            RefreshState();
        }

        /// <summary>
        /// Brings back the last sampling session from disk, so this window still has its results after Play Mode.
        ///
        /// This window SAVED the session and never LOADED it: _lastFindings is an ordinary field, and leaving Play
        /// Mode reloads the domain, which wipes it. So the one window that owns runtime results was the only one that
        /// could not show them afterwards — while the main panel, which restores the same session, ended up holding
        /// all the runtime content. Tim found it from the outside: "the real runtime panel has no content".
        ///
        /// It is the same defect the main panel already fixed ("Runtime sampling used to live and die inside its own
        /// panel"); only half of it was fixed, on the other side.
        ///
        /// The raw counter readout is not restored, because it needs a full RuntimeProfileResult and the stored
        /// session keeps summary metrics rather than the object. The findings ARE the content, and the banner says
        /// what this is so a restored session is never mistaken for one just taken.
        /// </summary>
        internal void RestoreLastSession()
        {
            var session = RuntimeSessionStore.Load();
            if (session == null || session.Findings.Count == 0) return;

            // Adopt it when it is a DIFFERENT measurement from the one on screen, not merely when the screen is
            // empty. The first version returned early on "we already have something", which meant the window kept
            // whatever it restored first — so after switching scene and sampling again it went on showing the
            // previous scene's results while every other surface had moved on. Tim caught it with the Autopilot
            // holding a Cockpit measurement and this panel still listing Garden's roof tiles.
            //
            // The timestamp is the identity: a sample taken IN this window saved the very session being compared
            // here, so its own results are recognised and never clobbered by a reload of themselves.
            if (_lastFindings != null && _restoredSession != null &&
                _restoredSession.CapturedAtUtc == session.CapturedAtUtc) return;

            _lastFindings = new List<Finding>(session.Findings);
            _restoredSession = session;
            _lastResult = null;   // the raw readout belongs to the sample that produced it, not to this one
            RenderResults();
        }

        /// <summary>Set when the findings on screen came back from disk rather than from a sample taken just now.</summary>
        private RuntimeSessionStore.Session _restoredSession;

        private void CreateGUI()
        {
            var root = rootVisualElement;
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;

            // ── Toolbar ─────────────────────────────
            var toolbar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 }
            };
            // The one thing this window is for. Which of the two primaries it wears — the ordinary one or the
            // "running, click to stop" one — is decided per state in RefreshState, and the two share a box so the
            // button does not move a pixel when it flips.
            _toggleButton = PerfLintStyle.Primary(L.Tr("Start Sampling", "开始采样"), ToggleSampling);
            _toggleButton.style.flexGrow = 1;
            toolbar.Add(_toggleButton);

            var openMain = PerfLintStyle.Toolbar(L.Tr("Static Scan Panel", "静态扫描面板"), PerfLintWindow.Open);
            openMain.style.marginLeft = 6;
            toolbar.Add(openMain);

            // One-click Deep Profile toggle — mirrors the Unity Profiler's own toggle so users can refine CPU hotspots to
            // method level (ClassName.Method) without leaving this panel. State reflects ProfilerDriver.deepProfiling; takes
            // effect on the next Play Mode sample (instrumentation is set up when entering Play Mode).
            _deepProfileButton = PerfLintStyle.AsToolbar(new Button(ToggleDeepProfile));
            _deepProfileButton.style.marginLeft = 6;
            toolbar.Add(_deepProfileButton);

            // Dev-only inline shortcut for Tools ▸ PerfLint ▸ Language (no-op in release — see L.InjectDevLangSwitch).
            // CreateGUI appends without clearing, so a flip wipes root before rebuilding to avoid stacking a second
            // copy of the panel.
            L.InjectDevLangSwitch(toolbar, () => { root.Clear(); CreateGUI(); });
            PerfLintStyle.ToolbarButtons(toolbar);
            root.Add(toolbar);

            // ── Status card (the shared card, same as every other panel's) ──
            var headerCard = PerfLintStyle.Card();

            _stateLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Dim } };
            headerCard.Add(_stateLabel);

            // Live readout while sampling (bold; hidden until sampling starts).
            _liveLabel = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal, marginTop = 6,
                    unityFontStyleAndWeight = FontStyle.Bold, color = PerfLintStyle.Ink,
                    display = DisplayStyle.None
                }
            };
            headerCard.Add(_liveLabel);
            root.Add(headerCard);

            // Mipmap Streaming tuning deck (collapsed by default): live "saving ~X MB" readout + the streaming
            // parameters SRP gives you no debug view for. Companion to the static PERF.TEXSTR001 advisor.
            root.Add(TextureStreamingSection.Build());

            _results = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            root.Add(_results);

            root.Add(new Label(L.Tr("Runtime sampling runs locally and is never uploaded · Explain sends only finding metadata · AI Fix sends only ~48 lines around the flagged code", "运行时采样在本机完成、永不上传 · Explain 仅发 finding 元数据 · AI Fix 仅发标记代码附近约 48 行"))
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, marginTop = 6, fontSize = 10, color = PerfLintStyle.Dimmer }
            });

            RefreshState();

            // Draw whatever was restored while this window had no visual tree to draw into. Without it the guard
            // above would trade an exception for a blank panel: RestoreLastSession has already set _lastFindings and
            // will not do it twice, so nothing else would ever render them.
            RenderResults();
        }

        private void ToggleSampling()
        {
            if (_sampler.IsRunning) StopSampling();
            else StartSampling();
        }

        private void StartSampling()
        {
            if (!EditorApplication.isPlaying)
            {
                bool enter = EditorUtility.DisplayDialog(
                    L.Tr("PerfLint Runtime Analysis", "PerfLint 运行时分析"),
                    L.Tr("Runtime analysis requires sampling in Play Mode. Enter Play Mode now?\n\n", "运行时分析需要在 Play Mode 下采样。现在进入 Play Mode 吗？\n\n") +
                    L.Tr("Once in, come back to this window and click \"Start Sampling\".", "进入后回到本窗口点「开始采样」即可。"),
                    L.Tr("Enter Play Mode", "进入 Play Mode"), L.Tr("Cancel", "取消"));
                if (enter) EditorApplication.isPlaying = true;
                return;
            }

            _results.Clear();
            _lastResult = null;
            _lastFindings = null;
            _sampler.Start();

            _liveLabel.style.display = DisplayStyle.Flex;
            _poll = rootVisualElement.schedule.Execute(UpdateLiveReadout).Every(250);
            RefreshState();
        }

        private void StopSampling()
        {
            _poll?.Pause();
            _poll = null;
            _sampler.CancelHotspots(); // Cancel the previous unfinished merge (defensive)

            _lastResult  = _sampler.Stop();
            _lastFindings = null;
            _liveLabel.style.display = DisplayStyle.None;

            if (_lastResult == null) { RefreshState(); return; }

            // First phase done (counter layer): show GC/FPS/memory counter diagnostics first, with hotspots "merging".
            _toggleButton.SetEnabled(false);
            _stateLabel.text = L.Tr("Merging hotspot data… 0%", "正在归并热点数据… 0%");
            _results.Clear();

            _sampler.BeginHotspots(
                onComplete: (hotspots, worstFrames, gpuFrameTimeNs, ok) =>
                {
                    _lastResult   = _lastResult.WithHotspots(hotspots, ok, worstFrames, gpuFrameTimeNs, _sampler.LastGcSite,
                        _sampler.LastGameGcPerFrame, _sampler.LastGameFrameTime);
                    _lastFindings = RuntimeAnalyzer.Analyze(_lastResult);
                    // Persist immediately: leaving Play Mode reloads the domain and would otherwise destroy the
                    // measurement that this very session produced. On disk it can reach the main report, the health
                    // score, the exports and a later before/after comparison. Captured here, while the sampled
                    // scenes are still the loaded ones.
                    RuntimeSessionStore.Save(_lastResult, _lastFindings, null, _sampler?.StartScene);
                    // Remember WHICH session is on screen. Without this the next Open() sees a store entry it does
                    // not recognise and adopts it — throwing away the raw readout of the sample just taken.
                    _restoredSession = RuntimeSessionStore.Load();
                    _toggleButton.SetEnabled(true);
                    RefreshState();
                    RenderResults();
                },
                onProgress: (done, total) =>
                {
                    if (_stateLabel == null) return;
                    int pct = total > 0 ? (int)(100.0 * done / total) : 100;
                    _stateLabel.text = L.Tr($"Merging hotspot data… {pct}% ({done}/{total} frames)", $"正在归并热点数据… {pct}%（{done}/{total} 帧）");
                });
        }

        private void UpdateLiveReadout()
        {
            if (!_sampler.IsRunning) return;

            double frameMs = _sampler.LastValue("Main Thread") / 1_000_000.0;
            double fps = frameMs > 0 ? 1000.0 / frameMs : 0;
            double gc = _sampler.LastValue("GC Allocated In Frame");
            double mem = _sampler.LastValue("Total Used Memory");
            double draw = _sampler.LastValue("Draw Calls Count");
            double setpass = _sampler.LastValue("SetPass Calls Count");

            _liveLabel.text = L.Tr(
                $"Sampling  {_sampler.CurrentDurationSeconds:0.0}s   ·   " +
                $"{fps:0} FPS ({frameMs:0.0} ms)   ·   GC {Human(gc)}/frame   ·   " +
                $"Memory {Human(mem)}   ·   Draw {draw:0}   ·   SetPass {setpass:0}",
                $"采样中  {_sampler.CurrentDurationSeconds:0.0}s   ·   " +
                $"{fps:0} FPS ({frameMs:0.0} ms)   ·   GC {Human(gc)}/帧   ·   " +
                $"内存 {Human(mem)}   ·   Draw {draw:0}   ·   SetPass {setpass:0}");
        }

        /// <summary>Reflects the current Deep Profile state on the toggle button (label + color + tooltip).</summary>
        private void RefreshDeepProfileButton()
        {
            if (_deepProfileButton == null) return;
            bool on = UnityEditorInternal.ProfilerDriver.deepProfiling;
            _deepProfileButton.text = on ? L.Tr("Deep Profile ●", "Deep Profile ●") : L.Tr("Deep Profile", "Deep Profile");
            // On: the good green, saying it is active. Off: cleared to Null rather than to a colour, so the label
            // falls back to whatever .pl-secondary says — setting it inline is what would kill the hover.
            _deepProfileButton.style.color = on ? new StyleColor(PerfLintStyle.Good) : new StyleColor(StyleKeyword.Null);
            _deepProfileButton.tooltip = on
                ? L.Tr("Deep Profile is ON: CPU hotspots refine to specific script methods (ClassName.Method), but it has high overhead — use it for localization, not for measuring real frame rate. Click to turn off.", "Deep Profile 已开启：CPU 热点会细化到具体脚本方法（ClassName.Method），但开销很大——仅用于定位、勿用于测真实帧率。点击关闭。")
                : L.Tr("Turn on Deep Profile to refine CPU hotspots to specific script methods (ClassName.Method). High overhead — for localization only. Takes effect on the next Play Mode sample.", "开启 Deep Profile 可把 CPU 热点细化到具体脚本方法（ClassName.Method）。开销很大、仅用于定位。在下次 Play Mode 采样时生效。");
        }

        /// <summary>One-click Deep Profile toggle. Mirrors Unity's own Profiler "Deep Profile" button: setting
        /// ProfilerDriver.deepProfiling alone is NOT enough — the deep-profiling instrumentation is (un)injected during a
        /// script/domain reload, so we must RequestScriptReload() for it to actually apply. Without the reload the flag flips
        /// but sampling still produces coarse markers (BehaviourUpdate) instead of ClassName.Method().
        /// A reload can't happen during Play Mode without leaving it, so in Play Mode we confirm first (it will exit Play Mode).</summary>
        private void ToggleDeepProfile()
        {
            bool now = !DeepProfileControl.Enabled;
            bool wasPlaying = EditorApplication.isPlaying;

            // The switch, its Play-Mode confirmation and the reload all live in DeepProfileControl, because the
            // Autopilot offers the same action from a finding that names it.
            if (!DeepProfileControl.Set(now)) return;
            RefreshDeepProfileButton();

            if (!wasPlaying)
                ShowNotification(new GUIContent(now
                    ? L.Tr("Deep Profile on — enter Play Mode and sample", "Deep Profile 已开 — 进入 Play Mode 采样")
                    : L.Tr("Deep Profile turned off", "已关闭 Deep Profile")));
        }

        private void RefreshState()
        {
            if (_toggleButton == null) return;

            RefreshDeepProfileButton();

            bool playing = EditorApplication.isPlaying;
            bool sampling = _sampler.IsRunning;

            _toggleButton.text = sampling ? L.Tr("Stop & Analyze", "停止并分析") : L.Tr("Start Sampling", "开始采样");
            // The product's primary to start; the stop colour while recording. Both are classes rather than an
            // inline fill — an inline background outranks the stylesheet, which is why this button had no hover
            // response at all before, in either state.
            if (sampling) PerfLintStyle.AsDanger(_toggleButton);
            else PerfLintStyle.AsPrimary(_toggleButton);

            if (sampling)
                _stateLabel.text = L.Tr("Sampling runtime data… drive the game into the scene/action you want to diagnose, then click \"Stop & Analyze\".", "正在采样运行时数据……让游戏进入要诊断的场景/操作，然后点「停止并分析」。");
            else if (!playing)
                _stateLabel.text = L.Tr("Not in Play Mode. Clicking \"Start Sampling\" will prompt you to enter Play Mode; once in, click again to start sampling.", "未在 Play Mode。点「开始采样」会提示进入 Play Mode；进入后再次点击开始采样。");
            else if (_lastResult != null)
                _stateLabel.text = L.Tr($"Last sampling: {_lastResult.DurationSeconds:0.0}s · {_lastResult.FrameCount} frames · avg {_lastResult.AverageFps:0} FPS. You can \"Start Sampling\" again.", $"上次采样：{_lastResult.DurationSeconds:0.0}s · {_lastResult.FrameCount} 帧 · " +
                                   $"平均 {_lastResult.AverageFps:0} FPS。可再次「开始采样」。");
            else
                _stateLabel.text = L.Tr("Already in Play Mode. Click \"Start Sampling\" to begin recording, then click \"Stop & Analyze\" once you're in the scene to diagnose.", "已在 Play Mode。点「开始采样」开始记录，进入要诊断的场景后点「停止并分析」。");
        }

        private void RenderResults()
        {
            // The window object outlives a domain reload; its visual tree does not, and CreateGUI is not called back
            // until the tab is next SHOWN. OnPlayModeChanged fires regardless, so leaving Play Mode with this window
            // sitting in a hidden tab reached here with _results still null — a NullReferenceException on every
            // measurement, twice per Play Mode round-trip, which also aborted the RefreshState() call after it.
            // RefreshState already guards itself the same way; this path had never been given the same treatment.
            if (_results == null) return;

            _results.Clear();
            if (_lastFindings == null) return;

            if (_lastFindings.Count == 0)
            {
                _results.Add(new Label(L.Tr("No obvious runtime issues found in this sampling. Try sampling again under a more complex scene/action.", "本次采样未发现明显运行时问题。可在更复杂的场景/操作下再采样一段。"))
                {
                    style = { marginTop = 8, whiteSpace = WhiteSpace.Normal }
                });
                AppendSummary();
                return;
            }

            // Shown for any restored session, and the banner itself decides how loud to be about the scene.
            if (_restoredSession != null && _lastResult == null) _results.Add(RestoredBanner(_restoredSession));

            _gcRelevantFiles = LoadGcRelevantFiles(); // gate the "Line-level analysis" button to files that actually have allocation/GC static findings

            // Render in descending order of severity.
            var ordered = _lastFindings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal);

            foreach (var f in ordered)
                _results.Add(MakeFindingCard(f));

            AppendSummary();
        }

        /// <summary>
        /// Says these results were measured earlier, WHERE, when, and what the sample was.
        ///
        /// The scene is the part that was missing and the part that matters most. A measurement describes the scene
        /// it was taken in; restoring one after the reader has opened a different scene and presenting it as this
        /// panel's content is how "the Runtime panel is still showing the last scene" happens. Tim hit exactly that.
        /// The rest of the product already keeps such a session but never lets it move a number and says so; this is
        /// that rule, arriving in the window whose entire content IS the session.
        /// </summary>
        private static VisualElement RestoredBanner(RuntimeSessionStore.Session s)
        {
            double mins = (DateTime.UtcNow - s.CapturedAtUtc).TotalMinutes;
            string when = mins < 1 ? L.Tr("just now", "刚刚")
                        : mins < 60 ? L.Tr($"{mins:0} minutes ago", $"{mins:0} 分钟前")
                        : L.Tr($"{mins / 60:0} hours ago", $"{mins / 60:0} 小时前");
            string deep = s.WasDeepProfile
                ? L.Tr(" Deep Profile was on, so the millisecond figures are the profiler's own cost rather than your frame rate.",
                       " 当时开着 Deep Profile，因此毫秒数是分析器自身的开销，不是你的真实帧率。")
                : "";

            // DescribesScenes, not Applies. Applies also demands the session FOUND something, and conflating the two
            // here would label a clean measurement of the open scene as "from another scene" — the banner asks only
            // whether these results are about what is on screen. Caught by rendering both cases before believing it.
            bool applies = s.DescribesScenes(RuntimeSessionStore.ScenesInScope());
            // The running scene names the measurement; anything else was loaded around it and is said as context,
            // because "measured in TerminalScene, CockpitScene, OasisScene, GardenScene" reads as four measurements.
            string where = !string.IsNullOrEmpty(s.ActiveScene) ? s.ActiveScene
                         : s.Scenes != null && s.Scenes.Count > 0 ? string.Join(", ", s.Scenes)
                         : L.Tr("an unnamed scene", "未命名场景");
            var alsoList = s.AlsoLoaded();
            if (alsoList.Count > 0)
                where += L.Tr($" (with {string.Join(", ", alsoList)} also loaded)", $"（同时还加载了 {string.Join("、", alsoList)}）");

            // Two different things, so two different blocks: a session that describes the open scene is context
            // (accent), and one that describes somewhere else is a caveat you must read before believing anything
            // under it (warning). Same distinction the old code drew with a faint grey vs. an amber wash — now in
            // the shared vocabulary, so it matches the Autopilot's verdict blocks rather than resembling them.
            var box = PerfLintStyle.Note(applies ? PerfLintStyle.NoteAccent : PerfLintStyle.NoteWarning);
            box.style.marginTop = 6;
            box.style.marginBottom = 4;

            string text = applies
                ? L.Tr($"Measured {when} in {where} · {s.FrameCount} frames · {s.Hotspots.Count} call paths recorded.{deep} Sample again for the raw counter readout.",
                       $"测于{when}，场景 {where} · {s.FrameCount} 帧 · 记录了 {s.Hotspots.Count} 条调用路径。{deep} 重新采样可看到原始计数读数。")
                // Not a footnote: everything below it describes somewhere else, so it says that first and plainly.
                : L.Tr($"These results are from {where}, measured {when} — NOT the scene you have open. Nothing below describes what is on screen now. Sample this scene to replace them.",
                       $"以下结果来自 {where}，测于{when}——**不是**你当前打开的场景。下面的内容都不描述现在屏幕上的东西。对当前场景采样即可替换它们。");

            box.Add(new Label(text)
            { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11,
                        // Ink for the case that must be read (these results describe another scene), Dim for
                        // ordinary context. Not amber: the block is already amber, and hue-on-hue reads as washed out.
                        color = applies ? PerfLintStyle.Dim : PerfLintStyle.Ink } });

            // A stored session keeps the findings it was written with — loading never re-runs the analyzer, by
            // design, so after an update the wording, thresholds and advice on screen are still the old build's.
            // Said here because the alternative is what actually happened: PerfLint was changed, the panel reopened,
            // the old sentence was still there, and it read as the change not working. The banner above already says
            // WHEN this was measured; this says by WHAT.
            if (s.FromDifferentBuild)
                box.Add(new Label(L.Tr(
                    "These findings were written by a different build of PerfLint than the one running now. The measurement still stands — but the wording, thresholds and advice are the old build's, because a stored session keeps its findings rather than regenerating them. Sample again to produce them with this build.",
                    "这些结论是由**另一个版本**的 PerfLint 写下的。测量本身依然有效——但文案、阈值与建议都还是旧版本的，因为已保存的会话保留当时的结论、不会重新生成。重新采样即可用当前版本重新产出。"))
                { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 4,
                            color = PerfLintStyle.Dim } });
            return box;
        }

        private void AppendSummary()
        {
            if (_lastResult == null) return;

            // Supporting detail under the findings, so the recessed panel rather than another card — the same
            // treatment the Autopilot gives a block of figures.
            var box = PerfLintStyle.Panel();
            box.style.marginTop = 10;
            box.Add(new Label(L.Tr("Raw sampling readout (average)", "采样原始读数（平均）"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4, color = PerfLintStyle.Ink } });
            box.Add(RawLine(L.Tr("Frame time CPU", "帧时间 CPU"), _lastResult.FrameTimeNs, v => $"{v / 1_000_000.0:0.0} ms ({(v > 0 ? 1_000_000_000.0 / v : 0):0} FPS)"));
            box.Add(RawLine(L.Tr("Frame time GPU", "帧时间 GPU"), _lastResult.GpuFrameTimeNs, v => $"{v / 1_000_000.0:0.0} ms"));
            box.Add(RawLine(L.Tr("GC/frame", "GC/帧"), _lastResult.GcPerFrameBytes, v => $"{Human(v)}"));
            box.Add(RawLine(L.Tr("Memory Total", "内存 Total"), _lastResult.TotalMemoryBytes, v => $"{Human(v)}"));
            box.Add(RawLine(L.Tr("  ├ Managed heap", "  ├ 托管堆"), _lastResult.GcUsedBytes, v => $"{Human(v)}"));
            box.Add(RawLine(L.Tr("  └ Graphics", "  └ 图形资源"), _lastResult.GfxUsedBytes, v => $"{Human(v)}"));
            box.Add(RawLine("SetPass", _lastResult.SetPassCalls, v => $"{v:0}"));
            box.Add(RawLine("Draw Call", _lastResult.DrawCalls, v => $"{v:0}"));
            box.Add(RawLine("Batches", _lastResult.Batches, v => $"{v:0}"));
            box.Add(RawLine(L.Tr("Triangles", "三角面"), _lastResult.Triangles, v => $"{v:0}"));

            var sb = _lastResult.SceneBatching;
            if (sb != null && sb.HasData)
            {
                string sceneText = L.Tr($"  Scene: {sb.RendererCount} mesh Renderers · {sb.UniqueMaterialCount} materials", $"  场景：{sb.RendererCount} 网格 Renderer · {sb.UniqueMaterialCount} 材质");
                if (sb.InstancedMaterialRendererCount > 0)
                    sceneText += L.Tr($" · {sb.InstancedMaterialRendererCount} runtime material instances", $" · {sb.InstancedMaterialRendererCount} 个运行时材质实例化");
                box.Add(new Label(sceneText) { style = { fontSize = 11, color = PerfLintStyle.Dim } });
            }

            _results.Add(box);
        }

        private static Label RawLine(string name, MetricStats m, Func<double, string> fmt)
        {
            string val = (m != null && m.HasData) ? fmt(m.Avg) : "—";
            return new Label(L.Tr($"  {name}: {val}", $"  {name}：{val}")) { style = { fontSize = 11, color = PerfLintStyle.Dim } };
        }

        private VisualElement MakeFindingCard(Finding f)
        {
            // Outer column container: card body + on-demand expandable AI sub-panels.
            var col = new VisualElement { style = { marginTop = 6 } };

            // The shared card, washed in its severity — the same object the Autopilot lists a round with. This panel
            // used to build its own copy of it (a hand-picked fill and a 3 px stripe down the left edge), which is
            // how the two windows drifted into showing the same finding two different ways.
            var card = FindingCardUI.Card(f.Severity);
            card.style.marginTop = 0;

            // Wrap, or a card whose title is a sentence pushes its own buttons out of a docked window and clips them
            // away — the ScrollView is vertical-only, so they cannot be scrolled back into reach.
            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap } };
            titleRow.Add(FindingCardUI.Dot(f.Severity));
            titleRow.Add(new Label($"{f.RuleId} · {f.Title}")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, flexGrow = 1, flexShrink = 1, minWidth = 0,
                          whiteSpace = WhiteSpace.Normal, fontSize = 12, color = PerfLintStyle.Ink }
            });

            // GC001's fallback Locate (no runtime function attributed → jumps to the static Script GC panel) is only useful when that scan actually has
            // allocation findings. When it doesn't, suppress the button — the detail already guides to the scan panel / Unity Profiler instead of a dead-end.
            bool suppressLocate = f.RuleId == "RUN.GC001" && string.IsNullOrEmpty(f.TargetPath) &&
                                  (_gcRelevantFiles == null || _gcRelevantFiles.Count == 0);
            if (f.Ping != null && !suppressLocate)
            {
                var locate = new Button(() => f.Ping()) { text = "Locate" };
                locate.style.marginLeft = 4;
                titleRow.Add(locate);
            }
            // Map hotspot to script: run line-level GC/Roslyn analysis + AI Fix in the static panel (runtime → static fix chain). Only offered when the last
            // static scan actually found an allocation/GC line issue in that file — otherwise the jump would surface unrelated findings (e.g. a Debug.Log).
            if (!string.IsNullOrEmpty(f.CodeFile) && _gcRelevantFiles != null && _gcRelevantFiles.Contains(f.CodeFile.Replace('\\', '/')))
            {
                var analyze = new Button(() => OpenScriptInMainPanel(f.CodeFile)) { text = L.Tr("Line-level analysis", "逐行分析") };
                analyze.style.marginLeft = 4;
                titleRow.Add(analyze);
            }

            // Code-level AI fix (needs CodeFile + CodeLine; currently RUN.HOT001 has the path but no line number, so it doesn't trigger yet).
            if (f.AiFixable && LlmSettings.IsConfigured)
            {
                VisualElement aiFixPanel = null;
                var aifix = new Button { text = "AI Fix" };
                aifix.style.marginLeft = 4;
                aifix.clicked += () =>
                {
                    if (!Entitlements.RequireAiCredit("AI Fix")) return;
                    if (aiFixPanel == null) { aiFixPanel = BuildAiFixPanel(f); col.Add(aiFixPanel); }
                    else aiFixPanel.style.display = aiFixPanel.style.display == DisplayStyle.None
                        ? DisplayStyle.Flex : DisplayStyle.None;
                };
                titleRow.Add(aifix);
            }

            // The same "where do I look" the Autopilot offers, from the panel that produced the finding.
            //
            // These cards had Locate only when the finding carried its own Ping, so the frame-rate and stutter ones —
            // which are whole-frame measurements with no location — showed Explain and nothing else. Meanwhile the
            // Autopilot, reading the SAME session, could offer the script hotspot it had mapped. One window knowing
            // where to send you and the other not, about the same measurement, is the asymmetry Tim asked about.
            // Not when the finding already lists its own targets below (GPU002 draws one Locate per mesh — adding a
            // sixth button for the first of them is noise), and not when the destination is this very window.
            if (f.Ping == null && !FindingActions.LocationOf(f).HasPath
                && (f.LocateTargets == null || f.LocateTargets.Count == 0))
            {
                var next = FindingActions.WhereToLook(f, CurrentDiagnosis.Load());
                if (next.Exists && !next.OpensRuntimePanel)
                {
                    var go = new Button(next.Go) { text = next.Label, tooltip = next.Tooltip };
                    go.style.marginLeft = 4;
                    titleRow.Add(go);
                }
            }

            // AI Explain: sends only finding metadata (rule/description), no source code or assets — available for all findings.
            if (LlmSettings.IsConfigured)
            {
                VisualElement explainPanel = null;
                var explain = new Button { text = "Explain" };
                explain.style.marginLeft = 4;
                explain.clicked += () =>
                {
                    if (!Entitlements.RequireAiCredit(L.Tr("AI Explain", "AI 解释"))) return;
                    if (explainPanel == null) { explainPanel = BuildExplainPanel(f); col.Add(explainPanel); }
                    else explainPanel.style.display = explainPanel.style.display == DisplayStyle.None
                        ? DisplayStyle.Flex : DisplayStyle.None;
                };
                titleRow.Add(explain);
            }

            // One tier of button on a finding, decided here rather than per button. Six of them are added above under
            // six different conditions, and styling them one at a time is exactly how a row ends up with two looks.
            PerfLintStyle.CompactActions(titleRow);
            card.Add(titleRow);

            if (!string.IsNullOrEmpty(f.Detail))
                card.Add(new Label(f.Detail) { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2, fontSize = 11, color = PerfLintStyle.Dim } });

            // Per-target Locate rows (e.g. RUN.GPU002's Top-N meshes): each row names one target and has its own Locate button that reveals just that group.
            if (f.LocateTargets != null && f.LocateTargets.Count > 0)
            {
                foreach (var t in f.LocateTargets)
                {
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 3 } };
                    var textCol = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
                    textCol.Add(new Label(t.Label) { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, color = PerfLintStyle.Dim } });
                    // Indented by layout, not by spaces — UI Toolkit collapses leading whitespace in a wrapping Label.
                    if (!string.IsNullOrEmpty(t.Detail))
                        textCol.Add(new Label(t.Detail) { style = { whiteSpace = WhiteSpace.Normal, fontSize = 10, opacity = 0.7f, color = PerfLintStyle.Dim, marginLeft = 12, marginTop = 1 } });
                    row.Add(textCol);
                    var target = t; // capture for the closure
                    var locateOne = PerfLintStyle.AsCompact(new Button(() => target.Ping?.Invoke()) { text = "Locate" });
                    locateOne.style.marginLeft = 4;
                    locateOne.style.flexShrink = 0;
                    row.Add(locateOne);
                    card.Add(row);
                }
            }

            col.Add(card);
            return col;
        }

        private VisualElement BuildAiFixPanel(Finding f)
        {
            string provider = LlmSettings.ProviderDisplayName;
            int n = ScriptFixService.WindowLineCount(f);

            // Amber, and a whole block of it rather than a 2 px edge: this panel is about to send code somewhere, and
            // that is a caveat to read, not a decoration on the left.
            var box = PerfLintStyle.Note(PerfLintStyle.NoteWarning);
            box.style.marginTop = 2;

            var status = new Label(L.Tr($"AI Fix will send ~{n} lines around the flagged code to {provider} (only this snippet, not the whole file/project).", $"AI 修复会把被标记代码附近约 {n} 行发送给 {provider}（仅这一段，不发整文件/项目）。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Dim }
            };
            box.Add(status);

            var diffArea = new VisualElement();

            var gen = PerfLintStyle.AsSecondary(new Button { text = L.Tr($"Generate fix (send ~{n} lines to {provider})", $"生成修复（发送约 {n} 行给 {provider}）") });
            gen.style.marginTop = 4;
            gen.style.alignSelf = Align.FlexStart;
            gen.clicked += () =>
            {
                gen.SetEnabled(false);
                status.text = L.Tr("Generating…", "生成中…");
                diffArea.Clear();
                ScriptFixService.Propose(f, p =>
                {
                    gen.SetEnabled(true);
                    if (!p.Ok) { status.text = L.Tr("Failed: ", "失败：") + p.Error; return; }
                    if (p.NoChange) { status.text = L.Tr("AI determined no change is needed here — the original is already correct; this may be a false positive and can be ignored.", "AI 判断此处无需修改——原始写法已正确，可能是规则误报，可忽略。"); return; }
                    status.text = p.Locatable
                        ? L.Tr("Fix generated, please review the diff before applying:", "已生成修复，请审阅 diff 后应用：")
                        : L.Tr("Fix generated, but the original snippet couldn't be located precisely; please apply manually:", "已生成修复，但无法精确定位原始片段，请手动应用：");
                    RenderAiFixDiff(diffArea, p);
                });
            };
            box.Add(gen);
            box.Add(diffArea);
            return box;
        }

        private VisualElement BuildExplainPanel(Finding f)
        {
            var conv = new ExplainConversation(f);

            var box = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            box.style.marginTop = 2;

            var output = new TextField { multiline = true, isReadOnly = true };
            output.style.whiteSpace = WhiteSpace.Normal;
            box.Add(output);

            var inputRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6, display = DisplayStyle.None }
            };
            var field = new TextField { style = { flexGrow = 1 } };
            var askBtn = PerfLintStyle.AsCompact(new Button { text = L.Tr("Ask follow-up", "追问") });
            askBtn.style.marginLeft = 4;
            inputRow.Add(field);
            inputRow.Add(askBtn);
            box.Add(inputRow);

            string transcript = "";

            void Run(string follow)
            {
                if (!string.IsNullOrEmpty(follow)) transcript += L.Tr("\n\n— You: ", "\n\n— 你：") + follow;
                output.value = transcript.Length > 0 ? transcript + L.Tr("\n\n…thinking…", "\n\n…思考中…") : L.Tr("…thinking…", "…思考中…");
                askBtn.SetEnabled(false);
                conv.Ask(follow, r =>
                {
                    askBtn.SetEnabled(true);
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

            askBtn.clicked += () =>
            {
                string q = field.value;
                if (string.IsNullOrWhiteSpace(q)) return;
                field.value = "";
                Run(q);
            };

            Run(null);
            return box;
        }

        private void RenderAiFixDiff(VisualElement area, ScriptFixProposal p)
        {
            area.Clear();
            AiFixDiffView.BuildDiffBlocks(area, p); // Shares the same diff blocks as the main panel's single / batch review windows

            if (p.Locatable)
            {
                var apply = PerfLintStyle.AsSecondary(new Button { text = L.Tr("Apply fix (writes to file; commit to version control first)", "应用修复（写入文件，建议先提交版本控制）") });
                apply.style.marginTop = 6;
                apply.style.alignSelf = Align.FlexStart;
                apply.clicked += () =>
                {
                    bool ok = ScriptFixService.Apply(p, out string msg);
                    if (ok)
                    {
                        ShowNotification(new GUIContent(L.Tr("AI fix applied", "AI 修复已应用")));
                        area.Clear();
                        area.Add(new Label("✓ " + msg) { style = { color = PerfLintStyle.Good, whiteSpace = WhiteSpace.Normal } });
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(L.Tr("Apply failed", "应用失败"), msg, "OK");
                    }
                };
                area.Add(apply);
            }
        }

        /// <summary>Switches to the static scan panel and focuses the report on this script (file-level GC/Roslyn analysis + AI Fix live there).
        /// Deliberately does NOT open the .cs in the IDE — that's the separate "Locate" button's job (which lands on the hotspot method, not line 1).</summary>
        private static void OpenScriptInMainPanel(string scriptPath)
        {
            var win = PerfLintWindow.OpenWindow();
            win.FocusOnScript(scriptPath);
        }

        // Build the set of scripts that the LAST static scan flagged with a line-level allocation/GC finding (PERF.GC* / PERF.UPD* — the same family the
        // static "Script GC / per-frame allocation" view targets). Best-effort from the persisted scan; on any failure the set is empty and the button hides.
        private static HashSet<string> LoadGcRelevantFiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var restored = PerfLint.Core.ScanResultStore.Load();
                var findings = restored?.Result?.Findings;
                if (findings != null)
                    foreach (var f in findings)
                    {
                        if (!IsAllocationRule(f.RuleId)) continue;
                        string file = FileOfFinding(f);
                        if (!string.IsNullOrEmpty(file)) set.Add(file);
                    }
            }
            catch { /* best-effort — no button rather than a wrong one */ }
            return set;
        }

        private static bool IsAllocationRule(string ruleId) =>
            !string.IsNullOrEmpty(ruleId) &&
            (ruleId.StartsWith("PERF.GC", StringComparison.Ordinal) || ruleId.StartsWith("PERF.UPD", StringComparison.Ordinal));

        // A static finding's .cs file, from CodeFile or a "Assets/X.cs:line" TargetPath (report-style rules like PERF.GC004 carry the location in TargetPath, not CodeFile).
        private static string FileOfFinding(Finding f)
        {
            if (!string.IsNullOrEmpty(f.CodeFile)) return f.CodeFile.Replace('\\', '/');
            string tp = f.TargetPath;
            if (string.IsNullOrEmpty(tp)) return null;
            tp = tp.Replace('\\', '/');
            int colon = tp.LastIndexOf(':');
            if (colon > 1 && tp.Substring(0, colon).EndsWith(".cs", StringComparison.Ordinal)) return tp.Substring(0, colon);
            return tp.EndsWith(".cs", StringComparison.Ordinal) ? tp : null;
        }

        private static string Human(double bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes:0} B";
        }
    }
}
