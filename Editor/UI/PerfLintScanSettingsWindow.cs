using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>Scan settings: ignored path list (one fragment per line).</summary>
    public sealed class PerfLintScanSettingsWindow : EditorWindow
    {
        public static void Open()
        {
            var w = GetWindow<PerfLintScanSettingsWindow>(true, "PerfLint · " + L.Tr("Scan Settings", "扫描设置"));
            w.minSize = new Vector2(440, 280);
            w.Show();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            root.Add(new Label(L.Tr(
                "Ignored paths — one path fragment per line. Any asset whose path contains a fragment is skipped by all scanners (third-party folders, log wrappers, etc.). Default includes /Plugins/.",
                "忽略路径——每行一个路径片段。资源路径包含任一片段即被所有扫描器忽略（第三方目录、日志封装等）。默认含 /Plugins/。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
            });

            var field = new TextField { multiline = true, value = IgnoreSettings.Raw };
            field.style.minHeight = 140;
            field.style.whiteSpace = WhiteSpace.Normal;
            field.RegisterValueChangedCallback(e => IgnoreSettings.Raw = e.newValue);
            root.Add(field);

            var reset = new Button(() =>
            {
                IgnoreSettings.Raw = IgnoreSettings.Default;
                field.value = IgnoreSettings.Default;
            })
            { text = L.Tr("Reset to default (/Plugins/)", "恢复默认（/Plugins/）") };
            reset.style.marginTop = 6;
            root.Add(reset);

            root.Add(new Label(L.Tr(
                "Re-scan to apply.",
                "改完后重新扫描生效。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.55f, fontSize = 10, marginTop = 8 }
            });
        }
    }
}
