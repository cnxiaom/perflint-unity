using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// Scan settings: the ignored-path list, one path fragment per line.
    ///
    /// One screen, one setting, and it still had three of the four drift markers the shared layer exists to stop:
    /// a stock editor button, a 0.55 opacity for the closing line (which fades dark text towards a white page on
    /// the light skin), and no stylesheet at all — so the button had no hover.
    /// </summary>
    public sealed class PerfLintScanSettingsWindow : EditorWindow
    {
        public static void Open()
        {
            var w = GetWindow<PerfLintScanSettingsWindow>(true, "PerfLint · " + L.Tr("Scan Settings", "扫描设置"));
            w.minSize = new Vector2(440, 300);
            w.Show();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 12;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingBottom = 10;

            root.Add(new Label(L.Tr("Ignored paths", "忽略路径"))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, marginBottom = 4,
                          whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink }
            });
            root.Add(new Label(L.Tr(
                "One path fragment per line. Any asset whose path contains a fragment is skipped by every scanner — third-party folders, generated content, log wrappers. The default is /Plugins/.",
                "每行一个路径片段。资源路径包含任一片段即被所有扫描器跳过——第三方目录、生成内容、日志封装等。默认是 /Plugins/。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 8, color = PerfLintStyle.Dim }
            });

            var field = new TextField { multiline = true, value = IgnoreSettings.Raw };
            field.style.flexGrow = 1;
            field.style.minHeight = 140;
            field.style.whiteSpace = WhiteSpace.Normal;
            field.RegisterValueChangedCallback(e => IgnoreSettings.Raw = e.newValue);
            root.Add(field);

            // Left-aligned rather than stretched: it is an undo, not the thing this screen is for. Secondary for the
            // same reason — the screen's real action is typing in the box above it, which no button can be.
            var reset = PerfLintStyle.Secondary(L.Tr("Reset to default (/Plugins/)", "恢复默认（/Plugins/）"), () =>
            {
                IgnoreSettings.Raw = IgnoreSettings.Default;
                field.value = IgnoreSettings.Default;
            });
            reset.style.marginTop = 8;
            reset.style.alignSelf = Align.FlexStart;
            reset.style.flexShrink = 0;
            root.Add(reset);

            root.Add(new Label(L.Tr("Re-scan to apply.", "改完后重新扫描生效。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, fontSize = 10,
                          marginTop = 8, color = PerfLintStyle.Dimmer }
            });
        }
    }
}
