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
        const string DocsUrl = "https://perflint.dev/docs#ci";

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
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            root.Add(Title(L.Tr("Run PerfLint from the command line", "从命令行运行 PerfLint")));
            root.Add(Body(L.Tr(
                "For automation, CI, or driving PerfLint from an AI agent. Scanning and the health gate are free; applying fixes needs Pro. Nothing is uploaded.",
                "用于自动化、CI，或让 AI agent 驱动 PerfLint。扫描与健康门禁免费；应用修复需 Pro。不上传任何内容。")));

            // ── Section 1: Pipeline commands, against the OPEN editor ──
            root.Add(Header(L.Tr("In your open editor — the simplest way", "在打开的编辑器里 —— 最简单的方式")));
            if (PipelineCommandsAvailable())
            {
                root.Add(Body(L.Tr(
                    "Ready. Run these in a terminal — no editor path, no -batchmode, no -projectPath, and no need to close the editor:",
                    "已就绪。在终端里直接跑——无需 Unity 路径、无需 -batchmode、无需 -projectPath、不用关编辑器：")));
                root.Add(CommandRow("unity command perflint_scan"));
                root.Add(CommandRow("unity command perflint_gate --min_score 60"));
                root.Add(CommandRow("unity command perflint_fix"));
                root.Add(Hint(L.Tr(
                    "Add --dry_run to perflint_fix to preview. Run `unity command` with no arguments to list every command.",
                    "给 perflint_fix 加 --dry_run 可预览。跑 `unity command`（不带参数）会列出全部命令。")));
            }
            else
            {
                root.Add(Body(L.Tr(
                    "To enable `unity command perflint_*`, install Unity's Pipeline package (requires Unity 6) plus the Unity CLI. Then `unity command` lists PerfLint's commands automatically — nothing to configure on our side.",
                    "要启用 `unity command perflint_*`，安装 Unity 的 Pipeline 包（需 Unity 6）和 Unity CLI。装好后 `unity command` 会自动列出 PerfLint 的命令——我方无需任何配置。")));
                root.Add(CommandRow("com.unity.pipeline"));
                root.Add(Hint(L.Tr("Add that package via Package Manager ▸ Add package by name.", "在 Package Manager ▸ Add package by name 里添加该包。")));
            }

            // ── Section 2: Headless CI (no editor running) ──
            root.Add(Header(L.Tr("Headless CI — no running editor", "无头 CI —— 无需运行编辑器")));
            root.Add(Body(L.Tr(
                "In a build script, fail the build on a health regression (or auto-apply the safe fixes with RunFix):",
                "在构建脚本里，健康度回退就让 build 失败（或用 RunFix 自动应用安全修复）：")));
            root.Add(CommandRow("Unity -batchmode -projectPath . -executeMethod PerfLint.Ci.PerfLintCli.RunGate -perflintMinScore 60 -logFile -"));

            var docs = new Button(() => Application.OpenURL(DocsUrl))
            {
                text = L.Tr("Open the full CLI & CI guide", "打开完整 CLI & CI 指南")
            };
            docs.style.marginTop = 14;
            docs.style.height = 26;
            root.Add(docs);
        }

        // ── small UI helpers ──
        static Label Title(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14, marginBottom = 4, whiteSpace = WhiteSpace.Normal } };

        static Label Header(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 12, marginBottom = 4, whiteSpace = WhiteSpace.Normal } };

        static Label Body(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6 } };

        static Label Hint(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.6f, fontSize = 10, marginTop = 4 } };

        static VisualElement CommandRow(string cmd)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };

            var code = new TextField { value = cmd, isReadOnly = true };
            code.style.flexGrow = 1;
            code.style.flexShrink = 1;
            code.style.minWidth = 0; // let a long command (the batchmode line) shrink instead of pushing Copy off-screen
            code.style.marginRight = 6;
            row.Add(code);

            var copy = new Button { text = L.Tr("Copy", "复制") };
            copy.style.height = 22;
            copy.style.flexShrink = 0; // keep the Copy button visible no matter how long the command is
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
