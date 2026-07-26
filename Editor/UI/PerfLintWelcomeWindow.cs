using System;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// First-run onboarding. A UPM package installs silently — nothing opens, nothing appears in the
    /// Project window — so a new user's only clue that PerfLint exists is a menu they have to go looking
    /// for. This window opens once, right after the package first compiles, and answers the two questions
    /// that decide whether the tool ever gets used: <b>where do I find it</b> and <b>what does each part do</b>.
    ///
    /// Deliberately not a wizard: no steps, no state, nothing to configure. Reading it is optional and it
    /// never reappears on its own (see <see cref="PerfLintFirstRun"/>); the menu item reopens it on demand.
    /// </summary>
    public sealed class PerfLintWelcomeWindow : EditorWindow
    {
        const string DocsUrl = "https://perflint.dev/docs";

        [MenuItem("Tools/PerfLint/Getting Started", priority = 2000)] // priority gap => separated from the working tools above
        public static void Open()
        {
            var w = GetWindow<PerfLintWelcomeWindow>(true, "PerfLint · " + L.Tr("Getting Started", "新手引导"));
            w.minSize = new Vector2(600, 520);
            w.Show();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 12;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingBottom = 10;

            // Everything scrolls except the footer buttons — on a laptop-height utility window the primary
            // "Open PerfLint" action must stay reachable without scrolling to the bottom.
            var scroll = new ScrollView { style = { flexGrow = 1 } };
            root.Add(scroll);

            scroll.Add(Title(L.Tr("Welcome to PerfLint", "欢迎使用 PerfLint")));
            scroll.Add(Body(L.Tr(
                "A senior tech lead inside your editor. One scan finds what's slowing down, bloating, or blocking your project — with the exact location, the impact in plain language, and a fix. Detection is a deterministic rule engine: offline, no account, no tokens, same result every run.",
                "装进编辑器里的资深技术经理。一次扫描找出拖慢、撑大、卡住你工程的问题——给出准确位置、大白话影响，以及修复方案。检测由确定性规则引擎完成：离线、免注册、零 token，每次跑结果一致。")));

            // ── 1. Where to find it ──────────────────────────────
            scroll.Add(Header(L.Tr("Where to find it", "在哪里找到它")));
            scroll.Add(MenuRow(
                "Tools ▸ PerfLint ▸ Scan Project", "Ctrl/Cmd + Shift + L",
                L.Tr("The main panel — health score and every finding. Start here.",
                     "主面板——健康分和全部问题清单。从这里开始。")));
            scroll.Add(MenuRow(
                "Tools ▸ PerfLint ▸ Runtime Profiler", "Ctrl/Cmd + Shift + K",
                L.Tr("Play Mode profiling: stutter, per-frame GC, and CPU hotspots, down to the script.",
                     "Play Mode 运行时分析：卡顿、每帧 GC、CPU 热点，定位到具体脚本。")));
            scroll.Add(MenuRow(
                "Tools ▸ PerfLint ▸ Shader Variant Stripping", null,
                L.Tr("Record the shader variants your game actually renders, then warm them up at startup.",
                     "录制游戏真正用到的着色器变体，并在启动时预热。")));
            scroll.Add(Hint(L.Tr(
                "This guide lives at Tools ▸ PerfLint ▸ Getting Started — it won't open by itself again.",
                "本引导可在 Tools ▸ PerfLint ▸ Getting Started 重新打开——它不会再自动弹出。")));

            // ── 2. What a scan gives you ─────────────────────────
            scroll.Add(Header(L.Tr("What a scan gives you", "扫描会得到什么")));
            scroll.Add(Body(L.Tr(
                "Click Scan Project and, in seconds, you get a project health score (0–100) and findings grouped by domain. Every finding carries a severity, a Locate button that jumps to the exact asset or line, what it costs you, and how to fix it — the safe ones in one click.",
                "点击 Scan Project，几秒后得到工程健康分（0–100）和按域分组的问题清单。每条问题都带严重级别、可跳到具体资源或代码行的 Locate 按钮、它的实际代价，以及修复方法——安全的那些可一键修复。")));
            scroll.Add(Bullet(DotPerformance, L.Tr("Performance", "性能"), L.Tr(
                "Uncompressed or oversized textures, needless Read/Write, per-frame GC in Update (GetComponent / Camera.main / LINQ / string concat), Debug.Log shipping in builds, batching breakers, mesh and audio import settings.",
                "未压缩/超大纹理、多余的 Read/Write、Update 里的每帧 GC（GetComponent / Camera.main / LINQ / 字符串拼接）、进 build 的 Debug.Log、破坏合批的用法、网格与音频导入设置。")));
            scroll.Add(Bullet(DotAssets, L.Tr("Assets", "资产"), L.Tr(
                "Duplicate assets (content-hashed), the same asset double-packed across AssetBundles / Addressables, unreferenced assets dragged into the build, shader-variant blowups.",
                "重复资源（按内容哈希）、被 AssetBundle / Addressables 重复打包的同一份资源、被拖进 build 的无引用资源、着色器变体爆炸。")));
            scroll.Add(Bullet(DotMigration, L.Tr("Migration", "迁移"), L.Tr(
                "Deprecated or removed APIs located to the line, old vs. new Input System mixing, package-version compatibility with your Unity, and Unity 6 upgrade blockers.",
                "定位到行的废弃/移除 API、新旧 Input System 混用、包版本与你的 Unity 是否兼容、Unity 6 升级阻塞项。")));
            scroll.Add(Bullet(DotSettings, L.Tr("Project Settings", "工程设置"), L.Tr(
                "Build, quality and player settings that quietly cost you frames or megabytes.",
                "悄悄吃掉帧率或体积的 Build / Quality / Player 设置。")));

            // ── 3. The main panel's toolbar, button by button ─────
            scroll.Add(Header(L.Tr("The main panel, button by button", "主面板工具栏逐个说明")));
            scroll.Add(ToolRow("Scan Project", L.Tr(
                "Runs the full scan. Re-running after an edit is incremental — only what changed is re-scanned.",
                "跑完整扫描。改动后重跑是增量的——只重扫变化的部分。")));
            scroll.Add(ToolRow("Fix All", L.Tr(
                "Applies every safe, deterministic fix in one batch (import settings and the like), with a preview first and Edit ▸ Undo after. Pro.",
                "一次性应用全部安全的确定性修复（导入设置等），先预览、事后可 Edit ▸ Undo 撤销。Pro 功能。")));
            scroll.Add(ToolRow(L.Tr("Export CSV / Export Report", "Export CSV / Export Report"), L.Tr(
                "The raw findings as CSV, or a self-contained shareable HTML health report. Both free, both offline.",
                "导出原始问题清单 CSV，或自包含、可分享的 HTML 健康报告。都免费、都离线。")));
            scroll.Add(ToolRow(L.Tr("Ignore", "Ignore"), L.Tr(
                "Exclude paths or whole rules from scans — third-party plugins, generated content, rules you've decided against.",
                "把某些路径或整条规则排除出扫描——第三方插件、生成内容、你已决定不改的规则。")));
            scroll.Add(ToolRow(L.Tr("Runtime", "Runtime"), L.Tr(
                "Opens the Runtime Profiler. A static scan can't see stutter; that panel measures it while your game runs.",
                "打开运行时分析面板。静态扫描看不见卡顿，那个面板在游戏运行时实测。")));
            scroll.Add(ToolRow("LLM", L.Tr(
                "Settings for the two optional AI helpers: Explain (\"why does this matter here?\") and AI Fix (writes the code change, compiles it, and rolls it back automatically if the build breaks). Works out of the box on your plan's credits, or plug in your own Claude / DeepSeek key.",
                "两个可选 AI 助手的设置：Explain（“这条在我工程里为什么要紧？”）和 AI Fix（写好代码改动、触发编译，编译不过自动回滚）。开箱即用走套餐额度，也可填自己的 Claude / DeepSeek key。")));
            scroll.Add(ToolRow("CLI", L.Tr(
                "How to run PerfLint from a terminal or CI — and how to hand the whole diagnosis to your AI agent over Unity's MCP server.",
                "如何从终端或 CI 运行 PerfLint——以及如何经 Unity 的 MCP server 把整套诊断交给你的 AI agent。")));
            scroll.Add(ToolRow(L.Tr("Free / Pro ●", "Free / Pro ●"), L.Tr(
                "Your license state. Click to activate a key or manage the machine's activation.",
                "许可证状态。点击可激活 key 或管理本机激活。")));

            // ── 4. Privacy — the trust anchor, stated before anything is clicked ──
            scroll.Add(Header(L.Tr("Your project stays on your machine", "你的工程不会离开本机")));
            scroll.Add(Body(L.Tr(
                "Scanning is entirely local — your code and art assets are never uploaded, and no account is needed to scan. The only thing that ever leaves your machine is the finding text or the single snippet you pick when you use Explain / AI Fix, and you're told what's being sent each time. Zero telemetry.",
                "扫描全程本地——代码与美术资产永不上传，扫描也无需注册账号。唯一会离开本机的，是你主动使用 Explain / AI Fix 时的那条问题描述或你选中的那段代码，且每次都会告知发送内容。零遥测。")));

            // ── 5. Free vs Pro (no prices in-plugin: the landing page is the single source of truth) ──
            scroll.Add(Header(L.Tr("Free and Pro", "Free 与 Pro")));
            scroll.Add(Bullet(DotFree, L.Tr("Free, forever", "免费，永久"), L.Tr(
                "The full scan, every finding, the health score, the shareable HTML report, written fix guidance, and a daily allowance of AI Fix / Explain.",
                "完整扫描、全部问题、健康分、可分享 HTML 报告、文字修复指引，以及每日一定次数的 AI Fix / Explain。")));
            scroll.Add(Bullet(DotPro, "Pro", L.Tr(
                "Applying fixes at project scale: one-click and batch fixes, optimize-by-goal, duplicate-asset de-duplication, the whole-file Migration Assistant, a much larger AI allowance, and bring-your-own API key.",
                "在整个工程规模上执行修复：一键与批量修复、按目标优化、重复资源去重、整文件迁移助手、大得多的 AI 额度，以及自带 API key。")));

            // ── Footer: fixed, always reachable ──────────────────
            var footer = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginTop = 10, flexShrink = 0 }
            };

            var open = new Button(() => { PerfLintWindow.OpenWindow(); Close(); })
            {
                text = L.Tr("Open PerfLint", "打开 PerfLint")
            };
            open.style.height = 28;
            open.style.flexGrow = 1;
            open.style.backgroundColor = new Color(0.20f, 0.45f, 0.85f); // same accent as the panel's primary Scan button
            open.style.color = Color.white;
            open.style.unityFontStyleAndWeight = FontStyle.Bold;
            footer.Add(open);

            var docs = new Button(() => Application.OpenURL(DocsUrl)) { text = L.Tr("Docs", "文档") };
            docs.style.height = 28;
            docs.style.marginLeft = 6;
            footer.Add(docs);

            root.Add(footer);
        }

        // ── small UI helpers (same shape as PerfLintCliHelpWindow) ──
        static Label Title(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, marginBottom = 6, whiteSpace = WhiteSpace.Normal } };

        static Label Header(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 14, marginBottom = 6, whiteSpace = WhiteSpace.Normal } };

        static Label Body(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6, opacity = 0.9f } };

        static Label Hint(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.6f, fontSize = 10, marginTop = 4 } };

        /// <summary>Menu path + optional shortcut chip on one line, with the "what it's for" line under it.</summary>
        static VisualElement MenuRow(string menuPath, string shortcut, string desc)
        {
            var box = new VisualElement { style = { marginBottom = 8 } };

            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap } };
            top.Add(new Label(menuPath)
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
            });
            if (!string.IsNullOrEmpty(shortcut)) top.Add(Chip(shortcut));
            box.Add(top);

            box.Add(new Label(desc) { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, fontSize = 11, marginTop = 1 } });
            return box;
        }

        /// <summary>Keyboard-shortcut pill.</summary>
        static Label Chip(string t)
        {
            var l = new Label(t)
            {
                style =
                {
                    fontSize = 10, marginLeft = 8,
                    paddingLeft = 6, paddingRight = 6, paddingTop = 1, paddingBottom = 1,
                    backgroundColor = new Color(1f, 1f, 1f, 0.07f),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    opacity = 0.8f, flexShrink = 0
                }
            };
            return l;
        }

        // Bullet colors, one per section. Drawn, not typed — see Bullet().
        static readonly Color DotPerformance = new Color(0.30f, 0.60f, 0.95f);
        static readonly Color DotAssets = new Color(0.65f, 0.50f, 0.90f);
        static readonly Color DotMigration = new Color(0.95f, 0.70f, 0.30f);
        static readonly Color DotSettings = new Color(0.45f, 0.75f, 0.70f);
        static readonly Color DotFree = new Color(0.40f, 0.80f, 0.45f); // the green the main panel's Pro pill uses
        static readonly Color DotPro = new Color(0.95f, 0.75f, 0.25f);

        /// <summary>
        /// Colored dot + bold lead-in + wrapped description, laid out as a hanging indent.
        ///
        /// The dot is a drawn element, not an emoji character: the editor font on 2021/2022 carries no emoji
        /// glyphs, so the rocket / gear markers that used to be here came out as tofu boxes — and any emoji
        /// written with a U+FE0F variation selector renders as *two* boxes. Unity 6 has the glyphs and looks
        /// perfect, which is precisely how this ships broken unnoticed (observed 2026-07-25 on 2022.3).
        /// A VisualElement can't miss a glyph. Enforced by EditorGlyphSafetyTests.
        /// </summary>
        static VisualElement Bullet(Color dot, string name, string desc)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6 } };
            row.Add(new VisualElement
            {
                style =
                {
                    width = 8, height = 8, flexShrink = 0,
                    marginRight = 8, marginTop = 5, // marginTop aligns the dot with the middle of the bold lead-in
                    backgroundColor = dot,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4
                }
            });

            var col = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
            col.Add(new Label(name) { style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal } });
            col.Add(new Label(desc) { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.75f, fontSize = 11, marginTop = 1 } });
            row.Add(col);
            return row;
        }

        /// <summary>One toolbar button and what it does.</summary>
        static VisualElement ToolRow(string button, string desc)
        {
            var box = new VisualElement { style = { marginBottom = 6 } };
            box.Add(new Label(button) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, whiteSpace = WhiteSpace.Normal } });
            box.Add(new Label(desc) { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.75f, fontSize = 11, marginTop = 1 } });
            return box;
        }
    }

    /// <summary>
    /// Decides whether the onboarding window opens on its own, and when. Top-level on purpose — Unity's
    /// [InitializeOnLoad] scan is only dependable for top-level types.
    ///
    /// Three rules, each there for a reason:
    ///  · <b>Once per machine, not per project.</b> "Where do I find it" is machine-level knowledge; someone who
    ///    installs PerfLint into their fifth project already knows the menu path and doesn't want the popup again.
    ///    Hence EditorPrefs (machine-global), keyed by a guide version we only bump on a real rewrite.
    ///  · <b>Never in batch mode.</b> CI, the EditMode test runs and headless `-executeMethod` entry points must
    ///    never have a window thrown at them.
    ///  · <b>Never for someone who already scanned this project.</b> Existing users updating the package are not
    ///    first-time users; a persisted report means they've been here.
    ///
    /// <b>The flag is only ever written on the path that actually shows the guide.</b> Every suppression above
    /// leaves it untouched, and that asymmetry is the whole point: the flag is machine-global while both
    /// suppression signals are per-session or per-project, so writing it from a suppression path spends the one
    /// and only showing on a project that was never going to see it. Measured, not theorised (2026-07-25): a dev
    /// project carrying a saved report loaded the new build at 21:05 and quietly marked the guide seen; the
    /// brand-new project opened 50 minutes later to test the feature got nothing at all. Same hazard with batch
    /// mode — one headless CI run would eat the user's first interactive launch.
    ///
    /// The open itself waits for an idle editor — the package's very first load is usually mid-import/compile,
    /// and a window opened in that window of time can be destroyed by the reload that follows.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfLintFirstRun
    {
        // Machine-global (EditorPrefs), storing the guide version the user has seen. Bump GuideVersion only when
        // the content changes enough to be worth re-showing to everyone — a casual bump re-nags every user.
        private const string SeenKey = "PerfLint.Welcome.SeenVersion";
        private const string GuideVersion = "1";

        /// <summary>
        /// Pure decision (unit-tested): the whole "should the popup happen" policy, with no editor state involved.
        /// A plain bool on purpose — there is no "suppress and mark seen" outcome, because marking seen is exactly
        /// what a suppression must never do (see the type comment).
        /// </summary>
        internal static bool ShouldShow(string seenVersion, string guideVersion, bool batchMode, bool projectAlreadyScanned)
        {
            if (string.Equals(seenVersion, guideVersion, StringComparison.Ordinal)) return false; // already shown on this machine
            if (batchMode) return false;             // headless: no window to show it in
            if (projectAlreadyScanned) return false; // not a first-time user *here* — but other projects still get their turn
            return true;
        }

        static PerfLintFirstRun()
        {
            // Nothing here touches the UI or the disk: [InitializeOnLoad] runs while the domain is still coming
            // up. The real work happens on the first update tick, once the editor can be asked anything.
            EditorApplication.update += Tick;
        }

        private static bool _decided;

        private static void Tick()
        {
            if (!_decided)
            {
                _decided = true;
                // Disk is touched once, here — not every tick.
                if (!ShouldShow(EditorPrefs.GetString(SeenKey, string.Empty), GuideVersion,
                                Application.isBatchMode, ScanResultStore.Exists()))
                {
                    Stop(); // suppressed — and deliberately WITHOUT marking it seen
                    return;
                }
            }

            // Wait for a settled editor: the package is typically still importing/compiling on its first load,
            // and play mode is never a moment to interrupt with a guide.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            Stop();
            MarkSeen(); // before opening: if the user closes it instantly (or the editor dies), it still doesn't repeat forever
            try { PerfLintWelcomeWindow.Open(); }
            catch (Exception ex) { Debug.LogWarning("[PerfLint] Getting Started window could not open: " + ex.Message); }
        }

        private static void Stop() => EditorApplication.update -= Tick;

        private static void MarkSeen() => EditorPrefs.SetString(SeenKey, GuideVersion);
    }
}
