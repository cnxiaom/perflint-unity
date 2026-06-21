using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Licensing;
using PerfLint.Llm;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// PerfLint main panel.
    /// W5: Report UX — two-level collapse (domain → rule), severity/fixable/search filters, Info hidden by default, per-rule batch fixing.
    /// Later: W7 adds "Explain" (LLM); W8 adds Free/Pro feature gating.
    /// </summary>
    public sealed class PerfLintWindow : EditorWindow
    {
        private Label _scoreLabel;
        private Label _summaryLabel;
        private VisualElement _roslynBox;
        private Label _roslynNotice;
        private Button _roslynButton;
        private VisualElement _staleBanner; // Info banner after a report is restored from disk (non-blocking): the report is already visible; hints that a full rescan is available
        private Label _staleLabel;
        private Label _filterStatus;
        private ScrollView _results;
        private Button _scanButton;
        private Button _fixAllButton;
        private Button _licenseButton;
        private ScanResult _lastResult;

        // State after a report is restored from disk (surviving domain reload / window reopen): restored findings carry no
        // Fix/Action instances (those aren't serializable). Only these rules previously had one-click fixes; clicking
        // "Refresh this rule" rescans that rule on demand to bring back findings with instances. Once a rule is rescanned or a
        // full scan runs, it's removed from this set. An empty set means the current results are entirely "live".
        private readonly HashSet<string> _restoredFixableRuleIds = new HashSet<string>();

        // Filter state. Info is hidden by default, directly cutting the mass of "advisory-level" noise.
        private bool _showCritical = true;
        private bool _showWarning = true;
        private bool _showInfo = false;
        private bool _onlyFixable = false;
        private string _search = string.Empty;
        private TextField _searchField; // Promoted to a field: lets the "line-by-line analysis" jump set the search term externally
        private Toggle _infoToggle;     // Same: the jump needs Info enabled (line-level clues are mostly Info)

        // Remember each Foldout's expanded/collapsed state (keys: domain "D:..." / rule "R:..."), restored across rebuilds —
        // otherwise RenderResults rebuilding resets every group to its default expand/collapse, reopening what the user manually folded.
        private readonly Dictionary<string, bool> _foldoutExpanded = new Dictionary<string, bool>();

        // Max instance rows rendered per rule, to avoid stuffing tens of thousands of VisualElements at once in a huge project.
        private const int MaxRowsPerRule = 100;

        [MenuItem("Tools/PerfLint/Scan Project %#l")] // Ctrl/Cmd + Shift + L
        public static void Open() => OpenWindow();

        /// <summary>Open the main panel and return the window instance (for calling FocusOnScript after a "line-by-line analysis" jump).</summary>
        public static PerfLintWindow OpenWindow()
        {
            var win = GetWindow<PerfLintWindow>();
            win.titleContent = new GUIContent("PerfLint");
            // Min width 640: the title can wrap and buttons can wrap, but at extreme narrowness the two group-header buttons
            // (e.g. AI Fix all + Explain) + the scrollbar still get clipped; rather than keep fighting layout at tiny widths,
            // set a usable lower bound so group-header buttons stay fully visible (takes effect for floating windows).
            win.minSize = new Vector2(640, 380);
            win.Show();
            return win;
        }

        private void OnEnable() => LicenseService.Changed += RefreshLicenseButton;
        private void OnDisable() => LicenseService.Changed -= RefreshLicenseButton;

        private void RefreshLicenseButton()
        {
            if (_licenseButton == null) return;
            bool pro = Entitlements.IsPro;
            _licenseButton.text = pro ? "Pro ●" : "Free";
            _licenseButton.tooltip = LicenseService.StatusLine();
            _licenseButton.style.color = pro ? new Color(0.40f, 0.80f, 0.45f) : new StyleColor(StyleKeyword.Null);
        }

        private void CreateGUI()
        {
            LoadFoldoutState(); // restore folded/expanded groups across domain reload (e.g. after an AI Fix recompile)

            var root = rootVisualElement;
            root.style.paddingTop = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;

            // ── Top toolbar ──────────────────────────────
            var toolbar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 }
            };
            _scanButton = new Button(RunScan) { text = "Scan Project" };
            _scanButton.style.height = 26;
            _scanButton.style.flexGrow = 1;
            toolbar.Add(_scanButton);

            _fixAllButton = new Button(FixAllInResult) { text = "Fix All" };
            _fixAllButton.style.height = 26;
            _fixAllButton.style.marginLeft = 6;
            _fixAllButton.SetEnabled(false);
            toolbar.Add(_fixAllButton);

            var exportButton = new Button(ExportCsv) { text = L.Tr("Export CSV", "导出 CSV") };
            exportButton.style.height = 26;
            exportButton.style.marginLeft = 6;
            toolbar.Add(exportButton);

            var reportButton = new Button(ExportHtml) { text = L.Tr("Export Report", "导出报告") };
            reportButton.style.height = 26;
            reportButton.style.marginLeft = 6;
            reportButton.tooltip = L.Tr("Export a self-contained, shareable HTML health report (offline, nothing uploaded)",
                                        "导出自包含、可分享的 HTML 健康报告（离线、不上传任何内容）");
            toolbar.Add(reportButton);

            var ignoreButton = new Button(PerfLintScanSettingsWindow.Open) { text = L.Tr("Ignore", "忽略") };
            ignoreButton.style.height = 26;
            ignoreButton.style.marginLeft = 6;
            toolbar.Add(ignoreButton);

            var runtimeButton = new Button(PerfLintRuntimeWindow.Open) { text = L.Tr("Runtime", "运行时") };
            runtimeButton.style.height = 26;
            runtimeButton.style.marginLeft = 6;
            runtimeButton.tooltip = L.Tr(
                "Runtime (Play Mode) performance profiling: locate stutter / per-frame GC / CPU hotspots",
                "运行时（Play Mode）性能分析：定位卡顿 / 每帧 GC / CPU 热点");
            toolbar.Add(runtimeButton);

            var llmButton = new Button(PerfLintLlmSettingsWindow.Open) { text = "LLM" };
            llmButton.style.height = 26;
            llmButton.style.marginLeft = 6;
            toolbar.Add(llmButton);

            // Language toggle intentionally not exposed: the scan UI ships English-only by default.
            // The bilingual infrastructure (L.Tr / Chinese strings) is kept for internal use, but no
            // user-facing EN/中 switch is offered here.

            _licenseButton = new Button(PerfLintLicenseWindow.Open);
            _licenseButton.style.height = 26;
            _licenseButton.style.marginLeft = 6;
            toolbar.Add(_licenseButton);
            RefreshLicenseButton();
            root.Add(toolbar);

            // ── Health score ──────────────────────────────
            var header = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 }
            };
            _scoreLabel = new Label("—")
            {
                style = { fontSize = 28, unityFontStyleAndWeight = FontStyle.Bold, marginRight = 12 }
            };
            _summaryLabel = new Label(L.Tr("Not scanned yet. Click Scan Project to start.", "尚未扫描。点击 Scan Project 开始。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, flexGrow = 1 }
            };
            header.Add(_scoreLabel);
            header.Add(_summaryLabel);
            root.Add(header);

            // ── Deep script analysis (Roslyn) degradation notice + one-click enable ──────────────
            // Without the Roslyn module, script analysis is only text-level (LOG001 etc.); GC / per-frame allocation / heavy CPU loop rules are all silent.
            // Hidden by default; UpdateRoslynNotice() shows/hides it based on detection — otherwise users would wrongly think "the scripts are clean".
            _roslynBox = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None,
                    marginBottom = 4,
                    paddingTop = 4, paddingBottom = 4, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(0.85f, 0.65f, 0.13f, 0.15f),
                }
            };
            _roslynNotice = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    color = new Color(0.95f, 0.78f, 0.30f),
                    fontSize = 11
                }
            };
            _roslynButton = new Button(OnRoslynButton) { text = L.Tr("Enable script analysis", "一键启用脚本分析") };
            _roslynButton.style.marginTop = 4;
            _roslynButton.style.alignSelf = Align.FlexStart;
            _roslynBox.Add(_roslynNotice);
            _roslynBox.Add(_roslynButton);
            root.Add(_roslynBox);
            UpdateRoslynNotice();

            // ── Restore info banner (shown after a report is restored from disk; non-blocking) ──────────────
            // The report is persisted and survives domain reload / window reopen, so we no longer blank the report or force an 86s full rescan here.
            // It just informs: the report is from the last scan and may be slightly stale; Locate/AI Fix work immediately, one-click fixes use "Refresh" on a rule,
            // or do a full rescan. Hidden by default.
            _staleBanner = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None,
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    marginBottom = 4,
                    paddingTop = 4, paddingBottom = 4, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(0.30f, 0.55f, 0.85f, 0.18f),
                }
            };
            // Wrap the text in a shrinkable container (flexGrow=1 + minWidth=0); do NOT set flexGrow on the text itself — setting
            // flexGrow directly on the Label makes it refuse to shrink under text measurement and pushes the right-side "Rescan all"
            // button out of the window (clipped even when the window is wide). Wrapping in a container is the same robust pattern used for instance rows.
            var staleTextWrap = new VisualElement { style = { flexGrow = 1, minWidth = 0 } };
            _staleLabel = new Label(L.Tr(
                "Report restored from the last scan (may be slightly stale). Locate and AI Fix work; for one-click fixes use 'Refresh' on a rule, or rescan all.",
                "报告由上次扫描恢复（可能略旧）。Locate 与 AI Fix 可用；一键修复点规则上的「刷新」，或全量重扫。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, color = new Color(0.70f, 0.83f, 0.98f), fontSize = 11 }
            };
            staleTextWrap.Add(_staleLabel);
            _staleBanner.Add(staleTextWrap);
            var refreshButton = new Button(RunScan) { text = L.Tr("Rescan all", "全量重扫") };
            refreshButton.style.marginLeft = 6;
            refreshButton.style.flexShrink = 0;
            _staleBanner.Add(refreshButton);
            root.Add(_staleBanner);

            // ── Filter bar ──────────────────────────────────
            root.Add(BuildFilterBar());

            _filterStatus = new Label("") { style = { opacity = 0.6f, fontSize = 10, marginBottom = 2 } };
            root.Add(_filterStatus);

            root.Add(MakeDivider());

            // ── Results list ────────────────────────────────
            _results = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            // Reserve width on the content's right for the vertical scrollbar: otherwise the scrollbar floats over the content and clips part of the rightmost buttons (Explain / Fix).
            _results.contentContainer.style.paddingRight = 14;
            root.Add(_results);

            // ── Privacy footer (trust selling point, always visible) ──────────────
            root.Add(new Label(L.Tr(
                "Scans run locally and are never uploaded · AI Fix sends only the snippet you choose — via PerfLint's zero-log AI service, or direct to your own provider (Advanced)",
                "扫描本地完成、永不上传 · AI 修复仅发送你选择的那段代码——经 PerfLint 零日志 AI 服务转发，或直连你自己的服务商（高级）"))
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    unityFontStyleAndWeight = FontStyle.Italic,
                    opacity = 0.6f, marginTop = 6, fontSize = 10
                }
            });

            // After a domain reload / window reopen the in-memory results are empty → restore the last scan from disk, avoiding a blank report and an 86s forced full rescan.
            RestoreLastResultIfAny();
        }

        /// <summary>
        /// Restore the last scan results after the window is built:
        ///   1. Results already in memory (GUI rebuilt within the same session) → redraw directly, no disk read.
        ///   2. Otherwise restore the baseline from disk; restored findings carry no Fix/Action instances (not serializable), recorded in _restoredFixableRuleIds,
        ///      revived on demand by the "Refresh" button on a rule group. Locate / AI Fix don't depend on these instances and work right after restore.
        ///   3. If there are files just modified by AI Fix (staged by the verifier across reloads) → incrementally rescan those files so their findings become live with accurate line numbers —
        ///      this is exactly what replaces "force a full rescan after AI Fix": touch only the few changed files, sub-second.
        /// </summary>
        private void RestoreLastResultIfAny()
        {
            if (_lastResult != null) { RenderHeader(_lastResult); RenderResults(); return; }

            var restored = ScanResultStore.Load();
            if (restored == null) return; // Never scanned / file corrupted → stay in the not-scanned state

            _lastResult = restored.Result;
            _restoredFixableRuleIds.Clear();
            if (restored.FixableRuleIds != null)
                foreach (var id in restored.FixableRuleIds) _restoredFixableRuleIds.Add(id);

            // Changed files (modified by AI Fix + scripts the user manually edited/deleted/moved): incrementally rescan to make their findings live (replacing a full rescan).
            // Both sources are registered in PerfLintPendingRescan, written by the change tracker / verifier before domain reload, and consumed here.
            var changed = PerfLintPendingRescan.Consume();
            bool refreshedAny = false;
            foreach (var file in changed)
            {
                if (string.IsNullOrEmpty(file)) continue;
                var updated = ScanRunner.RescanFile(file, _lastResult);
                if (updated != null) { _lastResult = updated; refreshedAny = true; }
            }
            if (refreshedAny) ScanResultStore.Save(_lastResult);

            SessionState.EraseBool(PerfLintScriptFixVerifier.RescanFlag);

            // Too many changes to incrementally rescan one by one (branch switch / large batch reimport) → wholesale stale, prompt the user for a full rescan.
            bool stale = PerfLintPendingRescan.ConsumeStale();

            // Info banner: prompt when there are still "previously fixable but not rescanned" rules, or when wholesale stale (otherwise the report is live enough — don't disturb).
            if (_staleBanner != null)
            {
                bool show = _restoredFixableRuleIds.Count > 0 || stale;
                _staleBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                if (_staleLabel != null)
                    _staleLabel.text = stale
                        ? L.Tr("The project changed a lot; the report may be stale — a full rescan is recommended.",
                               "项目有较多改动，报告可能已过期，建议全量重扫。")
                        : L.Tr("Report restored from the last scan (may be slightly stale). Locate and AI Fix work; for one-click fixes use 'Refresh' on a rule, or rescan all.",
                               "报告由上次扫描恢复（可能略旧）。Locate 与 AI Fix 可用；一键修复点规则上的「刷新」，或全量重扫。");
            }

            RenderHeader(_lastResult);
            RenderResults();
        }

        private VisualElement BuildFilterBar()
        {
            var bar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginBottom = 2 }
            };

            bar.Add(MakeToggle("Critical", _showCritical, v => { _showCritical = v; RenderResults(); }));
            bar.Add(MakeToggle("Warning", _showWarning, v => { _showWarning = v; RenderResults(); }));
            _infoToggle = MakeToggle("Info", _showInfo, v => { _showInfo = v; RenderResults(); });
            bar.Add(_infoToggle);
            bar.Add(MakeToggle(L.Tr("Fixable only", "只看可修复"), _onlyFixable, v => { _onlyFixable = v; RenderResults(); }));

            var search = new TextField { value = _search };
            _searchField = search;
            search.style.flexGrow = 1;
            search.style.minWidth = 120;
            search.style.marginLeft = 6;
            // Placeholder hint
            var placeholder = new Label(L.Tr("Filter rule / title / path…", "筛选规则/标题/路径…"))
            {
                pickingMode = PickingMode.Ignore,
                style = { position = Position.Absolute, left = 4, top = 2, opacity = 0.4f, fontSize = 11 }
            };
            search.Add(placeholder);
            search.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? string.Empty;
                placeholder.style.display = string.IsNullOrEmpty(_search) ? DisplayStyle.Flex : DisplayStyle.None;
                RenderResults();
            });
            bar.Add(search);

            return bar;
        }

        private static Toggle MakeToggle(string label, bool initial, Action<bool> onChange)
        {
            var t = new Toggle(label) { value = initial };
            t.style.marginRight = 12;
            t.style.flexShrink = 0;

            // By default a BaseField's label has a min-width and stretches, pushing the checkbox to the right.
            // Tighten the label so the checkbox sits right next to the text.
            var lbl = t.Q<Label>();
            if (lbl != null)
            {
                lbl.style.minWidth = 0;
                lbl.style.flexGrow = 0;
                lbl.style.marginRight = 4;
                lbl.style.paddingRight = 0;
            }

            t.RegisterValueChangedCallback(evt => onChange(evt.newValue));
            return t;
        }

        private void RunScan()
        {
            if (_staleBanner != null) _staleBanner.style.display = DisplayStyle.None; // Once refreshed, clear the stale prompt
            _scanButton.SetEnabled(false);
            _fixAllButton.SetEnabled(false);
            _results.Clear();
            ScanResult result = null;

            try
            {
                var context = new ScanContext(
                    cancellationToken: CancellationToken.None,
                    reportProgress: (name, p) =>
                        EditorUtility.DisplayProgressBar("PerfLint", $"Scanning: {name}", p));

                result = ScanRunner.Run(context);
            }
            catch (OperationCanceledException)
            {
                // User canceled, return silently.
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _scanButton.SetEnabled(true);
            }

            if (result != null)
            {
                _lastResult = result;
                _restoredFixableRuleIds.Clear(); // A full scan produces live findings, so the restored state is all invalidated
                ScanResultStore.Save(_lastResult);
                RenderHeader(result);
                RenderResults();
            }
        }

        /// <summary>Show/hide the top "script analysis degraded" notice + one-click enable button based on whether the Roslyn module is compiled in. Refreshed by CreateGUI and after every scan.</summary>
        private void UpdateRoslynNotice()
        {
            if (_roslynBox == null) return;
            bool deep = ScanRunner.IsDeepScriptAnalysisAvailable();
            _roslynBox.style.display = deep ? DisplayStyle.None : DisplayStyle.Flex;
            if (deep) return;

            bool canOneClick = RoslynSetup.CanOneClickInstall;
            _roslynNotice.text =
                L.Tr("⚠ Deep script analysis is not enabled: only text-level checks (e.g. Debug.Log) run for now; ",
                     "⚠ 脚本深度分析未启用：当前仅做文本级检测（如 Debug.Log）；") +
                L.Tr("script GC / per-frame allocation / heavy CPU loop rules (GC001–004, UPD001–003, CPU001) are not running. ",
                     "脚本 GC / 每帧分配 / CPU 重循环（GC001–004、UPD001–003、CPU001）规则未运行。") +
                (canOneClick
                    ? L.Tr("Click 'Enable' below (auto-adds the Microsoft.CodeAnalysis DLLs + the PERFLINT_ROSLYN define and recompiles).",
                           "点下方一键启用（自动放入 Microsoft.CodeAnalysis DLL + 加 PERFLINT_ROSLYN 宏并重编译）。")
                    : L.Tr("This package has no bundled Roslyn DLLs; follow SETUP-ROSLYN.md to install via NuGetForUnity, then enable.",
                           "本包未内置 Roslyn DLL，请按 SETUP-ROSLYN.md 用 NuGetForUnity 安装后再启用。"));
            _roslynButton.text = canOneClick ? L.Tr("Enable script analysis", "一键启用脚本分析") : L.Tr("View setup steps", "查看启用步骤");
        }

        private void OnRoslynButton()
        {
            if (!RoslynSetup.CanOneClickInstall)
            {
                // No bundled DLLs: open the manual-steps doc.
                var doc = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                    "Packages/com.perflint.unity/Editor/Scripting/SETUP-ROSLYN.md");
                if (doc != null) AssetDatabase.OpenAsset(doc);
                else EditorUtility.DisplayDialog(L.Tr("Enable script analysis", "启用脚本分析"),
                    L.Tr("Follow Editor/Scripting/SETUP-ROSLYN.md in the package: install Microsoft.CodeAnalysis.CSharp via NuGetForUnity and add the Scripting Define `PERFLINT_ROSLYN`.",
                         "请按包内 Editor/Scripting/SETUP-ROSLYN.md：用 NuGetForUnity 安装 " +
                         "Microsoft.CodeAnalysis.CSharp，并加 Scripting Define `PERFLINT_ROSLYN`。"), "OK");
                return;
            }

            // The button itself is the "one-click enable" intent; no second confirmation needed (the action is reversible: removing the define turns it off). Execute directly.
            var (ok, msg, conflicts) = RoslynSetup.Install();
            if (ok)
            {
                EditorUtility.DisplayDialog(L.Tr("Enabled", "已启用"), msg, "OK");
            }
            else if (conflicts != null && conflicts.Length > 0)
            {
                // Version conflict: offer a "Locate conflicting DLLs" button that, when clicked, selects and highlights these old-version dependencies in the Project window.
                bool locate = EditorUtility.DisplayDialog(L.Tr("Enable failed", "启用失败"), msg, L.Tr("Locate conflicting DLLs", "定位冲突 DLL"), L.Tr("Close", "关闭"));
                if (locate) RoslynSetup.LocateInProject(conflicts);
            }
            else
            {
                EditorUtility.DisplayDialog(L.Tr("Enable failed", "启用失败"), msg, "OK");
            }
        }

        private void RenderHeader(ScanResult result)
        {
            UpdateRoslynNotice();
            _scoreLabel.text = $"{result.HealthScore()}";
            _scoreLabel.style.color = ScoreColor(result.HealthScore());
            _summaryLabel.text =
                $"{L.Tr("Grade", "等级")} {result.HealthGrade()} · {result.CriticalCount} Critical · " +
                $"{result.WarningCount} Warning · {result.InfoCount} Info · " +
                $"{result.AutoFixableCount} {L.Tr("one-click-fixable", "项可一键修复")} · {result.Duration.TotalSeconds:0.0}s";

            _fixAllButton.text = result.AutoFixableCount > 0 ? $"Fix All ({result.AutoFixableCount})" : "Fix All";
            _fixAllButton.SetEnabled(result.AutoFixableCount > 0);
        }

        /// <summary>Redraw the results list based only on filter state, without triggering a rescan — toggling filters is instant.</summary>
        /// <summary>
        /// Restore the scroll position after rebuilding the list. The list is multi-level Foldouts + TextFields, whose layout takes several passes to settle;
        /// in early layout passes the content height isn't ready yet, so setting scrollOffset gets clamped small. So re-set it on every layout change of the content container,
        /// until it sticks to the target (content is tall enough) or the attempt count is exhausted, then unregister the callback and hand control back to the user.
        /// </summary>
        private void RestoreScrollAfterLayout(Vector2 scroll)
        {
            int attempts = 0;
            void OnGeo(GeometryChangedEvent _)
            {
                _results.scrollOffset = scroll;
                attempts++;
                // Stuck to the target (content tall enough) or 12 attempts exhausted (including cases where content really got shorter and the target is unreachable) → unregister the callback.
                if (attempts >= 12 || Mathf.Abs(_results.scrollOffset.y - scroll.y) < 1f)
                    _results.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnGeo);
            }
            _results.contentContainer.RegisterCallback<GeometryChangedEvent>(OnGeo);
            _results.scrollOffset = scroll; // If content is already tall enough, takes effect immediately (fallback for when the geometry event doesn't fire because the height didn't change)
        }

        /// <summary>Get the remembered Foldout expanded state; use the default value if not recorded.</summary>
        private bool GetFoldout(string key, bool defaultValue) =>
            _foldoutExpanded.TryGetValue(key, out var v) ? v : defaultValue;

        // Foldout expand/collapse state is an instance field, so it would be wiped on the domain reload an AI Fix
        // triggers (recompile after applying) — reopening groups the user had folded. Persist it to SessionState
        // (survives domain reload, cleared on Unity restart) so the view stays exactly as the user left it across a fix.
        private const string KFoldoutState = "PerfLint.Window.FoldoutState";

        private void SaveFoldoutState()
        {
            if (_foldoutExpanded.Count == 0) { SessionState.EraseString(KFoldoutState); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _foldoutExpanded)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append('=').Append(kv.Value ? '1' : '0'); // keys are "D:<domain>"/"R:<ruleId>" — no '=' or '\n'
            }
            SessionState.SetString(KFoldoutState, sb.ToString());
        }

        private void LoadFoldoutState()
        {
            _foldoutExpanded.Clear();
            var s = SessionState.GetString(KFoldoutState, "");
            if (string.IsNullOrEmpty(s)) return;
            foreach (var line in s.Split('\n'))
            {
                int eq = line.LastIndexOf('=');
                if (eq > 0) _foldoutExpanded[line.Substring(0, eq)] = line[eq + 1] == '1';
            }
        }

        private void RenderResults()
        {
            _results.Clear();
            if (_lastResult == null) return;

            if (_lastResult.Findings.Count == 0)
            {
                _filterStatus.text = "";
                _results.Add(new Label(L.Tr("No issues found.", "未发现问题。")) { style = { marginTop = 8 } });
                return;
            }

            var filtered = _lastResult.Findings.Where(PassesFilter).ToList();
            _filterStatus.text = $"{L.Tr("Showing", "显示")} {filtered.Count} / {_lastResult.Findings.Count}" +
                                 (_showInfo ? "" : L.Tr(" · Info hidden", " · Info 已隐藏"));

            if (filtered.Count == 0)
            {
                _results.Add(new Label(L.Tr("No matches under the current filter.", "当前筛选下没有匹配项。")) { style = { marginTop = 8, opacity = 0.7f } });
                return;
            }

            // Two-level grouping: domain → rule.
            foreach (var domainGroup in filtered.GroupBy(f => f.Domain).OrderBy(g => g.Key))
            {
                string dkey = "D:" + domainGroup.Key;
                var domainFoldout = new Foldout
                {
                    text = $"{domainGroup.Key}  ({domainGroup.Count()})",
                    value = GetFoldout(dkey, true)
                };
                domainFoldout.style.marginTop = 6;
                domainFoldout.Q<Toggle>()?.AddToClassList("perflint-domain");
                domainFoldout.RegisterValueChangedCallback(_ => { _foldoutExpanded[dkey] = domainFoldout.value; SaveFoldoutState(); });

                var ruleGroups = domainGroup
                    .GroupBy(f => f.RuleId)
                    .OrderByDescending(g => g.Max(f => f.Severity))
                    .ThenByDescending(g => g.Count());

                foreach (var ruleGroup in ruleGroups)
                    domainFoldout.Add(BuildRuleFoldout(ruleGroup));

                _results.Add(domainFoldout);
            }
        }

        private VisualElement BuildRuleFoldout(IGrouping<string, Finding> ruleGroup)
        {
            var items = ruleGroup.ToList();
            var sev = items.Max(f => f.Severity);
            // The group header uses the rule-level title (without the per-instance count); falls back to the first item's Title if unset.
            string repTitle = items[0].GroupTitleOrTitle;
            int fixableCount = items.Count(f => f.CanAutoFix);

            // Info rules collapsed by default to cut noise; Critical/Warning expanded by default. The remembered state takes priority over the default.
            string rkey = "R:" + ruleGroup.Key;
            var foldout = new Foldout { value = GetFoldout(rkey, sev != Severity.Info) };
            foldout.style.marginLeft = 8;
            foldout.style.marginTop = 2;
            foldout.RegisterValueChangedCallback(_ => { _foldoutExpanded[rkey] = foldout.value; SaveFoldoutState(); });

            // Custom title row (with severity color dot, count, per-rule batch fix button).
            var titleToggle = foldout.Q<Toggle>();
            // minWidth=0 is key: flex children default to min-width:auto (= content width); without 0 the title refuses to shrink/wrap
            // and, once it fills the whole row, pushes the right-side buttons out of the window (especially in narrow windows). Setting 0 lets the title shrink with the window and wrap.
            // flexWrap: when the window is too narrow and the title has shrunk fully but still can't fit the right-side buttons, let the buttons wrap to the next line as a whole instead of clipping out of the window.
            // In wide windows they still lay out in one line by flex-basis (title flexGrow expands, buttons hug the right).
            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1, minWidth = 0, flexWrap = Wrap.Wrap } };
            titleRow.Add(new Label("●") { style = { color = SeverityColor(sev), marginRight = 6, minWidth = 12, flexShrink = 0 } });
            // Wrap the title in a shrinkable container (flexGrow on the container, not directly on the Label): setting flexGrow directly on the Label makes it refuse to shrink under text measurement
            // and push the right-side buttons out of the window; this is the same robust pattern used for instance rows / banners. minWidth/whiteSpace stay on the Label.
            var titleWrap = new VisualElement { style = { flexGrow = 1, minWidth = 0 } };
            titleWrap.Add(new Label($"{ruleGroup.Key} · {repTitle}  ({items.Count})")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, minWidth = 0, whiteSpace = WhiteSpace.Normal }
            });
            titleRow.Add(titleWrap);
            if (fixableCount > 0)
            {
                var fixRule = new Button(() => ApplyFixes(items.Where(f => f.CanAutoFix).ToList(), ruleGroup.Key))
                {
                    text = $"Fix ({fixableCount})"
                };
                fixRule.style.marginLeft = 4;
                fixRule.style.flexShrink = 0; // When the title wraps, the button keeps its width and isn't squashed/clipped
                // Stop the click from bubbling to the Foldout header, avoiding accidentally toggling expand/collapse.
                fixRule.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                titleRow.Add(fixRule);
            }
            else if (_restoredFixableRuleIds.Contains(ruleGroup.Key))
            {
                // Report restored from disk: this rule previously had a one-click fix, but the Fix instance is non-serializable and was lost. Clicking this rescans only this rule (incremental, not full),
                // bringing back findings with instances, and the "Fix" button then appears. Locate/AI Fix are unaffected and already work.
                string rid = ruleGroup.Key;
                var enableFix = new Button(() => RescanRules(new[] { rid }))
                {
                    text = L.Tr("Enable fix", "启用修复"),
                    tooltip = L.Tr("This rule's results were restored from the last scan; one-click fix needs a rule rescan to enable (rescans only this rule, not everything).",
                                   "此规则结果由上次扫描恢复，一键修复需重扫该规则才能启用（仅重扫这一条规则，不全量）")
                };
                enableFix.style.marginLeft = 4;
                enableFix.style.flexShrink = 0;
                enableFix.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                titleRow.Add(enableFix);
            }

            // AI batch fix: for all of this rule's findings that "point at code with no deterministic fix", generate and apply one by one, saving clicking each individually.
            int aiCount = items.Count(f => f.AiFixable);
            if (aiCount > 0)
            {
                if (LlmSettings.IsConfigured)
                {
                    string rid = ruleGroup.Key;
                    var aiAll = new Button(() => AiFixAllForRule(rid)) { text = $"{L.Tr("AI Fix all", "AI Fix 全部")} ({aiCount})" };
                    aiAll.style.marginLeft = 4;
                    aiAll.style.flexShrink = 0;
                    aiAll.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                    titleRow.Add(aiAll);
                }
                else
                {
                    // This rule has AI-fixable findings, but the LLM isn't configured — show a "go configure" prompt rather than silently hiding the button and leaving the user wondering why there's no AI Fix.
                    var cfg = new Button(() => PerfLintLlmSettingsWindow.Open())
                    {
                        text = $"{L.Tr("Set up AI Fix", "配置 AI Fix")} ({aiCount})",
                        tooltip = L.Tr("This rule supports AI one-click fix, but you must configure an LLM provider and key first. Click to open LLM settings.",
                                       "这条规则可用 AI 一键修复，但需先配置 LLM 服务商与密钥。点此打开 LLM 设置。")
                    };
                    cfg.style.marginLeft = 4;
                    cfg.style.flexShrink = 0;
                    cfg.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                    titleRow.Add(cfg);
                }
            }

            // Explain is at the rule level (one per rule, no longer repeated per row): use the first item as the representative; the explanation applies to the whole rule.
            if (LlmSettings.IsConfigured)
            {
                VisualElement panel = null;
                var explain = new Button { text = "Explain" };
                explain.style.marginLeft = 4;
                explain.style.flexShrink = 0;
                explain.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                explain.clicked += () =>
                {
                    if (!Entitlements.RequireAiCredit(L.Tr("LLM Explain", "LLM 解释"))) return;
                    foldout.value = true;
                    if (panel == null)
                    {
                        panel = BuildExplainPanel(items[0]);
                        foldout.Insert(0, panel); // Place above the instance rows
                    }
                    else
                    {
                        panel.style.display = panel.style.display == DisplayStyle.None
                            ? DisplayStyle.Flex : DisplayStyle.None;
                    }
                };
                titleRow.Add(explain);
            }

            // Put the title row into the Foldout's built-in Toggle "input cell" (the cell with the toggle arrow). It must be added into unity-toggle__input,
            // otherwise titleRow becomes another flex-grow child of the Toggle, each taking half the width with the arrow cell → the title is pushed to the window's centerline
            // and the right-side buttons are also clipped out of the window. Added into the input cell, it follows the arrow, fills the whole row, and the title left-aligns normally and wraps with the window.
            titleToggle?.Q<Label>()?.RemoveFromHierarchy();
            var toggleInput = titleToggle?.Q(className: "unity-toggle__input");
            (toggleInput ?? (VisualElement)titleToggle)?.Add(titleRow);

            // Rule-level "action-type" batch (e.g. "Extract to shared group, all") goes at the top of the expanded area on its own prominent line —
            // in the header line the title's flexGrow fills the space and the button would be pushed out of view on the window's right. Distinct from Fix All: config-changing actions don't go into Fix All.
            // Note: the batch targets ALL of this rule's findings (including those not shown, limited by MaxRowsPerRule), not just the visible rows.
            var actionItems = items.Where(f => f.HasAction).ToList();
            if (actionItems.Count > 0)
            {
                string label = actionItems[0].Action.Label;
                var bar = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 18, marginTop = 3, marginBottom = 3 }
                };
                // Actions that pick a target (duplicate merge) batch through a single shared project scan instead of
                // re-scanning per group (500 groups would otherwise be 500 full scans); others run one-by-one.
                bool batchChoice = actionItems[0].Action.SupportsTargetChoice;
                var actAll = new Button(() => { if (batchChoice) RunMergeAllForDuplicates(actionItems); else RunActionsForRule(actionItems); })
                { text = $"⚡ {label} {L.Tr("all", "全部")} ({actionItems.Count})" };
                bar.Add(actAll);
                foldout.Add(bar);
            }

            // Instance rows (limited).
            int shown = Math.Min(items.Count, MaxRowsPerRule);
            for (int i = 0; i < shown; i++)
                foldout.Add(MakeFindingRow(items[i]));
            if (items.Count > shown)
            {
                string hint = fixableCount > 0
                    ? L.Tr("use the Fix button above to batch-process", "用上方 Fix 批量处理")
                    : L.Tr("narrow with search, or use Export CSV to see all", "用搜索缩小范围，或「导出 CSV」查看全部");
                foldout.Add(new Label($"… {items.Count - shown} {L.Tr("more", "条")} ({hint})")
                {
                    style = { opacity = 0.55f, marginLeft = 18, unityFontStyleAndWeight = FontStyle.Italic }
                });
            }

            return foldout;
        }

        private VisualElement MakeFindingRow(Finding finding)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginLeft = 18,
                    paddingTop = 2, paddingBottom = 2,
                    borderBottomWidth = 1,
                    borderBottomColor = new Color(1, 1, 1, 0.05f)
                }
            };

            var text = new VisualElement { style = { flexGrow = 1 } };
            text.Add(new Label(finding.Title) { style = { whiteSpace = WhiteSpace.Normal } });
            if (!string.IsNullOrEmpty(finding.TargetPath))
                text.Add(new Label(finding.TargetPath) { style = { opacity = 0.5f, fontSize = 10, whiteSpace = WhiteSpace.Normal } });
            row.Add(text);

            // When multiple assets are involved (e.g. a duplicate group), offer "select all" — a single Locate isn't enough.
            if (finding.HasGroup)
            {
                var sel = new Button(() => SelectGroup(finding.Group)) { text = $"{L.Tr("Select group", "选中组")} ({finding.Group.Count})" };
                sel.style.marginLeft = 4;
                row.Add(sel);
            }
            else if (finding.Ping != null)
            {
                var locate = new Button(() => finding.Ping()) { text = "Locate" };
                locate.style.marginLeft = 4;
                row.Add(locate);
            }

            if (finding.CanAutoFix)
            {
                // The button is always visible (Free sees it → clicking turns into an upgrade nudge); only Pro actually executes.
                var fix = new Button(() => ApplyFix(finding)) { text = "Fix" };
                fix.style.marginLeft = 4;
                row.Add(fix);
            }

            // Action-type actions (e.g. "Extract to shared group"): config-changing, not undoable, not in Fix All; separate button + separate confirmation.
            if (finding.HasAction)
            {
                var act = new Button(() => RunAction(finding)) { text = finding.Action.Label };
                act.style.marginLeft = 4;
                row.Add(act);
            }

            // Script-level AI fix: only for findings that "point at code with no deterministic fix" (one by one, each at a different location).
            if (finding.AiFixable && LlmSettings.IsConfigured)
            {
                var col = new VisualElement { style = { marginLeft = 18 } };
                row.style.marginLeft = 0;

                VisualElement panel = null;
                var aifix = new Button { text = "AI Fix" };
                aifix.style.marginLeft = 4;
                aifix.clicked += () =>
                {
                    if (panel == null) { panel = BuildAiFixPanel(finding); col.Add(panel); }
                    else panel.style.display = panel.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None;
                };
                row.Add(aifix);

                col.Add(row);
                return col;
            }

            return row;
        }

        /// <summary>Script-level AI fix panel: clearly state how much code will be sent → generate → review diff → apply (undo relies on version control).</summary>
        private VisualElement BuildAiFixPanel(Finding finding)
        {
            string provider = LlmSettings.ProviderDisplayName;
            int n = ScriptFixService.WindowLineCount(finding);

            var box = new VisualElement
            {
                style =
                {
                    marginTop = 4, marginBottom = 6,
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.04f),
                    borderLeftWidth = 2, borderLeftColor = new Color(0.95f, 0.70f, 0.20f)
                }
            };

            var status = new Label(L.Tr(
                $"AI Fix will send ~{n} lines around the flagged code to {provider} (only this snippet, not the whole file/project).",
                $"AI 修复会把被标记代码附近约 {n} 行发送给 {provider}（仅这一段，不发整文件/项目）。"))
            {
                style = { whiteSpace = WhiteSpace.Normal }
            };
            box.Add(status);

            var diffArea = new VisualElement();

            var gen = new Button { text = L.Tr($"Generate fix (send ~{n} lines to {provider})", $"生成修复（发送约 {n} 行给 {provider}）") };
            gen.style.marginTop = 4;
            gen.clicked += () =>
            {
                if (!Entitlements.RequireAiCredit(L.Tr("AI Fix", "AI 修复"))) return;
                gen.SetEnabled(false);
                status.text = L.Tr("Generating…", "生成中…");
                diffArea.Clear();
                ScriptFixService.Propose(finding, p =>
                {
                    gen.SetEnabled(true);
                    if (!p.Ok) { status.text = L.Tr("Failed: ", "失败：") + p.Error; return; }
                    if (p.NoChange)
                    {
                        status.text = L.Tr("AI judged no change is needed here — the original code is fine; this may be a false positive and can be ignored.",
                                           "AI 判断此处无需修改——原始写法已正确，可能是规则误报，可忽略。");
                        return;
                    }
                    status.text = p.Locatable
                        ? L.Tr("Fix generated. Review the diff, then apply:", "已生成修复，请审阅 diff 后应用：")
                        : L.Tr("Fix generated, but the original snippet couldn't be located precisely in the file. Apply manually:", "已生成修复，但无法在文件中精确定位原始片段，请手动应用：");
                    RenderAiFixDiff(diffArea, p);
                });
            };
            box.Add(gen);
            box.Add(diffArea);
            return box;
        }

        private void RenderAiFixDiff(VisualElement area, ScriptFixProposal p)
        {
            area.Clear();
            AiFixDiffView.BuildDiffBlocks(area, p); // −original/＋fix/＋field/＋using (shared with the batch review window and the runtime panel)

            if (p.Locatable)
            {
                var apply = new Button { text = L.Tr("Apply fix (writes to file; commit to version control first)", "应用修复（写入文件，建议先提交版本控制）") };
                apply.style.marginTop = 6;
                apply.clicked += () =>
                {
                    bool ok = ScriptFixService.Apply(p, out string msg);
                    if (ok)
                    {
                        ShowNotification(new GUIContent(L.Tr("AI fix applied", "AI 修复已应用")));
                        area.Clear();
                        area.Add(new Label("✓ " + msg) { style = { color = new Color(0.45f, 0.80f, 0.50f) } });

                        // Immediate refresh: rescan only the changed file and replace its warnings, avoiding a full rescan (86s-class).
                        // Doesn't depend on compile/domain reload — applying the fix deliberately doesn't trigger a reload.
                        if (_lastResult != null && !string.IsNullOrEmpty(p.FilePath))
                        {
                            // Preserve scroll position: RenderResults rebuilds the list and snaps the ScrollView to the top; the user wants to stay in place to keep looking at nearby warnings.
                            Vector2 scroll = _results.scrollOffset;
                            _lastResult = ScanRunner.RescanFile(p.FilePath, _lastResult);
                            ScanResultStore.Save(_lastResult);
                            RenderHeader(_lastResult);
                            RenderResults();
                            RestoreScrollAfterLayout(scroll);
                        }
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(L.Tr("Apply failed", "应用失败"), msg, "OK");
                    }
                };
                area.Add(apply);
            }
        }

        /// <summary>Build a single finding's AI explanation panel: auto-fire the first explanation, support follow-up questions.</summary>
        private VisualElement BuildExplainPanel(Finding finding)
        {
            var conv = new ExplainConversation(finding);

            var box = new VisualElement
            {
                style =
                {
                    marginTop = 4, marginBottom = 6,
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.04f),
                    borderLeftWidth = 2, borderLeftColor = new Color(0.45f, 0.65f, 0.95f)
                }
            };

            // Read-only multiline TextField: content can wrap and be selected/copied (suited to answers with code snippets).
            var output = new TextField { multiline = true, isReadOnly = true };
            output.style.whiteSpace = WhiteSpace.Normal;
            box.Add(output);

            var inputRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6, display = DisplayStyle.None }
            };
            var field = new TextField { style = { flexGrow = 1 } };
            var ask = new Button { text = L.Tr("Ask follow-up", "追问") };
            ask.style.marginLeft = 4;
            inputRow.Add(field);
            inputRow.Add(ask);
            box.Add(inputRow);

            string transcript = "";

            void Run(string follow)
            {
                if (!string.IsNullOrEmpty(follow)) transcript += L.Tr("\n\n— You: ", "\n\n— 你：") + follow;
                string thinking = L.Tr("…thinking…", "…思考中…");
                output.value = transcript.Length > 0 ? transcript + "\n\n" + thinking : thinking;
                ask.SetEnabled(false);

                conv.Ask(follow, r =>
                {
                    ask.SetEnabled(true);
                    if (r.Success)
                    {
                        transcript += (transcript.Length > 0 ? "\n\n" : "") + r.Text;
                        output.value = transcript;
                        inputRow.style.display = DisplayStyle.Flex;
                    }
                    else
                    {
                        output.value = transcript + (transcript.Length > 0 ? "\n\n" : "") + L.Tr("Error: ", "出错：") + r.Error;
                    }
                });
            }

            ask.clicked += () =>
            {
                string q = field.value;
                if (string.IsNullOrWhiteSpace(q)) return;
                field.value = "";
                Run(q);
            };

            Run(null); // Auto-fire the first explanation
            return box;
        }

        private void ApplyFix(Finding finding)
        {
            if (!Entitlements.RequirePro(L.Tr("One-click fix", "一键修复"))) return;

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Apply Fix", "PerfLint — 应用修复"),
                $"{finding.Title}\n\n{finding.Fix.Preview()}\n\n" + L.Tr("This change can be undone via Edit > Undo.", "该改动可通过 Edit > Undo 撤销。"),
                L.Tr("Apply", "应用"), L.Tr("Cancel", "取消"));
            if (!confirm) return;

            var r = finding.Fix.Apply();
            if (r.Success) ShowNotification(new GUIContent(r.Message ?? L.Tr("Fixed", "已修复")));
            else EditorUtility.DisplayDialog(L.Tr("Fix failed", "修复失败"), r.Message, "OK");

            RescanRules(new[] { finding.RuleId });
        }

        /// <summary>Run a single finding's "action-type action" (config changes etc., not undoable, not in Fix All). Separate confirmation dialog.</summary>
        private void RunAction(Finding finding)
        {
            var act = finding.Action;
            if (act == null) return;
            if (act.RequiresPro && !Entitlements.RequirePro(act.Label)) return;

            // Actions that let the user choose a target (e.g. "which duplicate copy to keep") open a chooser
            // instead of a plain confirm. The chooser runs the merge and we re-scan when it's done.
            if (act.SupportsTargetChoice && finding.HasGroup)
            {
                PerfLintDuplicateMergeWindow.Open(finding, () => RescanRules(new[] { finding.RuleId }));
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(L.Tr("PerfLint — Run Action", "PerfLint — 执行操作"), act.ConfirmMessage, L.Tr("Run", "执行"), L.Tr("Cancel", "取消"));
            if (!confirm) return;

            var r = act.Run();
            if (r.Success) ShowNotification(new GUIContent(r.Message ?? L.Tr("Done", "已完成")));
            else EditorUtility.DisplayDialog(L.Tr("Action failed", "操作失败"), r.Message, "OK");

            RescanRules(new[] { finding.RuleId });
        }

        /// <summary>
        /// Rule-level batch merge for duplicate groups (ASSET.DUP001). Unlike <see cref="RunActionsForRule"/> (one call
        /// per finding), this routes through <see cref="DuplicateAssetMerger.MergeAll"/> which scans the project once
        /// for all groups — so N duplicate groups don't trigger N full scans. Each group keeps its most-referenced copy.
        /// </summary>
        private void RunMergeAllForDuplicates(IReadOnlyList<Finding> findings)
        {
            if (findings == null || findings.Count == 0) return;
            var act = findings[0].Action;
            if (act == null) return;
            if (act.RequiresPro && !Entitlements.RequirePro(act.Label)) return;

            var groups = findings.Where(f => f.HasGroup).Select(f => f.Group).ToList();
            if (groups.Count == 0) return;

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Merge Duplicates", "PerfLint — 合并去重"),
                L.Tr($"Merge {groups.Count} duplicate group(s), keeping the most-referenced copy in each.\n\n",
                     $"合并 {groups.Count} 个重复组，每组保留被引用最多的那份。\n\n") + PerfLintWarnings.Irreversible,
                $"{L.Tr("Merge all", "全部合并")} ({groups.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            var r = DuplicateAssetMerger.MergeAll(groups);
            // Always show a dialog so the user sees the outcome (how many merged, how many left for manual handling).
            EditorUtility.DisplayDialog(
                r.Success ? L.Tr("Merge complete", "合并完成") : L.Tr("Merge failed", "合并失败"),
                r.Message, "OK");

            RescanRules(findings.Select(f => f.RuleId));
        }

        /// <summary>Rule-level batch run of "action-type actions". One confirmation covers all; run one by one then summarize, finally save and rescan in one go.</summary>
        private void RunActionsForRule(IReadOnlyList<Finding> findings)
        {
            if (findings == null || findings.Count == 0) return;
            var first = findings[0].Action;
            if (first == null) return;
            if (first.RequiresPro && !Entitlements.RequirePro(first.Label)) return;

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Batch Run", "PerfLint — 批量执行"),
                L.Tr($"Will run '{first.Label}' on {findings.Count} items.\n\n{first.ConfirmMessage}\n\n(The undo note above applies to each item.)",
                     $"将对 {findings.Count} 个项执行「{first.Label}」。\n\n{first.ConfirmMessage}\n\n（以上撤销说明适用于每一项。）"),
                $"{L.Tr("Run all", "执行全部")} ({findings.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            // Don't use Start/StopAssetEditing: extraction doesn't involve asset reimport, and it would defer Addressables' SaveAssets,
            // instead causing entries not to persist. Run each independently (each internally does postEvent=true + SaveAssets).
            int ok = 0, fail = 0;
            string lastErr = null;
            foreach (var f in findings)
            {
                if (f.Action == null) continue;
                var r = f.Action.Run();
                if (r.Success) ok++;
                else { fail++; lastErr = r.Message; }
            }
            AssetDatabase.SaveAssets();

            ShowNotification(new GUIContent(L.Tr($"Ran {ok}, failed {fail}", $"已执行 {ok}，失败 {fail}")));
            if (fail > 0 && lastErr != null)
                EditorUtility.DisplayDialog(L.Tr("Some failed", "部分失败"), L.Tr($"{fail} items failed. Last error: {lastErr}", $"{fail} 项失败，最后错误：{lastErr}"), "OK");
            RescanRules(findings.Select(f => f.RuleId));
        }

        private void FixAllInResult()
        {
            if (_lastResult == null) return;
            ApplyFixes(_lastResult.Findings.Where(f => f.CanAutoFix).ToList(), L.Tr("All", "全部"));
        }

        /// <summary>Batch-apply fixes to a given fixable set, using Start/StopAssetEditing to batch the reimports.</summary>
        private void ApplyFixes(IReadOnlyList<Finding> fixables, string label)
        {
            if (fixables == null || fixables.Count == 0) return;
            if (!Entitlements.RequirePro(L.Tr("Batch auto-fix", "批量自动修复"))) return;

            string breakdown = string.Join("\n",
                fixables.GroupBy(f => f.RuleId)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"  · {g.Key}: {g.Count()}"));

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Batch Fix", "PerfLint — 批量修复"),
                L.Tr($"Will apply auto-fix to {fixables.Count} items ({label}):\n\n{breakdown}\n\n",
                     $"将对 {fixables.Count} 项（{label}）应用自动修复：\n\n{breakdown}\n\n") +
                L.Tr("These changes modify asset import settings and trigger reimport; they can be undone via Edit > Undo.\nCommit your project to version control first.",
                     "这些改动会修改资源导入设置并触发重新导入，可通过 Edit > Undo 撤销。\n建议先确保项目已提交版本控制。"),
                $"{L.Tr("Fix", "修复")} ({fixables.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            int success = 0, failed = 0;
            var failures = new List<string>();

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < fixables.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L.Tr("PerfLint — Batch Fix", "PerfLint — 批量修复"),
                            $"{i + 1}/{fixables.Count}  {fixables[i].Title}",
                            (float)i / fixables.Count))
                        break;

                    try
                    {
                        var res = fixables[i].Fix.Apply();
                        if (res.Success) success++;
                        else { failed++; failures.Add($"{fixables[i].RuleId}: {res.Message}"); }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures.Add($"{fixables[i].RuleId}: {ex.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            if (failed > 0)
                Debug.LogWarning($"[PerfLint] " + L.Tr($"Batch fix done: {success} succeeded, {failed} failed.\n", $"批量修复完成：成功 {success}，失败 {failed}。\n") +
                                 string.Join("\n", failures.Take(20)));

            ShowNotification(new GUIContent(L.Tr($"Batch fix done: {success} succeeded, {failed} failed", $"批量修复完成：成功 {success}，失败 {failed}")));
            RescanRules(fixables.Select(f => f.RuleId));
        }

        /// <summary>
        /// "Batch fix" all of a rule's AI-fixable findings. **Changed to semi-automatic since [0.21.x]**: first generate proposals one by one (without writing files),
        /// then review each diff in the review window and check which to apply, writing the checked ones all at once only after confirmation — handing the "break the code" risk back to the user to confirm on the diff.
        /// (The old version "generated each and wrote to disk automatically", letting semantically wrong fixes land without review.)
        /// </summary>
        private void AiFixAllForRule(string ruleId)
        {
            if (!Entitlements.RequireAiCredit(L.Tr("AI batch fix", "AI 批量修复"))) return;
            if (_lastResult == null) return;

            // Dedupe by (file, line): one line may have several findings of the same rule (e.g. two Camera.main on one line → two UPD003),
            // and AI fixes the whole line in one shot, so applying the second is bound to be redundant / fail to locate. Generate/apply once per line (see AiFixBatch.DedupeByLine).
            var findings = AiFixBatch.DedupeByLine(
                _lastResult.Findings.Where(f => f.RuleId == ruleId && f.AiFixable));
            if (findings.Count == 0) return;

            string provider = LlmSettings.ProviderDisplayName;
            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — AI Batch Fix", "PerfLint — AI 批量修复"),
                L.Tr($"Will generate AI fixes for the {findings.Count} findings of rule {ruleId}, one call per finding (consuming tokens):\n\n", $"将对规则 {ruleId} 的 {findings.Count} 条逐条用 AI 生成修复（每条一次调用、消耗 token）：\n\n") +
                L.Tr($"· Each sends only its code snippet to {provider}; nothing is written yet.\n", $"· 每条只把对应代码片段（仅那一段）发送到 {provider}；此刻不写入任何文件。\n") +
                L.Tr("· After generation you'll review every diff and pick which to apply — only the ones you check are written.", "· 生成后你会逐条看 diff 并勾选要应用的——仅勾选的会被写入。"),
                $"{L.Tr("Generate", "生成")} ({findings.Count})", L.Tr("Cancel", "取消"));
            if (!confirm) return;

            AiFixGenerateAll(ruleId, findings, 0, new List<AiFixCandidate>());
        }

        /// <summary>Phase 1: Propose one by one to collect proposals (**without writing files**); open the review window once all are generated or the user cancels.</summary>
        private void AiFixGenerateAll(string ruleId, List<Finding> findings, int i, List<AiFixCandidate> collected)
        {
            if (i >= findings.Count)
            {
                EditorUtility.ClearProgressBar();
                OpenAiFixReview(ruleId, collected);
                return;
            }

            if (EditorUtility.DisplayCancelableProgressBar(
                    L.Tr("PerfLint — AI Batch Fix (generating)", "PerfLint — AI 批量修复（生成中）"),
                    $"{i + 1}/{findings.Count}  {findings[i].Title}", (float)i / findings.Count))
            {
                EditorUtility.ClearProgressBar();
                if (collected.Count > 0) OpenAiFixReview(ruleId, collected); // Already-generated ones can still be reviewed
                else ShowNotification(new GUIContent(L.Tr("AI batch fix canceled", "已取消 AI 批量修复")));
                return;
            }

            var finding = findings[i];
            ScriptFixService.Propose(finding, p =>
            {
                collected.Add(new AiFixCandidate { Finding = finding, Proposal = p });
                AiFixGenerateAll(ruleId, findings, i + 1, collected);
            });
        }

        /// <summary>Phase 2: Open the review window. If there are no applicable items, just explain why instead of opening an empty window.</summary>
        private void OpenAiFixReview(string ruleId, List<AiFixCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;
            if (!candidates.Any(c => AiFixBatch.IsApplicable(c.Proposal)))
            {
                EditorUtility.DisplayDialog(
                    L.Tr("AI Batch Fix", "AI 批量修复"),
                    L.Tr("None of the generated fixes can be applied (couldn't locate / no change needed / generation failed). Handle these manually.",
                         "生成的修复没有一条可应用（无法定位 / 无需改动 / 生成失败）。请手动处理。"),
                    "OK");
                return;
            }
            PerfLintAiFixReviewWindow.Open(ruleId, candidates, selected => ApplyReviewedAiFixes(selected));
        }

        /// <summary>
        /// Phase 3: After the user checks and confirms in the review window, write the selected proposals serially. Suspend background compilation to avoid a domain reload interrupting the loop;
        /// Apply one by one (each Apply re-locates by content, tolerating line-number drift from the previous change) + incremental rescan, with a unified verification at the end.
        /// </summary>
        private void ApplyReviewedAiFixes(List<ScriptFixProposal> selected)
        {
            if (selected == null || selected.Count == 0) return;

            // Multiple in the same file: group by file and apply **bottom-up** within a group (descending expected line) — fix lower lines first so the positions of upper lines still to be fixed don't shift,
            // reducing interference between fixes in the same file. Combined with LocateRegion's "closest to expected line" anchoring, even duplicate lines land in their right places
            // (the old batch rescanned and regenerated per file each time, so it didn't have this problem; after switching to "generate all at once" these two measures are required to keep same-file fixes from crossing wires).
            var ordered = selected.OrderBy(p => p.FilePath ?? "", StringComparer.Ordinal)
                                  .ThenByDescending(p => p.ExpectedLine)
                                  .ToList();

            PerfLintFixCompileScheduler.Suspend();
            int applied = 0, failed = 0;
            string lastErr = null;
            try
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L.Tr("PerfLint — AI Batch Fix (applying)", "PerfLint — AI 批量修复（应用中）"),
                            $"{i + 1}/{ordered.Count}  {ShortName(p.FilePath)}", (float)i / ordered.Count))
                        break;

                    if (ScriptFixService.Apply(p, out string msg))
                    {
                        applied++;
                        _lastResult = ScanRunner.RescanFile(p.FilePath, _lastResult); // Refresh this file, absorbing line-number drift
                    }
                    else { failed++; lastErr = msg; }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                PerfLintFixCompileScheduler.Resume(); // Each Apply already registered its own background verification; on resume it triggers once for all
            }

            ShowNotification(new GUIContent(L.Tr($"AI batch fix: {applied} applied, {failed} failed", $"AI 批量修复：已应用 {applied}，失败 {failed}")));
            if (failed > 0 && lastErr != null)
                EditorUtility.DisplayDialog(L.Tr("Some fixes failed", "部分修复失败"),
                    L.Tr($"{failed} couldn't be applied. Last error: {lastErr}\n\n", $"{failed} 条未能应用。最后错误：{lastErr}\n\n") +
                    L.Tr("This usually means a sibling fix on the same/adjacent line already changed it. Just run 'AI Fix all' again to retry the rest (it regenerates against the current file), or use 'AI Fix' on each remaining one.",
                         "这通常是因为同一行/相邻行的另一条修复已经改过它。直接再点一次「AI Fix 全部」即可重试剩余项（会基于当前文件重新生成），或对剩余每条点「AI Fix」。"),
                    "OK");

            if (_lastResult != null)
            {
                ScanResultStore.Save(_lastResult); // Persist once at the end of the batch, avoiding per-item IO
                Vector2 scroll = _results.scrollOffset;
                RenderHeader(_lastResult);
                RenderResults();
                RestoreScrollAfterLayout(scroll);
            }
        }

        private static string ShortName(string path) => string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);

        /// <summary>
        /// After a fix, rescan only the affected "groups" (the scanners owning the rules of the fixed findings) and replace their results —
        /// no more full rescan (86s-class). Preserve filters and scroll position. When there's no ownership table, ScanRunner.RescanRules safely falls back to a full scan.
        /// </summary>
        private void RescanRules(IEnumerable<string> affectedRuleIds)
        {
            if (_lastResult == null) { RunScan(); return; }

            var ids = (affectedRuleIds ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (ids.Count == 0) { RunScan(); return; }

            Vector2 scroll = _results.scrollOffset;
            try
            {
                var ctx = new ScanContext(
                    cancellationToken: CancellationToken.None,
                    reportProgress: (name, p) =>
                        EditorUtility.DisplayProgressBar("PerfLint", $"Rescanning: {name}", p));
                _lastResult = ScanRunner.RescanRules(ids, _lastResult, ctx);
                foreach (var id in ids) _restoredFixableRuleIds.Remove(id); // After rescan this rule has live findings
                // All restored rules have been rescanned → the report is live enough, remove the info banner.
                if (_restoredFixableRuleIds.Count == 0 && _staleBanner != null)
                    _staleBanner.style.display = DisplayStyle.None;
                ScanResultStore.Save(_lastResult);
            }
            catch (OperationCanceledException) { /* User canceled, keep existing results */ }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            RenderHeader(_lastResult);
            RenderResults();
            RestoreScrollAfterLayout(scroll);
        }

        /// <summary>Select a group of assets in the Project window (for cases where one finding involves multiple assets, e.g. a duplicate group).</summary>
        private static void SelectGroup(IReadOnlyList<string> paths)
        {
            var objs = new List<UnityEngine.Object>();
            foreach (var p in paths)
            {
                var o = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p);
                if (o != null) objs.Add(o);
            }
            if (objs.Count == 0) return;
            Selection.objects = objs.ToArray();
            EditorGUIUtility.PingObject(objs[0]);
        }

        /// <summary>Export the results under the current filter to CSV — the realistic way to handle the 20k-item scale (sort/batch-process offline in a spreadsheet).</summary>
        private void ExportCsv()
        {
            if (_lastResult == null) { ShowNotification(new GUIContent(L.Tr("Scan first", "请先扫描"))); return; }
            var rows = _lastResult.Findings.Where(PassesFilter).ToList();
            if (rows.Count == 0) { ShowNotification(new GUIContent(L.Tr("Nothing to export under the current filter", "当前筛选下无可导出项"))); return; }

            string path = EditorUtility.SaveFilePanel(L.Tr("Export PerfLint report", "导出 PerfLint 报告"), "", "perflint-report.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine("Severity,Domain,RuleId,Title,Path,Detail");
            foreach (var f in rows)
                sb.AppendLine(string.Join(",",
                    Csv(f.Severity.ToString()), Csv(f.Domain.ToString()), Csv(f.RuleId),
                    Csv(f.Title), Csv(f.TargetPath), Csv(OneLine(f.Detail))));

            try
            {
                // UTF-8 with BOM, to ensure Excel displays Chinese correctly.
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                ShowNotification(new GUIContent(L.Tr($"Exported {rows.Count} rows to CSV", $"已导出 {rows.Count} 条到 CSV")));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(L.Tr("Export failed", "导出失败"), ex.Message, "OK");
            }
        }

        /// <summary>
        /// Export a self-contained, shareable HTML health report (cold-start acquisition hook). The whole report reflects the **full** scan (the score is the overall headline),
        /// unaffected by the current filter — what gets shared is the project's overall health, not some filtered view. Keep zero telemetry: purely local file write, no upload.
        /// </summary>
        private void ExportHtml()
        {
            if (_lastResult == null) { ShowNotification(new GUIContent(L.Tr("Scan first", "请先扫描"))); return; }

            string defaultName = "perflint-report-" + SanitizeFileName(Application.productName) + ".html";
            string path = EditorUtility.SaveFilePanel(L.Tr("Export PerfLint HTML report", "导出 PerfLint HTML 报告"), "", defaultName, "html");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string html = HtmlReport.Build(_lastResult, Application.productName, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                File.WriteAllText(path, html, new UTF8Encoding(false)); // HTML already declares charset=utf-8, no BOM needed
                ShowNotification(new GUIContent(L.Tr("HTML report exported", "已导出 HTML 报告")));
                if (EditorUtility.DisplayDialog(
                        L.Tr("Report exported", "报告已导出"),
                        L.Tr($"Saved to:\n{path}\n\nOpen it now?", $"已保存到：\n{path}\n\n现在打开它？"),
                        L.Tr("Open", "打开"), L.Tr("Close", "关闭")))
                    Application.OpenURL("file://" + path.Replace("\\", "/"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(L.Tr("Export failed", "导出失败"), ex.Message, "OK");
            }
        }

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unity";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Replace(' ', '-');
        }

        private static string Csv(string s)
        {
            s ??= "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");

        // ── Filtering and visual helpers ──────────────────────────────
        private bool PassesFilter(Finding f)
        {
            bool sevOk = f.Severity switch
            {
                Severity.Critical => _showCritical,
                Severity.Warning => _showWarning,
                _ => _showInfo
            };
            if (!sevOk) return false;
            if (_onlyFixable && !f.CanAutoFix) return false;
            if (!string.IsNullOrEmpty(_search))
            {
                string q = _search.Trim();
                bool hit =
                    (f.RuleId?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (f.Title?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (f.TargetPath?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) return false;
            }
            return true;
        }

        /// <summary>
        /// Jump from the runtime panel's "line-by-line analysis": focus the static report on a given script — set the search filter to that script's path and enable the Info filter
        /// (line-level clues like GC004 are Info by default), so the user immediately sees all of that script's line-level findings without digging through tens of thousands.
        /// This closes the loop of "runtime confirms where it's slow → static locates which lines".
        ///
        /// Analyze only this one script and scan nothing else, to guarantee instant results:
        ///   · Full results already exist → use RescanFile to refresh only this file's findings, leaving the rest as is;
        ///   · No results yet → use ScanFileOnly to scan only this script, producing a standalone result (without triggering an 86s-class full scan).
        /// </summary>
        public void FocusOnScript(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath)) return;

            _search = scriptPath;
            _showInfo = true;

            // Do file-level analysis only for this script, avoiding a full scan.
            var ctx = new ScanContext(
                cancellationToken: CancellationToken.None,
                reportProgress: (name, p) => EditorUtility.DisplayProgressBar("PerfLint", $"Analyzing: {name}", p));
            try
            {
                _lastResult = _lastResult == null
                    ? ScanRunner.ScanFileOnly(scriptPath, ctx)
                    : ScanRunner.RescanFile(scriptPath, _lastResult);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (_lastResult != null)
            {
                ScanResultStore.Save(_lastResult);
                RenderHeader(_lastResult);
            }

            // Sync the UI controls (when already built): setting value triggers their respective callbacks → update state + RenderResults.
            if (_searchField != null) _searchField.value = scriptPath;
            if (_infoToggle != null) _infoToggle.value = true;
            // Controls not ready, or the value above didn't change when set (no callback fired) → render once proactively.
            RenderResults();

            if (_lastResult == null)
                ShowNotification(new GUIContent(L.Tr("No file-level analyzer claims this script (not a runtime script?)", "该脚本无文件级行分析器认领（非运行时脚本？）")));
        }

        private static VisualElement MakeDivider() => new VisualElement
        {
            style = { height = 1, backgroundColor = new Color(1, 1, 1, 0.1f), marginTop = 4, marginBottom = 4 }
        };

        private static Color SeverityColor(Severity s) => s switch
        {
            Severity.Critical => new Color(0.93f, 0.30f, 0.30f),
            Severity.Warning => new Color(0.95f, 0.70f, 0.20f),
            _ => new Color(0.45f, 0.65f, 0.95f)
        };

        private static Color ScoreColor(int score)
        {
            if (score >= 75) return new Color(0.40f, 0.80f, 0.45f);
            if (score >= 50) return new Color(0.95f, 0.70f, 0.20f);
            return new Color(0.93f, 0.30f, 0.30f);
        }
    }
}
