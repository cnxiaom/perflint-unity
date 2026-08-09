using System;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// Discovery / help for running PerfLint from the terminal: the Unity Pipeline commands
    /// (<c>unity command perflint_*</c>, against the open editor) and the headless CI entry points.
    /// Detects whether the Pipeline package is installed and shows the matching guidance. Opened from
    /// the main window's "CLI" toolbar button — so people who want to automate discover it, without
    /// nagging the GUI-only crowd.
    /// </summary>
    public sealed class PerfLintCliHelpWindow : EditorWindow
    {
        const string DocsUrl = "https://perflint.dev/docs/#ci";

        public static void Open()
        {
            var w = GetWindow<PerfLintCliHelpWindow>(true, "PerfLint · " + L.Tr("CLI & CI", "命令行 & CI"));
            w.minSize = new Vector2(560, 400);
            w.Show();
        }

        // Our Pipeline command assembly (PerfLint.Editor.Pipeline) compiles only when com.unity.pipeline is
        // present (PERFLINT_PIPELINE). Reflect for it instead of a #if — this window lives in the main assembly.
        static bool PipelineCommandsAvailable() =>
            Type.GetType("PerfLint.Ci.Pipeline.PerfLintPipelineCommands, PerfLint.Editor.Pipeline") != null;

        private void CreateGUI()
        {
            var root = rootVisualElement;
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingBottom = 10;

            // Everything scrolls except the guide button. The "Pipeline not installed" branch is two paragraphs
            // longer than the ready one, and this window opens at a fixed 400 px — without this, the branch that
            // needs the most reading is the one that gets cut off.
            var body = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            root.Add(body);

            body.Add(Title(L.Tr("Run PerfLint from the command line", "从命令行运行 PerfLint")));
            body.Add(Body(L.Tr(
                "For automation, CI, or driving PerfLint from an AI agent. Scanning and the health gate are free; applying fixes needs Pro. Nothing is uploaded.",
                "用于自动化、CI，或让 AI agent 驱动 PerfLint。扫描与健康门禁免费；应用修复需 Pro。不上传任何内容。")));

            // ── Section 1: Pipeline commands, against the OPEN editor ──
            body.Add(Header(L.Tr("In your open editor — the simplest way", "在打开的编辑器里 —— 最简单的方式")));
            if (PipelineCommandsAvailable())
            {
                body.Add(Body(L.Tr(
                    "Ready. Run these in a terminal — no editor path, no -batchmode, no -projectPath, and no need to close the editor:",
                    "已就绪。在终端里直接跑——无需 Unity 路径、无需 -batchmode、无需 -projectPath、不用关编辑器：")));
                body.Add(CommandRow("unity command perflint_scan"));
                body.Add(CommandRow("unity command perflint_gate --min_score 60"));
                body.Add(CommandRow("unity command perflint_fix"));
                body.Add(Hint(L.Tr(
                    "Add --dry_run to perflint_fix to preview. Run `unity command` with no arguments to list every command.",
                    "给 perflint_fix 加 --dry_run 可预览。跑 `unity command`（不带参数）会列出全部命令。")));
            }
            else
            {
                body.Add(Body(L.Tr(
                    "To enable `unity command perflint_*`, install Unity's Pipeline package (requires Unity 6) plus the Unity CLI. Then `unity command` lists PerfLint's commands automatically — nothing to configure on our side.",
                    "要启用 `unity command perflint_*`，安装 Unity 的 Pipeline 包（需 Unity 6）和 Unity CLI。装好后 `unity command` 会自动列出 PerfLint 的命令——我方无需任何配置。")));
                body.Add(CommandRow("com.unity.pipeline"));
                body.Add(Hint(L.Tr("Add that package via Package Manager ▸ Add package by name.", "在 Package Manager ▸ Add package by name 里添加该包。")));
            }

            // ── Section 2: Headless CI (no editor running) ──
            body.Add(Header(L.Tr("Headless CI — no running editor", "无头 CI —— 无需运行编辑器")));
            body.Add(Body(L.Tr(
                "In a build script, fail the build on a health regression (or auto-apply the safe fixes with RunFix):",
                "在构建脚本里，健康度回退就让 build 失败（或用 RunFix 自动应用安全修复）：")));
            body.Add(CommandRow("Unity -batchmode -projectPath . -executeMethod PerfLint.Ci.PerfLintCli.RunGate -perflintMinScore 60 -logFile -"));

            // Outside the scroll and aligned left rather than stretched across the window: it is a link out, not
            // the thing this screen is for. Full-width made it the loudest element on a page whose whole job is
            // the four commands above it.
            var docs = PerfLintStyle.Secondary(
                L.Tr("Open the full CLI & CI guide", "打开完整 CLI & CI 指南"),
                () => Application.OpenURL(DocsUrl));
            docs.style.marginTop = 10;
            docs.style.flexShrink = 0;
            docs.style.alignSelf = Align.FlexStart;
            root.Add(docs);
        }

        // ── small UI helpers ──
        //
        // Colours rather than opacities, and the shared palette rather than this window's own greys: opacity fades
        // text towards whatever is behind it, which on the light skin means fading it towards a white page.
        static Label Title(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, marginBottom = 4,
                    whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink } };

        static Label Header(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 12, marginBottom = 4,
                    whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink } };

        static Label Body(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6, color = PerfLintStyle.Dim } };

        static Label Hint(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, fontSize = 10,
                    marginTop = 4, color = PerfLintStyle.Dimmer } };

        /// <summary>
        /// One command, selectable, with a Copy button.
        ///
        /// The field WRAPS. It used to be single-line, which is fine for `unity command perflint_scan` and wrong
        /// for the one command nobody can retype from memory: the headless CI line is 104 characters and was cut
        /// off mid-flag at "…PerfLintCli.RunGate -p", so the only way to read the argument you were being told to
        /// set was to click into the box and scroll it. Copy still hands over the whole string either way — but a
        /// command you cannot READ is one you cannot check before pasting it into a build script.
        ///
        /// Still a TextField rather than a Label: it is what makes the text selectable on 2021.3, where Label
        /// selection does not exist yet.
        /// </summary>
        static VisualElement CommandRow(string cmd)
        {
            var row = new VisualElement
            { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };

            var code = new TextField { value = cmd, isReadOnly = true, multiline = true };
            code.style.flexGrow = 1;
            code.style.flexShrink = 1;
            code.style.minWidth = 0; // let a long command shrink instead of pushing Copy off-screen
            code.style.marginRight = 6;
            code.style.whiteSpace = WhiteSpace.Normal;
            row.Add(code);

            var copy = PerfLintStyle.AsCompact(new Button { text = L.Tr("Copy", "复制") });
            copy.style.flexShrink = 0; // keep the Copy button visible no matter how long the command is
            copy.style.alignSelf = Align.FlexStart;
            copy.clicked += () =>
            {
                EditorGUIUtility.systemCopyBuffer = cmd;
                copy.text = L.Tr("Copied!", "已复制！");
                copy.schedule.Execute(() => copy.text = L.Tr("Copy", "复制")).StartingIn(1200);
            };
            row.Add(copy);
            return row;
        }
    }
}
