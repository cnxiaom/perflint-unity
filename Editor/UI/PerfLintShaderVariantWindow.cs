using System.Reflection;
using PerfLint.L10n;
using PerfLint.Licensing;
using PerfLint.Scanners;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// Shader-variant panel (the "B layer"). RECORD the variants your project actually renders (Clear → Play → Save to a
    /// .shadervariants asset), then either WARM THEM UP at startup (the safe, recommended path — Unity precompiles them so
    /// they don't hitch on first use) or, experimentally, STRIP everything else at build time (off by default; it can drop
    /// variants the build needs and black-screen the player, so it's clearly flagged and gated).
    ///
    /// Privacy is identical to the rest of PerfLint: everything is captured locally; nothing is uploaded.
    /// </summary>
    public sealed class PerfLintShaderVariantWindow : EditorWindow
    {
        private Label _countLabel;
        private Label _stateLabel;
        private VisualElement _unavailableBox;
        private IVisualElementScheduledItem _poll;

        [MenuItem("Tools/PerfLint/Shader Variant Stripping")]
        public static void Open()
        {
            var win = GetWindow<PerfLintShaderVariantWindow>();
            win.titleContent = new GUIContent("PerfLint Shaders");
            win.minSize = new Vector2(460, 360);
            win.Show();
        }

        private void OnDisable() => _poll?.Pause();

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;

            // ── Toolbar ───────────────────────────────
            var toolbar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            var openMain = new Button(PerfLintWindow.Open) { text = L.Tr("Static Scan Panel", "静态扫描面板") };
            openMain.style.height = 26;
            toolbar.Add(openMain);
            var openRuntime = new Button(PerfLintRuntimeWindow.Open) { text = L.Tr("Runtime", "运行时") };
            openRuntime.style.height = 26;
            openRuntime.style.marginLeft = 6;
            toolbar.Add(openRuntime);
            L.InjectDevLangSwitch(toolbar, () => { root.Clear(); CreateGUI(); });
            root.Add(toolbar);

            // ── Unavailable notice (internal API didn't resolve on this editor) ──
            _unavailableBox = MakeCard();
            _unavailableBox.style.display = DisplayStyle.None;
            _unavailableBox.Add(new Label(L.Tr(
                "Shader-variant recording isn't available on this Unity version (the editor's internal tracking API didn't resolve). Static shader diagnostics (SHDR001) still work in the Scan panel.",
                "本 Unity 版本不支持着色器变体录制（编辑器内部追踪 API 未解析到）。静态着色器诊断（SHDR001）在扫描面板仍可用。"))
            { style = { whiteSpace = WhiteSpace.Normal, color = new Color(0.95f, 0.78f, 0.30f), fontSize = 11 } });
            root.Add(_unavailableBox);

            // ── Capture status card ───────────────────
            var statusCard = MakeCard();
            _countLabel = new Label("—") { style = { fontSize = 20, unityFontStyleAndWeight = FontStyle.Bold } };
            _stateLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, fontSize = 11, marginTop = 4 } };
            statusCard.Add(_countLabel);
            statusCard.Add(_stateLabel);
            root.Add(statusCard);

            // ── Record controls ───────────────────────
            var controls = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8, flexWrap = Wrap.Wrap } };
            var clearBtn = new Button(OnClear) { text = L.Tr("Clear captured", "清空已捕获") };
            clearBtn.style.height = 26;
            controls.Add(clearBtn);
            var saveBtn = new Button(OnSave) { text = L.Tr("Save captured…", "保存已捕获…") };
            saveBtn.style.height = 26;
            saveBtn.style.marginLeft = 6;
            saveBtn.style.backgroundColor = new Color(0.20f, 0.45f, 0.85f); // primary
            saveBtn.style.color = Color.white;
            saveBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            controls.Add(saveBtn);
            var playBtn = new Button(EnterPlay) { text = L.Tr("Enter Play Mode", "进入 Play Mode") };
            playBtn.style.height = 26;
            playBtn.style.marginLeft = 6;
            controls.Add(playBtn);
            root.Add(controls);

            // ── How-to ────────────────────────────────
            root.Add(new Label(L.Tr(
                "How to capture: 1) Clear. 2) Enter Play Mode and walk through the scenes, quality levels and platforms you ship — Unity records every shader variant it actually renders. 3) Save to a .shadervariants asset. Then turn on Warm-up below: Unity precompiles those variants at startup, so they don't hitch the first time they're used. (Build-time stripping from the same asset is also available, but it's experimental — see its warning.)",
                "录制步骤：1）清空。2）进入 Play Mode，把你要发布的场景、画质档、平台都走一遍——Unity 会记录它实际渲染过的每个着色器变体。3）保存为 .shadervariants 资产。然后在下方开启「启动预热」：Unity 启动时预编译这些变体，首次使用就不会卡顿。（同一份资产也可做构建期剥离，但那是实验性的——见其警告。）"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f, fontSize = 11, marginTop = 10 } });

            root.Add(new Label(L.Tr(
                "Tip: capture is cumulative until you Clear. Run several sessions (different scenes/quality/platforms) for fuller coverage — warm-up is safe either way (precompiling fewer variants just means a few first-use hitches remain), but stripping to an incomplete capture is what breaks builds.",
                "提示：捕获会累积，直到你点清空。多跑几次（不同场景/画质/平台）覆盖更全——预热无论如何都安全（少预热几个，只是还剩几次首用卡顿），但「剥离到不完整捕获」才是会弄坏 build 的那个。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, fontSize = 11, marginTop = 6, unityFontStyleAndWeight = FontStyle.Italic } });

            root.Add(BuildBLayerSections());

            var strictCard = BuildStrictMatchingCard();
            if (strictCard != null) root.Add(strictCard);

            root.Add(new Label(L.Tr(
                "Recording runs locally and is never uploaded.",
                "录制在本机完成、永不上传。"))
            { style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.5f, marginTop = 10, fontSize = 10 } });

            RefreshState();
            _poll = root.schedule.Execute(RefreshState).Every(500);
        }

        private void RefreshState()
        {
            bool ok = ShaderVariantRecorder.Available;
            if (_unavailableBox != null) _unavailableBox.style.display = ok ? DisplayStyle.None : DisplayStyle.Flex;
            if (!ok)
            {
                if (_countLabel != null) _countLabel.text = "—";
                if (_stateLabel != null) _stateLabel.text = "";
                return;
            }

            int variants = ShaderVariantRecorder.VariantCount;
            int shaders = ShaderVariantRecorder.ShaderCount;
            _countLabel.text = L.Tr($"Captured {variants:N0} variants · {shaders:N0} shaders", $"已捕获 {variants:N0} 个变体 · {shaders:N0} 个着色器");
            _stateLabel.text = EditorApplication.isPlaying
                ? L.Tr("Recording while in Play Mode — exercise your scenes; the count grows as new variants render.", "Play Mode 录制中——多走走场景；渲染到新变体时计数会增长。")
                : L.Tr("Tracking editor rendering. Enter Play Mode for representative coverage, then Save.", "正在追踪编辑器渲染。进入 Play Mode 走一遍更有代表性，然后保存。");
        }

        private void OnClear()
        {
            if (!ShaderVariantRecorder.Clear())
                EditorUtility.DisplayDialog(L.Tr("Clear failed", "清空失败"),
                    L.Tr("Could not clear the tracked variant collection on this Unity version.", "本 Unity 版本无法清空追踪的变体集合。"), "OK");
            RefreshState();
        }

        private void OnSave()
        {
            if (ShaderVariantRecorder.VariantCount <= 0)
            {
                EditorUtility.DisplayDialog(L.Tr("Nothing captured yet", "尚未捕获任何变体"),
                    L.Tr("No variants have been tracked yet. Enter Play Mode and render your scenes first, then Save.", "目前还没追踪到任何变体。请先进入 Play Mode 渲染你的场景，再保存。"), "OK");
                return;
            }
            string path = EditorUtility.SaveFilePanelInProject(
                L.Tr("Save captured shader variants", "保存已捕获的着色器变体"),
                "CapturedShaderVariants", "shadervariants",
                L.Tr("Choose where to save the .shadervariants asset", "选择 .shadervariants 资产的保存位置"));
            if (string.IsNullOrEmpty(path)) return;

            if (ShaderVariantRecorder.Save(path))
            {
                AssetDatabase.Refresh();
                var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
                ShowNotification(new GUIContent(L.Tr("Shader variants saved", "已保存着色器变体")));
            }
            else
            {
                EditorUtility.DisplayDialog(L.Tr("Save failed", "保存失败"),
                    L.Tr("Could not save the variant collection on this Unity version.", "本 Unity 版本无法保存变体集合。"), "OK");
            }
        }

        private static void EnterPlay()
        {
            if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// What to do with the captured collection: a shared SVC picker, then the SAFE option (warm-up at startup, the
        /// recommended path) and the EXPERIMENTAL option (build-time stripping, which can break a build — kept off and
        /// clearly flagged after it black-screened a real build during testing).
        /// </summary>
        private VisualElement BuildBLayerSections()
        {
            var settings = ShaderStripSettings.instance;
            var container = new VisualElement();

            // ── Shared "captured collection" picker (used by both warm-up and stripping) ──
            var pickCard = MakeCard();
            pickCard.style.marginTop = 4;
            pickCard.Add(new Label(L.Tr("Use this captured collection", "使用以下已捕获集合"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 } });
            var svcField = new ObjectField
            {
                objectType = typeof(ShaderVariantCollection),
                allowSceneObjects = false,
                value = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(settings.svcPath)
            };
            pickCard.Add(svcField);
            container.Add(pickCard);

            ShaderVariantCollection Svc() => svcField.value as ShaderVariantCollection;

            // ── Warm-up (recommended, safe) ──
            var warmCard = MakeCard();
            warmCard.Add(new Label(L.Tr("Warm up at startup (Pro) · recommended", "启动预热（Pro）· 推荐"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4, color = new Color(0.45f, 0.80f, 0.50f) } });
            var warmToggle = new Toggle(L.Tr("Preload this collection at startup", "启动时预热此集合"));
            WrapToggleLabel(warmToggle);
            warmCard.Add(warmToggle);
            // Dynamic benefit/status line — makes the payoff concrete (how many variants get precompiled, and whether it's on).
            var warmInfo = new Label { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 4 } };
            warmCard.Add(warmInfo);

            void UpdateWarm()
            {
                var svc = Svc();
                bool on = svc != null && ShaderWarmup.IsPreloaded(svc);
                warmToggle.SetValueWithoutNotify(on);
                if (svc == null)
                {
                    warmInfo.text = L.Tr("Pick a captured collection above to enable warm-up.", "在上方选择已捕获集合以启用预热。");
                    warmInfo.style.color = new StyleColor(StyleKeyword.Null);
                    warmInfo.style.opacity = 0.6f;
                    return;
                }
                int v = svc.variantCount, s = svc.shaderCount;
                if (on)
                {
                    warmInfo.text = L.Tr($"✓ Preloaded — Unity precompiles all {v:N0} variants ({s:N0} shaders) at startup, so they won't hitch on first use.",
                                         $"✓ 已预热——Unity 启动时预编译全部 {v:N0} 个变体（{s:N0} 个着色器），首次使用不再卡顿。");
                    warmInfo.style.color = new Color(0.45f, 0.80f, 0.50f);
                    warmInfo.style.opacity = 1f;
                }
                else
                {
                    warmInfo.text = L.Tr($"This collection has {v:N0} variants across {s:N0} shaders. Enable to precompile them at startup.",
                                         $"此集合含 {v:N0} 个变体、{s:N0} 个着色器。启用后启动时预编译。");
                    warmInfo.style.color = new StyleColor(StyleKeyword.Null);
                    warmInfo.style.opacity = 0.7f;
                }
            }

            warmToggle.RegisterValueChangedCallback(evt =>
            {
                var svc = Svc();
                if (svc == null) { warmToggle.SetValueWithoutNotify(false); PickFirstDialog(); return; }
                if (evt.newValue && !Entitlements.RequirePro(L.Tr("Shader warm-up", "着色器预热"))) { warmToggle.SetValueWithoutNotify(false); return; }
                bool ok = evt.newValue ? ShaderWarmup.AddToPreload(svc) : ShaderWarmup.RemoveFromPreload(svc);
                if (!ok) warmToggle.SetValueWithoutNotify(!evt.newValue);
                UpdateWarm();
            });
            warmCard.Add(new Label(L.Tr(
                "Adds the collection to Project Settings → Graphics → Preloaded Shaders. Safe: warm-up only pre-compiles — it never removes anything, so it can't break the build.",
                "把集合加入 Project Settings → Graphics → Preloaded Shaders。安全：预热只预编译、永不删除，不会弄坏 build。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.5f, fontSize = 10, marginTop = 4, unityFontStyleAndWeight = FontStyle.Italic } });
            container.Add(warmCard);

            // ── Build-time stripping (experimental, dangerous) ── hidden by default behind a collapsed foldout.
            // Research (docs/shader-strip-safety-research.md) concluded usage-based stripping can't be made safe: it
            // misuses the SVC (whose job is to KEEP/warm variants, not strip more) and fights Unity's already-correct
            // default stripping — it black-screened a real build. Kept only as a hidden expert escape hatch.
            var stripCard = MakeCard();
            stripCard.style.marginBottom = 6;
            stripCard.Add(new Label(L.Tr("Strips variants the build didn't capture — not recommended.", "剥掉捕获里没有的变体——不推荐。"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4, color = new Color(0.95f, 0.70f, 0.20f) } });

            var enableToggle = new Toggle(L.Tr("Enable build-time stripping", "启用构建期剥离")) { value = settings.enabled };
            WrapToggleLabel(enableToggle);
            enableToggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    if (Svc() == null)
                    {
                        enableToggle.SetValueWithoutNotify(false);
                        PickFirstDialog();
                        return;
                    }
                    if (!Entitlements.RequirePro(L.Tr("Shader variant stripping", "着色器变体剥离"))) { enableToggle.SetValueWithoutNotify(false); return; }
                }
                settings.enabled = evt.newValue;
                settings.SaveNow();
            });
            stripCard.Add(enableToggle);

            var aggrToggle = new Toggle(L.Tr("Aggressive mode", "激进模式")) { value = settings.aggressive };
            WrapToggleLabel(aggrToggle);
            aggrToggle.tooltip = L.Tr(
                "Aggressive: prune every shader to the captured set — bigger savings, but anything you didn't exercise can break in the build.",
                "激进：把所有 shader 都剥到捕获集合——省得更多，但任何没走到的都可能在 build 里出问题。");
            aggrToggle.RegisterValueChangedCallback(evt => { settings.aggressive = evt.newValue; settings.SaveNow(); });
            stripCard.Add(aggrToggle);

            // Diagnostic: log each stripped variant so it can be cross-referenced against Strict-Matching's "missing variant"
            // errors — the way to find whether a broken build is over-stripping (a bug) or a genuine capture gap.
            var logToggle = new Toggle(L.Tr("Log stripped variants (diagnostic)", "记录被剥变体（诊断）")) { value = settings.logStripped };
            WrapToggleLabel(logToggle);
            logToggle.tooltip = L.Tr(
                "Prints every stripped variant (shader / pass / keywords) to the build Console. Pair with Strict shader variant matching: if a variant is logged here AND reported missing there AND is in your collection, it's an over-stripping bug; if it's not in the collection, the capture was incomplete.",
                "把每个被剥的变体（shader / pass / keywords）打到构建 Console。配合「严格着色器变体匹配」：若某变体这里被剥、那里报缺失、且在你集合里，就是过度剥离 bug；若不在集合里，则是捕获不全。");
            logToggle.RegisterValueChangedCallback(evt => { settings.logStripped = evt.newValue; settings.SaveNow(); });
            stripCard.Add(logToggle);

            stripCard.Add(new Label(L.Tr(
                "⚠ Stripping removes variants the build didn't capture — an incomplete capture can drop variants the player actually needs, rendering pink or a black screen (only visible in the built player). Prefer warm-up above. Only enable this if you can fully test the built player on every scene, quality level and platform. Turn it off and rebuild to recover.",
                "⚠ 剥离会删掉捕获里没有的变体——捕获不全就会丢掉播放器真正需要的变体，导致粉红或黑屏（只在打出的包里才暴露）。优先用上面的预热。只有当你能在所有场景/画质/平台上完整测试构建产物时才启用。出问题就关掉它、重新打包即可恢复。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, fontSize = 11, marginTop = 4, color = new Color(0.95f, 0.75f, 0.45f) } });

            // Hidden by default — collapsed foldout keeps the experimental escape hatch available without promoting it.
            var advFold = new Foldout
            {
                text = L.Tr("Advanced: build-time stripping (experimental)", "高级：构建期剥离（实验性）"),
                value = false
            };
            advFold.style.marginTop = 4;
            advFold.Add(stripCard);
            container.Add(advFold);

            // Keep settings + the warm-up status in sync when the shared collection changes.
            svcField.RegisterValueChangedCallback(evt =>
            {
                settings.svcPath = evt.newValue != null ? AssetDatabase.GetAssetPath(evt.newValue) : "";
                settings.SaveNow();
                UpdateWarm();
            });

            UpdateWarm(); // initial state
            return container;
        }

        /// <summary>
        /// "Strict Shader Variant Matching" (Player setting, Unity 2022.3+). When on, the built player renders the error
        /// shader + logs a console error for any variant it's missing, instead of silently substituting the closest one —
        /// the safe way to verify warm-up/stripping/runtime-keyword coverage. The card is omitted on Unity versions where
        /// the API doesn't exist (resolved via reflection, so it never breaks compilation on 2021.3).
        /// </summary>
        private VisualElement BuildStrictMatchingCard()
        {
            bool? cur = GetStrictMatching();
            if (cur == null) return null;

            var card = MakeCard();
            card.style.marginTop = 4;
            card.Add(new Label(L.Tr("Verify a test build (recommended)", "校验测试构建（推荐）"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 } });

            var toggle = new Toggle(L.Tr("Strict shader variant matching", "严格着色器变体匹配")) { value = cur.Value };
            WrapToggleLabel(toggle);
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (!SetStrictMatching(evt.newValue)) toggle.SetValueWithoutNotify(!evt.newValue);
            });
            card.Add(toggle);

            card.Add(new Label(L.Tr(
                "When on, the built player renders the error shader and logs a console error for any variant it's missing — instead of silently substituting the closest one. Turn it on for a test build to catch gaps (warm-up coverage, experimental stripping, or keywords toggled at runtime), then turn it off to ship with fuzzy fallback if you prefer. Player only.",
                "开启后，构建出的播放器对任何缺失的变体会渲染错误着色器并在 Console 报错——而不是静默替换成最接近的。打测试包前开启它，揪出覆盖缺口（预热不全、实验性剥离、或运行时切换的 keyword）；之后若想要模糊回退再关掉。仅 Player 生效。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, fontSize = 11, marginTop = 4 } });

            return card;
        }

        private static PropertyInfo _strictProp;
        private static bool _strictResolved;
        private static PropertyInfo StrictProp()
        {
            if (!_strictResolved)
            {
                _strictResolved = true;
                try { _strictProp = typeof(PlayerSettings).GetProperty("strictShaderVariantMatching", BindingFlags.Public | BindingFlags.Static); }
                catch { _strictProp = null; }
            }
            return _strictProp;
        }

        /// <summary>Current strict-matching value, or null when the API doesn't exist on this Unity version.</summary>
        private static bool? GetStrictMatching()
        {
            var p = StrictProp();
            if (p == null || p.GetGetMethod() == null) return null;
            try { return (bool)p.GetValue(null); }
            catch { return null; }
        }

        private static bool SetStrictMatching(bool value)
        {
            var p = StrictProp();
            if (p == null || p.GetSetMethod() == null) return false;
            try { p.SetValue(null, value); return true; }
            catch { return false; }
        }

        private static void PickFirstDialog() => EditorUtility.DisplayDialog(
            L.Tr("Pick a collection first", "请先选择集合"),
            L.Tr("Choose a captured .shadervariants asset above first.", "请先在上方选择一个已捕获的 .shadervariants 资产。"), "OK");

        private static void WrapToggleLabel(Toggle t)
        {
            var lbl = t.labelElement ?? t.Q<Label>();
            if (lbl != null) { lbl.style.whiteSpace = WhiteSpace.Normal; lbl.style.minWidth = 0; }
        }

        private static VisualElement MakeCard()
        {
            var card = new VisualElement
            {
                style =
                {
                    marginBottom = 8,
                    paddingTop = 10, paddingBottom = 10, paddingLeft = 14, paddingRight = 14,
                    backgroundColor = new Color(1f, 1f, 1f, 0.03f),
                    borderTopLeftRadius = 10, borderTopRightRadius = 10,
                    borderBottomLeftRadius = 10, borderBottomRightRadius = 10,
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                }
            };
            var c = new Color(1f, 1f, 1f, 0.07f);
            card.style.borderTopColor = c; card.style.borderBottomColor = c;
            card.style.borderLeftColor = c; card.style.borderRightColor = c;
            return card;
        }
    }
}
