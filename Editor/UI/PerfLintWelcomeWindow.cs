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
        const string DocsUrl = "https://perflint.dev/docs/";

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
            PerfLintStyle.Apply(root);
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
            //
            // The Autopilot leads, and not by seniority: it is the menu's first item (priority 1) and the main
            // panel's own header points at it. A guide that opened with the instrument panel would be sending a
            // first-time user to the screen the Autopilot exists to spare them.
            scroll.Add(Header(L.Tr("Where to find it", "在哪里找到它")));
            scroll.Add(MenuRow(
                "Tools ▸ PerfLint ▸ Autopilot", null,
                L.Tr("Where you are, what to fix this round, and whether it actually worked. Start here — it decides what to do next so you don't have to read a list to find out.",
                     "你现在在哪、这一轮修什么、做完到底有没有用。从这里开始——它替你定下一步，不用先读完一整张清单。")));
            scroll.Add(MenuRow(
                // No "health score" in this description: the panel stopped showing one. The number is real and still
                // computed, but its only outlets are the exported HTML report, CLI stdout and the Pipeline JSON —
                // grep _ring/scoreLabel/HealthScore in PerfLintWindow returns nothing. Sending a first-time user to
                // a panel to look at something that is not on it is the cross-reference trap in CLAUDE.md, and this
                // is the first screen they ever see.
                "Tools ▸ PerfLint ▸ Scan Project", "Ctrl/Cmd + Alt + L",
                L.Tr("The full panel — every finding, how many of them apply in one click, and all the evidence behind the Autopilot's ranking.",
                     "完整面板——全部问题清单、其中有多少能一键修，以及 Autopilot 排序背后的全部证据。")));
            scroll.Add(MenuRow(
                "Tools ▸ PerfLint ▸ Runtime Profiler", "Ctrl/Cmd + Alt + M",
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
                "Click Scan Project and, in seconds, you get findings grouped by domain, with the count of what applies in one click up top. Every finding carries a severity, a Locate button that jumps to the exact asset or line, what it costs you, and how to fix it — the safe ones in one click. The 0–100 health score and A–F grade live in the HTML report you can export.",
                "点击 Scan Project，几秒后得到按域分组的问题清单，顶上标着其中有多少能一键修。每条问题都带严重级别、可跳到具体资源或代码行的 Locate 按钮、它的实际代价，以及修复方法——安全的那些可一键修复。0–100 的健康分与 A–F 等级在可导出的 HTML 报告里。")));
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

            // ── 3. The Autopilot — what the scan's list turns into ──
            //
            // Its three screens are named with the Autopilot's own tab titles, verbatim. A guide that paraphrases
            // the thing it is pointing at leaves the reader matching approximations against a live window.
            scroll.Add(Header(L.Tr("Autopilot: one thing at a time", "Autopilot：一次只做一件事")));
            scroll.Add(Body(L.Tr(
                "A scan gives you a list. The Autopilot turns it into a next step — ranked by what is actually limiting your project rather than by severity, and verified by measuring before and after. Three screens, in this order:",
                "扫描给你的是一张清单。Autopilot 把它变成「下一步做什么」——按「当前真正卡住你的是什么」排序，而不是按严重级别，并用前后两次测量来验证。三屏，按顺序：")));
            scroll.Add(Bullet(DotStep, L.Tr("1 · Where you are", "1 · 当前结论"), L.Tr(
                "The single item the evidence ranks first, with what it should buy you, its risk, and whether it can be undone. The rest is shown as a roadmap under it, not as a to-do list.",
                "证据排在第一位的那一项，附带预期改善、风险，以及能否撤销。其余的以路线图形式列在下面，而不是一张待办清单。")));
            scroll.Add(Bullet(DotStep, L.Tr("2 · This round", "2 · 本轮修复"), L.Tr(
                "Only the best-evidenced items, with the reversible ones applied in one click. Anything that changes how your game plays or looks is never applied for you — it is listed as a decision, with the trade-off spelled out.",
                "只放证据最充分的项，其中可逆的那些一键应用。任何会改变游戏玩法或观感的改动都不会替你执行——它们单列为「需要你决定」，并写清取舍。")));
            scroll.Add(Bullet(DotStep, L.Tr("3 · Did it work", "3 · 验证结果"), L.Tr(
                "Measure the scene, make the changes, measure again. A difference smaller than the drift PerfLint has measured on your own machine comes back as \"no measurable change\" rather than as a win.",
                "先测一次当前场景，改完再测一次。如果差异小于 PerfLint 在你这台机器上实测到的漂移，结论是「无可测量的变化」，而不是一场胜利。")));
            scroll.Add(Hint(L.Tr(
                "Ranking and measuring are free. Applying the fixes is the Pro part, and the round says which of its items that covers before you click anything.",
                "排序与测量免费。执行修复才是 Pro 的部分，而且本轮列表会在你点任何按钮之前先说明哪些项属于它。")));

            // ── 4. The main panel's toolbar, button by button ─────
            scroll.Add(Header(L.Tr("The main panel, button by button", "主面板工具栏逐个说明")));
            scroll.Add(ToolRow("Scan Project", L.Tr(
                "Runs the full scan. Re-running after an edit is incremental — only what changed is re-scanned.",
                "跑完整扫描。改动后重跑是增量的——只重扫变化的部分。")));
            scroll.Add(ToolRow("Fix All", L.Tr(
                "Applies every safe, deterministic fix in one batch (import settings and the like), with a preview first. Commit to version control before you run it — Edit ▸ Undo does not revert these. Pro.",
                "一次性应用全部安全的确定性修复（导入设置等），先预览。运行前请先提交版本控制——Edit ▸ Undo 撤销不了。Pro 功能。")));
            scroll.Add(ToolRow(L.Tr("Export CSV / Export Report", "Export CSV / Export Report"), L.Tr(
                "The raw findings as CSV, or a self-contained shareable HTML health report. Both free, both offline.",
                "导出原始问题清单 CSV，或自包含、可分享的 HTML 健康报告。都免费、都离线。")));
            scroll.Add(ToolRow(L.Tr("Ignore", "Ignore"), L.Tr(
                "Exclude paths or whole rules from scans — third-party plugins, generated content, rules you've decided against.",
                "把某些路径或整条规则排除出扫描——第三方插件、生成内容、你已决定不改的规则。")));
            // Listed here in the order the toolbar actually lays them out — Autopilot sits between Ignore and Runtime.
            scroll.Add(ToolRow(L.Tr("Autopilot", "Autopilot"), L.Tr(
                "Opens the guided loop described above. The same findings, ranked into one next step, with the before/after measurement that says whether it worked. The header's \"What should I do? →\" goes to the same place.",
                "打开上面那套引导流程。同一批问题，收敛成一个「下一步」，并用前后测量说明它到底有没有用。面板顶部的「该做什么？ →」通向同一处。")));
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

            // ── 5. Privacy — the trust anchor, stated before anything is clicked ──
            scroll.Add(Header(L.Tr("Your project stays on your machine", "你的工程不会离开本机")));
            scroll.Add(Body(L.Tr(
                "Scanning is entirely local — your code and art assets are never uploaded, and no account is needed to scan. The only thing that ever leaves your machine is the finding text or the single snippet you pick when you use Explain / AI Fix, and you're told what's being sent each time. Zero telemetry.",
                "扫描全程本地——代码与美术资产永不上传，扫描也无需注册账号。唯一会离开本机的，是你主动使用 Explain / AI Fix 时的那条问题描述或你选中的那段代码，且每次都会告知发送内容。零遥测。")));

            // ── 6. Free vs Pro (no prices in-plugin: the landing page is the single source of truth) ──
            scroll.Add(Header(L.Tr("Free and Pro", "Free 与 Pro")));
            scroll.Add(Bullet(DotFree, L.Tr("Free, forever", "免费，永久"), L.Tr(
                "The full scan, every finding, the shareable HTML report with its 0–100 health score, written fix guidance, and a daily allowance of AI Fix / Explain — or unlimited with your own API key.",
                "完整扫描、全部问题、带 0–100 健康分的可分享 HTML 报告、文字修复指引，以及每日一定次数的 AI Fix / Explain —— 填入自己的 API key 则不限量。")));
            scroll.Add(Bullet(DotPro, "Pro", L.Tr(
                "Applying fixes at project scale: one-click and batch fixes, optimize-by-goal, duplicate-asset de-duplication, the whole-file Migration Assistant, and a much larger AI allowance.",
                "在整个工程规模上执行修复：一键与批量修复、按目标优化、重复资源去重、整文件迁移助手，以及大得多的 AI 额度。")));

            // ── Footer: fixed, always reachable ──────────────────
            var footer = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginTop = 10, flexShrink = 0 }
            };

            // The one thing to do on this screen, in the product's primary. It used to be an accent blue picked to
            // match the Scan button, which was itself a blue picked here — two windows agreeing with each other and
            // with nothing else. Both now wear pl-primary, which is also the only way they get a hover at all.
            var open = PerfLintStyle.Primary(L.Tr("Open PerfLint", "打开 PerfLint"),
                () => { PerfLintWindow.OpenWindow(); Close(); });
            open.style.flexGrow = 1;
            footer.Add(open);

            var docs = PerfLintStyle.Secondary(L.Tr("Docs", "文档"), () => Application.OpenURL(DocsUrl));
            docs.style.marginLeft = 6;
            footer.Add(docs);

            root.Add(footer);
        }

        // ── small UI helpers (same type scale as the Autopilot) ──
        //
        // Three tints rather than three opacities. Opacity fades a label towards whatever is behind it, which on a
        // light skin means fading white text towards a white page; the palette states the colour it should be on
        // each skin instead.
        static Label Title(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, marginBottom = 6, whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink } };

        static Label Header(string t) => new Label(t)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 14, marginBottom = 6, whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink } };

        static Label Body(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6, color = PerfLintStyle.Dim } };

        static Label Hint(string t) => new Label(t)
        { style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, fontSize = 10, marginTop = 4, color = PerfLintStyle.Dimmer } };

        /// <summary>Menu path + optional shortcut chip on one line, with the "what it's for" line under it.</summary>
        static VisualElement MenuRow(string menuPath, string shortcut, string desc)
        {
            var box = new VisualElement { style = { marginBottom = 8 } };

            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap } };
            top.Add(new Label(menuPath)
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal, flexShrink = 1,
                          color = PerfLintStyle.Ink }
            });
            if (!string.IsNullOrEmpty(shortcut)) top.Add(PerfLintStyle.Chip(shortcut));
            box.Add(top);

            box.Add(new Label(desc) { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 1, color = PerfLintStyle.Dimmer } });
            return box;
        }

        // Bullet colors, one per section. Drawn, not typed — see Bullet(). Two of the six are states the rest of the
        // product already has a colour for (free = the good green, Pro = the amber it marks a caveat with), so they
        // take it rather than a second opinion of it; the four domain hues are this window's own and are stated per
        // skin, because a pastel picked on a dark editor has almost no contrast on a white page.
        // Properties, not static readonly fields: a field initialiser reads the skin once when the type is first
        // touched and keeps that answer for the rest of the domain, so a theme switch would leave four dots wrong.
        static Color DotPerformance => PerfLintStyle.Pro ? new Color(0.30f, 0.60f, 0.95f) : new Color(0.10f, 0.38f, 0.78f);
        static Color DotAssets => PerfLintStyle.Pro ? new Color(0.65f, 0.50f, 0.90f) : new Color(0.44f, 0.28f, 0.72f);
        static Color DotMigration => PerfLintStyle.Pro ? new Color(0.95f, 0.70f, 0.30f) : new Color(0.66f, 0.44f, 0.05f);
        static Color DotSettings => PerfLintStyle.Pro ? new Color(0.45f, 0.75f, 0.70f) : new Color(0.14f, 0.47f, 0.43f);
        static Color DotFree => PerfLintStyle.Good;
        static Color DotPro => PerfLintStyle.Amber;

        // One colour for all three Autopilot screens, and deliberately so: the four above are CATEGORIES and have to
        // be told apart, while these are STEPS in one loop. Three different hues would say they are three kinds of
        // thing. The order is carried by the numbers, which are the ones its own tab strip shows.
        static Color DotStep => PerfLintStyle.Accent;

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
            col.Add(new Label(name) { style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink } });
            col.Add(new Label(desc) { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 1, color = PerfLintStyle.Dimmer } });
            row.Add(col);
            return row;
        }

        /// <summary>One toolbar button and what it does.</summary>
        static VisualElement ToolRow(string button, string desc)
        {
            var box = new VisualElement { style = { marginBottom = 6 } };
            box.Add(new Label(button) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink } });
            box.Add(new Label(desc) { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 1, color = PerfLintStyle.Dimmer } });
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

        // 2 — the guide gained the Autopilot, which by then was the menu's first item and the panel's own answer to
        // "what should I do", and was not mentioned anywhere in here. That is the bar this constant is meant to have:
        // someone who read version 1 came away not knowing the product's front door exists.
        private const string GuideVersion = "2";

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
