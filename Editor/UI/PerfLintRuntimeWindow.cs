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
        private Label _stateLabel;
        private Label _liveLabel;
        private ScrollView _results;
        private RuntimeProfileResult _lastResult;
        private List<Finding> _lastFindings;
        private IVisualElementScheduledItem _poll;

        [MenuItem("Tools/PerfLint/Runtime Profiler %#k")] // Ctrl/Cmd + Shift + K
        public static void Open()
        {
            var win = GetWindow<PerfLintRuntimeWindow>();
            win.titleContent = new GUIContent("PerfLint Runtime");
            win.minSize = new Vector2(460, 380);
            win.Show();
        }

        private void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

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
            RefreshState();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;

            // ── Toolbar ─────────────────────────────
            var toolbar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 }
            };
            _toggleButton = new Button(ToggleSampling) { text = L.Tr("Start Sampling", "开始采样") };
            _toggleButton.style.height = 26;
            _toggleButton.style.flexGrow = 1;
            toolbar.Add(_toggleButton);

            var openMain = new Button(PerfLintWindow.Open) { text = L.Tr("Static Scan Panel", "静态扫描面板") };
            openMain.style.height = 26;
            openMain.style.marginLeft = 6;
            toolbar.Add(openMain);

            // Dev-only EN/中 switch (no-op in release — see L.InjectDevLangSwitch). CreateGUI appends without
            // clearing, so a flip wipes root before rebuilding to avoid stacking a second copy of the panel.
            L.InjectDevLangSwitch(toolbar, () => { root.Clear(); CreateGUI(); });
            root.Add(toolbar);

            _stateLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            root.Add(_stateLabel);

            // ── Live readout ───────────────────────────
            _liveLabel = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal, marginBottom = 4,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    display = DisplayStyle.None
                }
            };
            root.Add(_liveLabel);

            root.Add(MakeDivider());

            _results = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            root.Add(_results);

            root.Add(new Label(L.Tr("Runtime sampling runs locally and is never uploaded · Explain sends only finding metadata · AI Fix sends only ~48 lines around the flagged code", "运行时采样在本机完成、永不上传 · Explain 仅发 finding 元数据 · AI Fix 仅发标记代码附近约 48 行"))
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.6f, marginTop = 6, fontSize = 10 }
            });

            RefreshState();
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
                onComplete: (hotspots, worstFrame, gpuFrameTimeNs, ok) =>
                {
                    _lastResult   = _lastResult.WithHotspots(hotspots, ok, worstFrame, gpuFrameTimeNs);
                    _lastFindings = RuntimeAnalyzer.Analyze(_lastResult);
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

        private void RefreshState()
        {
            if (_toggleButton == null) return;

            bool playing = EditorApplication.isPlaying;
            bool sampling = _sampler.IsRunning;

            _toggleButton.text = sampling ? L.Tr("Stop & Analyze", "停止并分析") : L.Tr("Start Sampling", "开始采样");

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

            // Render in descending order of severity.
            var ordered = _lastFindings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal);

            foreach (var f in ordered)
                _results.Add(MakeFindingCard(f));

            AppendSummary();
        }

        private void AppendSummary()
        {
            if (_lastResult == null) return;

            var box = new VisualElement
            {
                style =
                {
                    marginTop = 10, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.04f)
                }
            };
            box.Add(new Label(L.Tr("Raw sampling readout (average)", "采样原始读数（平均）")) { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 2 } });
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
                box.Add(new Label(sceneText) { style = { fontSize = 11, opacity = 0.85f } });
            }

            _results.Add(box);
        }

        private static Label RawLine(string name, MetricStats m, Func<double, string> fmt)
        {
            string val = (m != null && m.HasData) ? fmt(m.Avg) : "—";
            return new Label(L.Tr($"  {name}: {val}", $"  {name}：{val}")) { style = { fontSize = 11, opacity = 0.85f } };
        }

        private VisualElement MakeFindingCard(Finding f)
        {
            // Outer column container: card body + on-demand expandable AI sub-panels.
            var col = new VisualElement { style = { marginTop = 4 } };

            var card = new VisualElement
            {
                style =
                {
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.03f),
                    borderLeftWidth = 3, borderLeftColor = SeverityColor(f.Severity)
                }
            };

            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            titleRow.Add(new Label("●") { style = { color = SeverityColor(f.Severity), marginRight = 6, minWidth = 12 } });
            titleRow.Add(new Label($"{f.RuleId} · {f.Title}")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, flexGrow = 1, whiteSpace = WhiteSpace.Normal }
            });

            if (f.Ping != null)
            {
                var locate = new Button(() => f.Ping()) { text = "Locate" };
                locate.style.marginLeft = 4;
                titleRow.Add(locate);
            }
            // Map hotspot to script: run line-level GC/Roslyn analysis + AI Fix in the static panel (runtime → static fix chain).
            if (!string.IsNullOrEmpty(f.CodeFile))
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

            card.Add(titleRow);

            if (!string.IsNullOrEmpty(f.Detail))
                card.Add(new Label(f.Detail) { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, marginTop = 2, fontSize = 11 } });

            col.Add(card);
            return col;
        }

        private VisualElement BuildAiFixPanel(Finding f)
        {
            string provider = LlmSettings.ProviderDisplayName;
            int n = ScriptFixService.WindowLineCount(f);

            var box = new VisualElement
            {
                style =
                {
                    marginTop = 2, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.04f),
                    borderLeftWidth = 2, borderLeftColor = new Color(0.95f, 0.70f, 0.20f)
                }
            };

            var status = new Label(L.Tr($"AI Fix will send ~{n} lines around the flagged code to {provider} (only this snippet, not the whole file/project).", $"AI 修复会把被标记代码附近约 {n} 行发送给 {provider}（仅这一段，不发整文件/项目）。"))
            {
                style = { whiteSpace = WhiteSpace.Normal }
            };
            box.Add(status);

            var diffArea = new VisualElement();

            var gen = new Button { text = L.Tr($"Generate fix (send ~{n} lines to {provider})", $"生成修复（发送约 {n} 行给 {provider}）") };
            gen.style.marginTop = 4;
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

            var box = new VisualElement
            {
                style =
                {
                    marginTop = 2, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.04f),
                    borderLeftWidth = 2, borderLeftColor = new Color(0.45f, 0.65f, 0.95f)
                }
            };

            var output = new TextField { multiline = true, isReadOnly = true };
            output.style.whiteSpace = WhiteSpace.Normal;
            box.Add(output);

            var inputRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6, display = DisplayStyle.None }
            };
            var field = new TextField { style = { flexGrow = 1 } };
            var askBtn = new Button { text = L.Tr("Ask follow-up", "追问") };
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
                var apply = new Button { text = L.Tr("Apply fix (writes to file; commit to version control first)", "应用修复（写入文件，建议先提交版本控制）") };
                apply.style.marginTop = 6;
                apply.clicked += () =>
                {
                    bool ok = ScriptFixService.Apply(p, out string msg);
                    if (ok)
                    {
                        ShowNotification(new GUIContent(L.Tr("AI fix applied", "AI 修复已应用")));
                        area.Clear();
                        area.Add(new Label("✓ " + msg) { style = { color = new Color(0.45f, 0.80f, 0.50f) } });
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(L.Tr("Apply failed", "应用失败"), msg, "OK");
                    }
                };
                area.Add(apply);
            }
        }

        /// <summary>Opens the static scan panel and focuses on this script: automatically filters to all line-level findings for this script (GC001/GC004 etc.) + AI Fix.</summary>
        private static void OpenScriptInMainPanel(string scriptPath)
        {
            // First open the script in the IDE/editor to locate it, then open the main panel and focus the report on this script (line-level GC analysis + AI Fix live there).
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                AssetDatabase.OpenAsset(obj);
            }
            var win = PerfLintWindow.OpenWindow();
            win.FocusOnScript(scriptPath);
        }

        private static VisualElement MakeDivider() => new VisualElement
        {
            style = { height = 1, backgroundColor = new Color(1, 1, 1, 0.1f), marginTop = 4, marginBottom = 4 }
        };

        private static Color SeverityColor(Severity s) => s switch
        {
            Severity.Critical => new Color(0.93f, 0.30f, 0.30f),
            Severity.Warning => new Color(0.95f, 0.70f, 0.20f),
            _ => new Color(0.45f, 0.65f, 0.95f)
        };

        private static string Human(double bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes:0} B";
        }
    }
}
