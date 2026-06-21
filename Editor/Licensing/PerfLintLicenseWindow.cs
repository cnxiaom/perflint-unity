using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.Licensing
{
    /// <summary>PerfLint license panel: displays Free/Pro status, license key input, activate/deactivate, and purchase entry point.</summary>
    public sealed class PerfLintLicenseWindow : EditorWindow
    {
        private Label _status;
        private Label _msg;
        private TextField _keyField;
        private Button _activate;
        private Button _deactivate;

        public static void Open()
        {
            var w = GetWindow<PerfLintLicenseWindow>(true, "PerfLint · License");
            w.minSize = new Vector2(460, 340);
            w.Show();
        }

        private void OnEnable() => LicenseService.Changed += Refresh;
        private void OnDisable() => LicenseService.Changed -= Refresh;

        private void CreateGUI() => Rebuild();

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            // ── Language ──
            var lang = new DropdownField(
                L.Tr("Language", "语言"),
                new System.Collections.Generic.List<string> { "English", "中文" },
                (int)L.Current);
            lang.RegisterValueChangedCallback(_ =>
            {
                L.Current = lang.index == 1 ? Lang.Chinese : Lang.English;
                Rebuild();
            });
            root.Add(lang);

            // ── Current status ──
            _status = new Label { style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, marginTop = 10 } };
            root.Add(_status);

            root.Add(new Label(L.Tr(
                "Free includes the full scan, all findings, the health report, and a daily allowance of AI Fix / Explain. " +
                "Pro unlocks unlimited one-click / batch auto-fix and a much larger monthly AI allowance.",
                "Free 含完整扫描、全部诊断、健康度报告，以及每日少量 AI 修复/解释额度；Pro 解锁无限一键/批量自动修复，以及大得多的每月 AI 额度。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, marginTop = 4, marginBottom = 8 }
            });

            // ── License key ──
            _keyField = new TextField(L.Tr("License key", "许可证密钥")) { value = LicenseSettings.Key };
            root.Add(_keyField);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };
            _activate = new Button(OnActivate) { text = L.Tr("Activate", "激活") };
            _activate.style.flexGrow = 1;
            row.Add(_activate);

            _deactivate = new Button(OnDeactivate) { text = L.Tr("Deactivate", "停用") };
            _deactivate.style.marginLeft = 6;
            row.Add(_deactivate);

            var validate = new Button(OnValidate) { text = L.Tr("Re-check", "复验") };
            validate.style.marginLeft = 6;
            row.Add(validate);
            root.Add(row);

            var buy = new Button(() => Application.OpenURL(LicenseSettings.BuyUrl))
            {
                text = L.Tr("Get Pro →", "获取 Pro →")
            };
            buy.style.marginTop = 6;
            root.Add(buy);

            _msg = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 8, opacity = 0.85f } };
            root.Add(_msg);

            // ── Advanced: custom validation endpoint ──
            var adv = new Foldout { text = L.Tr("Advanced", "高级"), value = false };
            adv.style.marginTop = 12;
            var ep = new TextField(L.Tr("License endpoint", "校验端点")) { value = LicenseSettings.Endpoint };
            ep.RegisterValueChangedCallback(e => LicenseSettings.Endpoint = e.newValue);
            adv.Add(ep);
            adv.Add(new Label(L.Tr(
                "The license-validation proxy URL. Leave default unless self-hosting.",
                "许可证校验代理地址。除非自建，否则保持默认。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.55f, fontSize = 10 }
            });
            root.Add(adv);

            root.Add(new Label(L.Tr(
                "The key is stored only in local EditorPrefs. Validation only sends your key — never your code or assets.",
                "密钥仅存于本机 EditorPrefs。校验只发送密钥本身，绝不上传你的代码或资产。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.5f, fontSize = 10, marginTop = 12 }
            });

            Refresh();
        }

        private void Refresh()
        {
            if (_status == null) return;
            bool pro = LicenseService.IsPro;
            _status.text = (pro ? "● " : "○ ") + L.Tr("Status: ", "状态：") + LicenseService.StatusLine();
            _status.style.color = pro ? new Color(0.40f, 0.80f, 0.45f) : new Color(0.75f, 0.75f, 0.75f);
            _deactivate.SetEnabled(!string.IsNullOrEmpty(LicenseSettings.Key));
        }

        private void OnActivate()
        {
            _msg.text = L.Tr("Activating…", "激活中…");
            _activate.SetEnabled(false);
            LicenseService.Activate(_keyField.value, (ok, m) =>
            {
                _activate.SetEnabled(true);
                _msg.text = m;
                Refresh();
            });
        }

        private void OnValidate()
        {
            _msg.text = L.Tr("Checking…", "复验中…");
            LicenseService.Validate((ok, m) => { _msg.text = m; Refresh(); });
        }

        private void OnDeactivate()
        {
            if (!EditorUtility.DisplayDialog(
                    L.Tr("Deactivate", "停用"),
                    L.Tr("Remove the license from this machine? You can re-activate later.",
                         "从本机移除许可证？之后可重新激活。"),
                    L.Tr("Deactivate", "停用"), L.Tr("Cancel", "取消")))
                return;

            LicenseService.Deactivate((ok, m) =>
            {
                _msg.text = m;
                _keyField.value = "";
                Refresh();
            });
        }
    }
}
