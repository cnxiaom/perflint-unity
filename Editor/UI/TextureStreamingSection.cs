using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
#if !UNITY_2022_1_OR_NEWER
using UnityEditor.UIElements; // FloatField/IntegerField live here until 2022.1 moved them into UnityEngine.UIElements
#endif

namespace PerfLint.UI
{
    /// <summary>
    /// The Mipmap Streaming tuning deck inside the Runtime Profiler window — the "debug UI you have to build yourself"
    /// that SRP lacks (Built-in had a mip debug view; SRP has none, which makes streaming parameters nearly untunable
    /// with stock tooling). Two halves:
    ///   · Live readouts (0.5s poll, Play Mode): what streaming is doing RIGHT NOW — current/desired/target/total
    ///     texture memory, the headline "saving ~X MB" figure (total − current), and an over-budget flag when the
    ///     Memory Budget is forcing mips below what the camera wants (= visual quality being traded).
    ///   · Tunable parameters (Memory Budget / Renderers Per Frame / Max Level Reduction / Max IO Requests / Add All
    ///     Cameras): take effect immediately in Play Mode so users can find their "positive-optimization" point per
    ///     scene, as the streaming NP-tuning workflow demands.
    ///
    /// Persistence (settled over three smoke rounds — the full story lives in dev-changelog 2026-07-02):
    ///   · Values changed in EDIT mode persist immediately (QualitySettings.asset).
    ///   · Values changed in PLAY mode are REVERTED by the editor on exit (manager state restore) — which would throw
    ///     away exactly the values a tuning session just found. So play-mode edits snapshot the parameter set into
    ///     SessionState, and back in edit mode a "keep the tuned values?" bar offers a one-click apply (or discard).
    ///   · The snapshot auto-clears when a NEW play session starts (stale bar prevention) and when the user edits any
    ///     field in edit mode (they've taken manual control; edit-mode changes persist on their own).
    /// Every parameter belongs to the ACTIVE QUALITY LEVEL — the deck shows the level name inline and refreshes it
    /// every poll, so a runtime level switch is visible instead of looking like lost tuning.
    /// Free (no Pro gate): the live "saving X MB" readout is the value demo; the paid piece is the batch enable action
    /// on PERF.TEXSTR001 in the static panel.
    /// </summary>
    internal static class TextureStreamingSection
    {
        private const string ExpandedPref = "PerfLint.TexStreamSection.Expanded";

        // Unity's documented defaults for the streaming parameters (the section's "Reset to defaults").
        private const float DefaultBudgetMb = 512f;
        private const int DefaultRenderersPerFrame = 512;
        private const int DefaultMaxLevelReduction = 2;
        private const int DefaultMaxIoRequests = 1024;

        // SessionState keys for the play-mode tuning snapshot (survives play-mode exit + domain reload, per session).
        private const string SnapHas = "PerfLint.TexStream.Snap.Has";
        private const string SnapActive = "PerfLint.TexStream.Snap.Active";
        private const string SnapBudget = "PerfLint.TexStream.Snap.Budget";
        private const string SnapRenderers = "PerfLint.TexStream.Snap.Renderers";
        private const string SnapReduction = "PerfLint.TexStream.Snap.Reduction";
        private const string SnapIo = "PerfLint.TexStream.Snap.Io";
        private const string SnapAllCams = "PerfLint.TexStream.Snap.AllCams";

        /// <summary>
        /// Called after every user edit. Playing → snapshot the full set (the editor will revert it on exit; last edit
        /// wins). Edit mode → clear any snapshot: the edit persists by itself, and a leftover play-mode snapshot would
        /// keep offering values the user has since overridden by hand (the exact confusion smoke round two hit).
        /// </summary>
        private static void OnUserEdit()
        {
            if (!EditorApplication.isPlaying)
            {
                ClearSnapshot();
                return;
            }
            SessionState.SetBool(SnapHas, true);
            SessionState.SetBool(SnapActive, QualitySettings.streamingMipmapsActive);
            SessionState.SetFloat(SnapBudget, QualitySettings.streamingMipmapsMemoryBudget);
            SessionState.SetInt(SnapRenderers, QualitySettings.streamingMipmapsRenderersPerFrame);
            SessionState.SetInt(SnapReduction, QualitySettings.streamingMipmapsMaxLevelReduction);
            SessionState.SetInt(SnapIo, QualitySettings.streamingMipmapsMaxFileIORequests);
            SessionState.SetBool(SnapAllCams, QualitySettings.streamingMipmapsAddAllCameras);
        }

        /// <summary>Whether a play-mode snapshot exists AND differs from the current (edit-mode, reverted) settings.</summary>
        private static bool SnapshotDiffers()
        {
            if (!SessionState.GetBool(SnapHas, false)) return false;
            return SessionState.GetBool(SnapActive, false) != QualitySettings.streamingMipmapsActive
                || Mathf.Abs(SessionState.GetFloat(SnapBudget, 0f) - QualitySettings.streamingMipmapsMemoryBudget) > 0.01f
                || SessionState.GetInt(SnapRenderers, 0) != QualitySettings.streamingMipmapsRenderersPerFrame
                || SessionState.GetInt(SnapReduction, 0) != QualitySettings.streamingMipmapsMaxLevelReduction
                || SessionState.GetInt(SnapIo, 0) != QualitySettings.streamingMipmapsMaxFileIORequests
                || SessionState.GetBool(SnapAllCams, true) != QualitySettings.streamingMipmapsAddAllCameras;
        }

        private static void ClearSnapshot() => SessionState.SetBool(SnapHas, false);

        /// <summary>
        /// A NEW play session invalidates any leftover snapshot (it described a previous session's tuning; edits made
        /// during the new session re-create it). Hooked globally — ExitingEditMode fires in the old domain before the
        /// play-mode reload, so this works whether or not the Runtime window is open, and prevents a stale "keep these
        /// values?" bar after an untouched play run.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void HookPlayModeTransitions()
        {
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode) ClearSnapshot();
            };
        }

        /// <summary>Applies the snapshot in edit mode and persists it — the "keep what I tuned" half of the workflow.</summary>
        private static void ApplySnapshot()
        {
            QualitySettings.streamingMipmapsActive = SessionState.GetBool(SnapActive, QualitySettings.streamingMipmapsActive);
            QualitySettings.streamingMipmapsMemoryBudget = SessionState.GetFloat(SnapBudget, QualitySettings.streamingMipmapsMemoryBudget);
            QualitySettings.streamingMipmapsRenderersPerFrame = SessionState.GetInt(SnapRenderers, QualitySettings.streamingMipmapsRenderersPerFrame);
            QualitySettings.streamingMipmapsMaxLevelReduction = SessionState.GetInt(SnapReduction, QualitySettings.streamingMipmapsMaxLevelReduction);
            QualitySettings.streamingMipmapsMaxFileIORequests = SessionState.GetInt(SnapIo, QualitySettings.streamingMipmapsMaxFileIORequests);
            QualitySettings.streamingMipmapsAddAllCameras = SessionState.GetBool(SnapAllCams, QualitySettings.streamingMipmapsAddAllCameras);
            AssetDatabase.SaveAssets();
            ClearSnapshot();
        }

        private static string SnapshotSummary()
            => $"Budget {SessionState.GetFloat(SnapBudget, 0f):0.#} MB · Renderers/Frame {SessionState.GetInt(SnapRenderers, 0)} · " +
               $"Max Level Reduction {SessionState.GetInt(SnapReduction, 0)} · Max IO {SessionState.GetInt(SnapIo, 0)} · " +
               (SessionState.GetBool(SnapActive, false)
                   ? L.Tr("streaming on", "串流开")
                   : L.Tr("streaming off", "串流关"));

        private static string ActiveLevelName()
        {
            var names = QualitySettings.names;
            int i = QualitySettings.GetQualityLevel();
            return (i >= 0 && i < names.Length) ? names[i] : i.ToString();
        }

        public static VisualElement Build()
        {
            var fold = new Foldout
            {
                text = L.Tr("Texture Streaming (Mipmap Streaming) tuning", "Texture Streaming（Mipmap 串流）调参"),
                value = EditorPrefs.GetBool(ExpandedPref, false)
            };
            fold.RegisterValueChangedCallback(e => EditorPrefs.SetBool(ExpandedPref, e.newValue));
            fold.style.marginBottom = 8;

            var body = new VisualElement();
            fold.Add(body);

            // ── Live readout block ──────────────────────────────────────────
            var stateLine = new Label { style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold } };
            var savingLine = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
            var budgetLine = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
            var countsLine = new Label { style = { fontSize = 11, opacity = 0.85f, whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
            body.Add(stateLine);
            body.Add(savingLine);
            body.Add(budgetLine);
            body.Add(countsLine);

            // ── Parameters (all per ACTIVE quality level; the toggle label shows which — see class note) ──
            var toggle = new Toggle();
            toggle.RegisterValueChangedCallback(e => { QualitySettings.streamingMipmapsActive = e.newValue; OnUserEdit(); });

            var budget = new FloatField(L.Tr("Memory Budget (MB)", "Memory Budget（MB）"))
            {
                tooltip = L.Tr("Total memory budget for loaded textures. Too high saves nothing; too low forces low mips everywhere (visible quality loss). Watch the over-budget line above while tuning.",
                               "已加载纹理的总内存预算。设太高省不下内存；设太低到处强制低级 Mip（画质肉眼可见受损）。调参时盯上方的超预算提示。")
            };
            budget.RegisterValueChangedCallback(e => { QualitySettings.streamingMipmapsMemoryBudget = Mathf.Max(16f, e.newValue); OnUserEdit(); });

            var renderers = new IntegerField(L.Tr("Renderers Per Frame", "Renderers Per Frame"))
            {
                tooltip = L.Tr("How many renderers the streaming system processes per frame. Lower = less CPU, slower mip response; higher = the reverse.",
                               "串流系统每帧处理多少个 Renderer。调低省 CPU 但 Mip 响应更慢；调高反之。")
            };
            renderers.RegisterValueChangedCallback(e => { QualitySettings.streamingMipmapsRenderersPerFrame = Mathf.Max(1, e.newValue); OnUserEdit(); });

            var reduction = new SliderInt(L.Tr("Max Level Reduction", "Max Level Reduction"), 1, 8)
            {
                showInputField = true,
                tooltip = L.Tr("Max mip levels a texture may drop when over budget. Higher = more memory headroom but bigger worst-case quality loss.",
                               "超预算时单张纹理最多可降低的 Mip 级数。越高内存越有余量，但最坏情况画质损失越大。")
            };
            reduction.RegisterValueChangedCallback(e => { QualitySettings.streamingMipmapsMaxLevelReduction = e.newValue; OnUserEdit(); });

            var io = new IntegerField(L.Tr("Max IO Requests", "Max IO Requests"))
            {
                tooltip = L.Tr("Max in-flight mip IO requests. Too low delays async texture upload; the OS has its own IO ceiling, so huge values just add overhead.",
                               "同时在途的 Mip IO 请求上限。太低会拖慢异步纹理上传；系统本身有 IO 上限，设太大徒增开销。")
            };
            io.RegisterValueChangedCallback(e => { QualitySettings.streamingMipmapsMaxFileIORequests = Mathf.Max(1, e.newValue); OnUserEdit(); });

            var allCams = new Toggle(L.Tr("Add All Cameras", "Add All Cameras"))
            {
                tooltip = L.Tr("Whether every camera drives streaming by default. Turn off to control per camera via Streaming Controller components.",
                               "是否默认让所有相机参与串流计算。关闭后可用 Streaming Controller 组件按相机精细控制。")
            };
            allCams.RegisterValueChangedCallback(e => { QualitySettings.streamingMipmapsAddAllCameras = e.newValue; OnUserEdit(); });

            foreach (var field in new VisualElement[] { toggle, budget, renderers, reduction, io, allCams })
            {
                field.style.marginTop = 2;
                body.Add(field);
            }

            var reset = new Button(() =>
            {
                QualitySettings.streamingMipmapsMemoryBudget = DefaultBudgetMb;
                QualitySettings.streamingMipmapsRenderersPerFrame = DefaultRenderersPerFrame;
                QualitySettings.streamingMipmapsMaxLevelReduction = DefaultMaxLevelReduction;
                QualitySettings.streamingMipmapsMaxFileIORequests = DefaultMaxIoRequests;
                QualitySettings.streamingMipmapsAddAllCameras = true;
                OnUserEdit();
            })
            { text = L.Tr("Reset parameters to Unity defaults", "参数重置为 Unity 默认值") };
            reset.style.marginTop = 4;
            reset.style.alignSelf = Align.FlexStart;
            body.Add(reset);

            body.Add(new Label(L.Tr("Changes made in EDIT mode persist immediately. Changes made in PLAY mode are reverted by Unity on exit — the deck remembers them and offers a one-click apply back in edit mode. " +
                                    "Parameters belong to the ACTIVE quality level (shown on the toggle) — if your game switches level at runtime, tune the level it actually plays on. " +
                                    "Tune per scene: lower the budget until the over-budget line appears / visuals degrade, then back off. Don't chase a global optimum — a verified positive point is enough.",
                                    "编辑态的改动立即持久；Play Mode 里的改动退出时会被 Unity 还原——面板会记住并在回到编辑态后提供一键应用。" +
                                    "参数属于「当前质量级别」（开关标签上有显示）——若游戏运行时会切质量级别，请在实际运行的那个级别上调。" +
                                    "建议逐场景调：把预算往下压到出现超预算提示/画质开始受损，再回退一档。不必追全局最优——验证为正优化的一组参数就够了。"))
            {
                style = { fontSize = 10, opacity = 0.6f, whiteSpace = WhiteSpace.Normal, marginTop = 4, unityFontStyleAndWeight = FontStyle.Italic }
            });

            // ── "Keep the tuned values" bar: edit mode only, when a play-mode snapshot differs from the
            //    (Unity-reverted) settings. Inserted at the top of the body so it's impossible to miss. ──
            var keepRow = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None, marginBottom = 4,
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(0.30f, 0.45f, 0.25f, 0.25f),
                    borderLeftWidth = 3, borderLeftColor = new Color(0.45f, 0.80f, 0.50f)
                }
            };
            var keepLabel = new Label { style = { whiteSpace = WhiteSpace.Normal } };
            keepRow.Add(keepLabel);
            var keepButtons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            var applyBtn = new Button(() =>
            {
                ApplySnapshot();
                keepRow.style.display = DisplayStyle.None;
            })
            { text = L.Tr("Apply to Quality Settings", "应用到 Quality Settings") };
            var discardBtn = new Button(() =>
            {
                ClearSnapshot();
                keepRow.style.display = DisplayStyle.None;
            })
            { text = L.Tr("Discard", "丢弃") };
            discardBtn.style.marginLeft = 4;
            keepButtons.Add(applyBtn);
            keepButtons.Add(discardBtn);
            keepRow.Add(keepButtons);
            body.Insert(0, keepRow);

            // ── Refresh loop: pull settings into the fields (unless focused) + live memory readout ──
            void Refresh()
            {
                bool active = QualitySettings.streamingMipmapsActive;
                bool playing = EditorApplication.isPlaying;

                // Offer the play-mode-tuned values once we're back in edit mode and Unity has reverted the settings.
                bool offerKeep = !playing && SnapshotDiffers();
                keepRow.style.display = offerKeep ? DisplayStyle.Flex : DisplayStyle.None;
                if (offerKeep)
                    keepLabel.text = L.Tr($"Play Mode tuning was reverted by Unity on exit. Keep the values you tuned?\n{SnapshotSummary()}",
                                          $"Play Mode 里调的参数在退出时被 Unity 还原了。要保留你调好的这组值吗？\n{SnapshotSummary()}");

                // The level name refreshes every poll so a runtime quality-level switch is visible, not mystifying.
                toggle.label = L.Tr($"Texture Streaming (quality level: {ActiveLevelName()})",
                                    $"Texture Streaming（质量级别：{ActiveLevelName()}）");

                // Reflect external changes without stomping a field the user is editing.
                if (toggle.focusController?.focusedElement != toggle) toggle.SetValueWithoutNotify(active);
                if (budget.focusController?.focusedElement != budget) budget.SetValueWithoutNotify(QualitySettings.streamingMipmapsMemoryBudget);
                if (renderers.focusController?.focusedElement != renderers) renderers.SetValueWithoutNotify(QualitySettings.streamingMipmapsRenderersPerFrame);
                reduction.SetValueWithoutNotify(QualitySettings.streamingMipmapsMaxLevelReduction);
                if (io.focusController?.focusedElement != io) io.SetValueWithoutNotify(QualitySettings.streamingMipmapsMaxFileIORequests);
                allCams.SetValueWithoutNotify(QualitySettings.streamingMipmapsAddAllCameras);

                if (!active)
                {
                    // Deliberately no pointer to PERF.TEXSTR001 here: that finding only exists when the eligible pool
                    // crosses its threshold — referencing it unconditionally would be a dangling cross-reference.
                    stateLine.text = L.Tr("Texture Streaming is OFF for the active quality level — flip the toggle below to enable it here (textures also need Stream Mip Maps in their import settings to participate).",
                                          "当前质量级别的 Texture Streaming 未开启——用下方开关即可打开（纹理还需在导入设置开 Stream Mip Maps 才会参与）。");
                    savingLine.text = budgetLine.text = countsLine.text = "";
                    return;
                }
                if (!playing)
                {
                    stateLine.text = L.Tr("Texture Streaming is ON. Enter Play Mode for live memory readouts and tuning.",
                                          "Texture Streaming 已开启。进入 Play Mode 后这里显示实时内存读数，可边跑边调参。");
                    savingLine.text = budgetLine.text = countsLine.text = "";
                    return;
                }

                ulong current = Texture.currentTextureMemory;   // actually resident now
                ulong desired = Texture.desiredTextureMemory;   // what current cameras ideally want (uncapped)
                ulong target = Texture.targetTextureMemory;     // desired after budget/reduction caps
                ulong total = Texture.totalTextureMemory;       // if every texture loaded at mip 0 (no streaming)

                stateLine.text = L.Tr($"Streaming live: resident {Mb(current)} · camera-desired {Mb(desired)} · budget-capped target {Mb(target)} · all-at-mip0 {Mb(total)}",
                                      $"串流实时：驻留 {Mb(current)} · 相机期望 {Mb(desired)} · 预算封顶后目标 {Mb(target)} · 全量 Mip0 {Mb(total)}");
                long saving = (long)total - (long)current;
                savingLine.text = saving > 0
                    ? L.Tr($"Streaming is currently saving ~{Mb((ulong)saving)} of texture memory vs. loading everything at full size.",
                           $"相比全量全尺寸加载，串流当前为你省下约 {Mb((ulong)saving)} 纹理内存。")
                    : "";
                if (desired > target)
                {
                    budgetLine.text = L.Tr($"⚠ Over budget: cameras want {Mb(desired)} but the budget caps at {Mb(target)} — textures are rendering below their ideal mip (quality being traded). Raise Memory Budget if this looks bad.",
                                           $"⚠ 超预算：相机期望 {Mb(desired)}，预算封顶到 {Mb(target)}——部分纹理在低于理想的 Mip 级渲染（画质在被牺牲）。若画面明显变糊，调高 Memory Budget。");
                    budgetLine.style.color = new Color(0.95f, 0.70f, 0.20f);
                }
                else
                {
                    budgetLine.text = L.Tr("Budget OK: every texture is at the mip level the cameras want.", "预算充足：所有纹理都在相机期望的 Mip 级别渲染。");
                    budgetLine.style.color = new Color(0.45f, 0.80f, 0.50f);
                }
                countsLine.text = L.Tr($"streaming textures {Texture.streamingTextureCount} · renderers {Texture.streamingRendererCount} · pending loads {Texture.streamingTexturePendingLoadCount} · mip uploads {Texture.streamingMipmapUploadCount} · non-streaming {Texture.nonStreamingTextureCount} ({Mb(Texture.nonStreamingTextureMemory)})",
                                       $"串流纹理 {Texture.streamingTextureCount} · Renderer {Texture.streamingRendererCount} · 待加载 {Texture.streamingTexturePendingLoadCount} · Mip 上传 {Texture.streamingMipmapUploadCount} · 非串流 {Texture.nonStreamingTextureCount}（{Mb(Texture.nonStreamingTextureMemory)}）");
            }

            Refresh();
            fold.schedule.Execute(Refresh).Every(500);
            return fold;
        }

        private static string Mb(ulong bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }
}
