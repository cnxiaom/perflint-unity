using System.Collections.Generic;
using PerfLint.L10n;
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

        // Provider changes affect available options and copy, so the entire panel is rebuilt.
        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            // English-only by default. A dev-only EN/中 switch is injected here ONLY in a PERFLINT_DEV editor
            // (no-op in release — see L.InjectDevLangSwitch); flipping it rebuilds the panel in the new language.
            L.InjectDevLangSwitch(root, Rebuild);

            // ── Zero-config ready card (Hosted default) ──
            bool byo = LlmSettings.Mode == LlmMode.ByoKey;
            root.Add(new Label(L.Tr(
                "Explain & AI Fix work out of the box — zero config. Calls run through PerfLint's zero-log AI service.",
                "Explain 与 AI Fix 开箱即用、零配置。调用经 PerfLint 零日志 AI 服务转发。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, marginTop = 8 }
            });

            var credits = new Label(byo
                    ? L.Tr("Using your own API key — unlimited, never counted against credits.", "正在使用你自己的 API key——不限量、永不计入 credits。")
                    : Licensing.CreditService.RemainingText())
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.75f, fontSize = 11, marginTop = 2, marginBottom = 8 }
            };
            root.Add(credits);

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
            root.Add(advToggle);
            root.Add(new Label(L.Tr(
                "Direct to the provider — unlimited, bypasses credits.",
                "直连服务商——不限量、绕过 credits。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, fontSize = 10, marginLeft = 18, marginBottom = 2 }
            });

            if (byo)
            {
                var adv = new VisualElement { style = { marginLeft = 12, marginBottom = 8 } };

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

                adv.Add(new Label(L.Tr(
                    "Routine explanations use the default (cheap/fast) model; migration-domain rules auto-use the stronger model. Your key never leaves this machine except in direct calls to the provider.",
                    "日常解释用默认（便宜快）模型；迁移类规则自动用更强的模型。你的 key 只在直连服务商时使用，绝不经过我们的服务器。"))
                {
                    style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, fontSize = 10, marginTop = 2 }
                });

                var test = new Button(TestConnection) { text = L.Tr("Test connection", "测试连接") };
                test.style.marginTop = 4;
                adv.Add(test);

                _status = new Label("") { style = { whiteSpace = WhiteSpace.Normal, marginTop = 6 } };
                adv.Add(_status);

                root.Add(adv);
            }

            // ── AI Fix: auto-verify and auto-rollback in the background after applying ── (details in the Label below, so the toggle label is kept short to prevent the checkbox from being pushed off the window edge)
            var autoVerify = new Toggle(L.Tr("Auto-verify AI fixes", "AI 修复后自动校验并回滚"))
            {
                value = LlmSettings.AutoVerifyFix
            };
            autoVerify.style.marginTop = 6;
            WrapToggleLabel(autoVerify);
            autoVerify.RegisterValueChangedCallback(e => LlmSettings.AutoVerifyFix = e.newValue);
            root.Add(autoVerify);
            root.Add(new Label(L.Tr(
                "On: applying a fix triggers one background recompile a few seconds later (one domain reload); broken fixes auto-roll back. Off (for very large projects): relies on pre-write guards + the next natural compile.",
                "开：应用修复几秒后后台触发一次编译（一次域重载），坏修复自动回滚。关（超大工程）：仅靠写入前守卫 + 下次自然编译校验。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, fontSize = 10, marginTop = 2, marginBottom = 8 }
            });

            root.Add(new Label(L.Tr(
                "Privacy: scans never leave your machine. AI Fix sends only the snippet you choose — through PerfLint's proxy, which never logs request bodies — or, with your own key, direct to the provider (never through our servers).",
                "隐私：扫描永不离开你的机器。AI Fix 仅发送你选择的那段代码——经 PerfLint 代理转发（代理绝不记录请求内容）；若用自己的 key，则直连服务商、绝不经过我们的服务器。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.55f, fontSize = 10, marginTop = 10 }
            });
        }

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
        }

        private void TestConnection()
        {
            if (!LlmSettings.IsConfigured)
            {
                _status.text = L.Tr("Enable LLM and enter an API key first.", "请先启用并填入 API Key。");
                return;
            }
            _status.text = L.Tr("Testing…", "测试中…");
            LlmClient.Send(
                model: LlmSettings.Model,
                system: null,
                messages: new[] { new LlmMessage("user", L.Tr("Reply with: OK", "回复两个字：可用")) },
                maxTokens: 200,
                onDone: r => _status.text = r.Success
                    ? L.Tr("Connected: ", "连接成功：") + r.Text
                    : L.Tr("Failed: ", "失败：") + r.Error);
        }
    }
}
