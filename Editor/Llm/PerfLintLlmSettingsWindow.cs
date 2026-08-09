using System.Collections.Generic;
using PerfLint.L10n;
using PerfLint.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.Llm
{
    /// <summary>PerfLint's LLM (Claude / DeepSeek) settings panel. Keys are stored in EditorPrefs (local machine only).</summary>
    public sealed class PerfLintLlmSettingsWindow : EditorWindow
    {
        private Label _status;

        public static void Open()
        {
            var w = GetWindow<PerfLintLlmSettingsWindow>(true, "PerfLint · LLM");
            w.minSize = new Vector2(440, 300);
            w.Show();
        }

        private void CreateGUI() => Rebuild();

        // Live-refresh the credits line when the balance changes (after a /llm call) or the license tier
        // flips (Free↔Pro) — otherwise an already-open panel keeps showing the previous tier's allowance.
        private void OnEnable()
        {
            Licensing.CreditService.Changed += Rebuild;
            Licensing.LicenseService.Changed += OnLicenseChanged;
            // Fetch the true remaining balance on open (no credit spent) so the panel shows the real number
            // immediately, instead of the "5000/month · ready" standby that only self-corrects after the next call.
            LlmClient.SyncHostedBalance();
        }

        private void OnDisable()
        {
            Licensing.CreditService.Changed -= Rebuild;
            Licensing.LicenseService.Changed -= OnLicenseChanged;
        }

        // A tier flip (Free↔Pro, incl. dev unlock toggle) drops the cached balance (different server pool);
        // re-fetch the new tier's real balance so the panel doesn't linger on the standby allowance.
        private void OnLicenseChanged()
        {
            Rebuild();
            LlmClient.SyncHostedBalance();
        }

        // Re-pull whenever the panel regains focus. The credit balance is a per-machine snapshot taken at the
        // last fetch; a spend on another machine (or another editor session) sharing the same Pro monthly pool
        // isn't pushed to us, so an already-open panel would keep showing a stale count. Re-fetching on focus
        // means the number is current whenever the user actually looks at it. No credit spent; Hosted-only
        // (SyncHostedBalance no-ops in BYO mode).
        private void OnFocus() => LlmClient.SyncHostedBalance();

        // Provider changes affect available options and copy, so the entire panel is rebuilt.
        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingBottom = 10;

            // Users switch language from Tools ▸ PerfLint ▸ Language; this is the dev-only inline shortcut, injected
            // ONLY in a PERFLINT_DEV editor (no-op in release — see L.InjectDevLangSwitch). Either way the panel is
            // rebuilt in the new language.
            L.InjectDevLangSwitch(root, Rebuild);

            // Everything scrolls. The window opens at 440x300 and the BYO branch below adds a provider dropdown,
            // a toggle, a key field, a model dropdown, a paragraph, a button and a status line — so turning the
            // escape hatch ON pushed the auto-verify toggle and the privacy note off the bottom, with no scrollbar
            // to reach them. Ticking a box must not hide settings.
            var body = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            root.Add(body);

            bool byo = LlmSettings.Mode == LlmMode.ByoKey;

            // ── Zero-config ready card (Hosted default) ──
            body.Add(new Label(L.Tr("Explain & AI Fix, zero config", "Explain 与 AI Fix，零配置"))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, marginBottom = 4,
                          whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink }
            });
            body.Add(new Label(L.Tr(
                "They work out of the box. Calls run through PerfLint's zero-log AI service.",
                "两者开箱即用。调用经 PerfLint 零日志 AI 服务转发。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 8, color = PerfLintStyle.Dim }
            });

            // The balance is the one fact somebody opens this window to read, and it used to be a 0.75-opacity
            // footnote under a paragraph. It gets the card.
            var creditCard = PerfLintStyle.Card();
            creditCard.Add(new Label(byo
                    ? L.Tr("Using your own API key — unlimited, never counted against credits.", "正在使用你自己的 API key——不限量、永不计入 credits。")
                    : Licensing.CreditService.RemainingText())
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 13,
                          unityFontStyleAndWeight = FontStyle.Bold,
                          color = byo ? PerfLintStyle.Good : PerfLintStyle.Ink }
            });
            body.Add(creditCard);

            // ── Advanced: bring your own API key (escape hatch) ──
            // Keep the label short: an overly long Toggle label pushes the checkbox off the right edge of the window (440 px wide), making it invisible and unclickable, so the BYO section can never be entered.
            var advToggle = new Toggle(L.Tr("Advanced: bring your own API key", "高级：自带 API key"))
            {
                value = byo
            };
            advToggle.style.marginTop = 4;
            WrapToggleLabel(advToggle);
            advToggle.RegisterValueChangedCallback(e =>
            {
                LlmSettings.Mode = e.newValue ? LlmMode.ByoKey : LlmMode.Hosted;
                Rebuild();
            });
            body.Add(advToggle);
            body.Add(Note(L.Tr(
                "Direct to the provider — unlimited, bypasses credits.",
                "直连服务商——不限量、绕过 credits。")));

            if (byo)
            {
                // A recessed block, so the settings that only exist while the box is ticked read as belonging to
                // it rather than as four more top-level rows.
                var adv = PerfLintStyle.Panel();
                adv.style.marginTop = 4;
                adv.style.marginBottom = 8;

                // ── Provider ──
                var provider = new DropdownField(
                    L.Tr("Provider", "服务商"),
                    new List<string> { "Claude (Anthropic)", "DeepSeek" },
                    (int)LlmSettings.Provider);
                provider.RegisterValueChangedCallback(e =>
                {
                    LlmSettings.Provider = provider.index == 1 ? LlmProvider.DeepSeek : LlmProvider.Anthropic;
                    Rebuild(); // key/model are stored per-Provider, so a refresh is needed
                });
                adv.Add(provider);

                // ── Enable ──
                var enable = new Toggle(L.Tr("Enable LLM (use this key)", "启用 LLM（使用此 Key）"))
                {
                    value = LlmSettings.Enabled
                };
                WrapToggleLabel(enable);
                enable.RegisterValueChangedCallback(e => LlmSettings.Enabled = e.newValue);
                adv.Add(enable);

                // ── Key ──
                var key = new TextField(L.Tr("API Key", "API Key")) { value = LlmSettings.ApiKey, isPasswordField = true };
                key.RegisterValueChangedCallback(e => LlmSettings.ApiKey = e.newValue);
                adv.Add(key);

                // ── Model ──
                var choices = new List<string>(LlmSettings.ModelChoices(LlmSettings.Provider));
                int idx = Mathf.Max(0, choices.IndexOf(LlmSettings.Model));
                var model = new DropdownField(L.Tr("Default model", "默认模型"), choices, idx);
                model.RegisterValueChangedCallback(e => LlmSettings.Model = e.newValue);
                adv.Add(model);

                adv.Add(Note(L.Tr(
                    "Routine explanations use the default (cheap/fast) model; migration-domain rules auto-use the stronger model. Your key never leaves this machine except in direct calls to the provider.",
                    "日常解释用默认（便宜快）模型；迁移类规则自动用更强的模型。你的 key 只在直连服务商时使用，绝不经过我们的服务器。")));

                var test = PerfLintStyle.Secondary(L.Tr("Test connection", "测试连接"), TestConnection);
                test.style.marginTop = 6;
                test.style.alignSelf = Align.FlexStart;
                adv.Add(test);

                _status = new Label("") { style = { whiteSpace = WhiteSpace.Normal, marginTop = 6, color = PerfLintStyle.Dim } };
                adv.Add(_status);

                body.Add(adv);
            }

            // ── AI Fix: auto-verify and auto-rollback in the background after applying ── (details in the Label below, so the toggle label is kept short to prevent the checkbox from being pushed off the window edge)
            var autoVerify = new Toggle(L.Tr("Auto-verify AI fixes", "AI 修复后自动校验并回滚"))
            {
                value = LlmSettings.AutoVerifyFix
            };
            autoVerify.style.marginTop = 6;
            WrapToggleLabel(autoVerify);
            autoVerify.RegisterValueChangedCallback(e => LlmSettings.AutoVerifyFix = e.newValue);
            body.Add(autoVerify);
            body.Add(Note(L.Tr(
                "On: applying a fix triggers one background recompile a few seconds later (one domain reload); broken fixes auto-roll back. Off (for very large projects): relies on pre-write guards + the next natural compile.",
                "开：应用修复几秒后后台触发一次编译（一次域重载），坏修复自动回滚。关（超大工程）：仅靠写入前守卫 + 下次自然编译校验。")));

            // The trust anchor gets a block rather than fine print. It is the product's core claim — the one
            // thing a reader has to believe before using either feature — and it was set in italics at 0.55
            // opacity, which is how a page says "boilerplate, skip me".
            var privacy = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            privacy.style.marginTop = 10;
            privacy.Add(new Label(L.Tr(
                "Privacy: scans never leave your machine. AI Fix sends only the snippet you choose — through PerfLint's proxy, which never logs request bodies — or, with your own key, direct to the provider (never through our servers).",
                "隐私：扫描永不离开你的机器。AI Fix 仅发送你选择的那段代码——经 PerfLint 代理转发（代理绝不记录请求内容）；若用自己的 key，则直连服务商、绝不经过我们的服务器。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, color = PerfLintStyle.Dim }
            });
            body.Add(privacy);
        }

        /// <summary>The line under a control that says what it does. One tint, so four of them cannot drift apart.</summary>
        private static Label Note(string text) => new Label(text)
        {
            style = { whiteSpace = WhiteSpace.Normal, fontSize = 10, marginLeft = 18, marginTop = 2,
                      marginBottom = 2, color = PerfLintStyle.Dimmer }
        };

        /// <summary>Makes the Toggle label wrap instead of overflowing horizontally — otherwise a long label pushes the checkbox off the right edge of the window, making it unreachable.</summary>
        private static void WrapToggleLabel(Toggle t)
        {
            // Use BaseField.labelElement (populated in the constructor) rather than t.Q<Label>(): on Unity 2021.3 the
            // label child isn't queryable yet right after `new Toggle(...)`, so Q<Label>() returns null here and the
            // styling was silently skipped (the label never wrapped). labelElement is non-null immediately across versions.
            var lbl = t.labelElement ?? t.Q<Label>();
            if (lbl == null) return;
            lbl.style.whiteSpace = WhiteSpace.Normal;
            lbl.style.flexShrink = 1;
            lbl.style.color = PerfLintStyle.Ink;
        }

        private void TestConnection()
        {
            if (!LlmSettings.IsConfigured)
            {
                SetStatus(L.Tr("Enable LLM and enter an API key first.", "请先启用并填入 API Key。"), PerfLintStyle.Amber);
                return;
            }
            SetStatus(L.Tr("Testing…", "测试中…"), PerfLintStyle.Dim);
            LlmClient.Send(
                model: LlmSettings.Model,
                system: null,
                messages: new[] { new LlmMessage("user", L.Tr("Reply with: OK", "回复两个字：可用")) },
                maxTokens: 200,
                onDone: r =>
                {
                    if (r.Success) SetStatus(L.Tr("Connected: ", "连接成功：") + r.Text, PerfLintStyle.Good);
                    else SetStatus(L.Tr("Failed: ", "失败：") + r.Error, PerfLintStyle.Bad);
                });
        }

        /// <summary>
        /// The test result, in the colour of its outcome.
        ///
        /// Null-guarded because the callback lands whenever the network does: the panel rebuilds itself on a
        /// provider change, a credit change and a licence flip, any of which can happen while a test is in
        /// flight — and a rebuild that leaves BYO mode drops the status label entirely.
        /// </summary>
        private void SetStatus(string text, Color tint)
        {
            if (_status == null) return;
            _status.text = text;
            _status.style.color = tint;
        }
    }
}
