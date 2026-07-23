using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// The one-click optimize plan dialog (per savings dimension). Beginner-shaped on purpose: one headline number,
    /// an auto tier that runs with this dialog as its single confirmation, and a decision tier of opt-in checkboxes —
    /// each a rule-level group with a one-line plain-language trade-off. Checked decision items still go through their
    /// action's normal confirmation flow (irreversible operations keep their own consent wording; this dialog never
    /// bypasses it). Execution and the verified "optimized ~X" accounting live in PerfLintWindow.RunOptimizePlan.
    /// </summary>
    internal sealed class PerfLintOptimizeWindow : EditorWindow
    {
        private PerfLintWindow _owner;
        private OptimizePlan _plan;
        private readonly List<(Toggle toggle, OptimizePlan.DecisionGroup group)> _choices = new List<(Toggle, OptimizePlan.DecisionGroup)>();
        private Button _startButton;

        public static void Open(PerfLintWindow owner, OptimizePlan plan)
        {
            var w = CreateInstance<PerfLintOptimizeWindow>();
            w._owner = owner;
            w._plan = plan;
            w.titleContent = new GUIContent(plan.Dimension == SavingsDimension.Memory
                ? L.Tr("PerfLint — Optimize Memory", "PerfLint — 一键优化内存")
                : L.Tr("PerfLint — Optimize Build Size", "PerfLint — 一键优化包体"));
            w.minSize = new Vector2(480, 260);
            w.BuildUi();
            w.ShowUtility();
        }

        private void BuildUi()
        {
            var root = rootVisualElement;
            root.Clear();
            _choices.Clear();
            root.style.paddingTop = 10; root.style.paddingBottom = 10;
            root.style.paddingLeft = 12; root.style.paddingRight = 12;

            // Memory is scene-scoped ("build this scene and feel it"); build size is project-wide by nature.
            root.Add(new Label(_plan.Dimension == SavingsDimension.Memory
                ? L.Tr($"Up to ~{ScannerUtil.Human(_plan.TotalSavingsBytes)} of memory reclaimable in the open scene(s) (estimate)",
                       $"当前场景预计最多可回收内存约 {ScannerUtil.Human(_plan.TotalSavingsBytes)}（估算）")
                : L.Tr($"Up to ~{ScannerUtil.Human(_plan.TotalSavingsBytes)} of build size reclaimable (estimate)",
                       $"预计最多可回收包体约 {ScannerUtil.Human(_plan.TotalSavingsBytes)}（估算）"))
            {
                style = { fontSize = 15, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 8, whiteSpace = WhiteSpace.Normal }
            });

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            root.Add(scroll);

            // ── Auto tier ─────────────────────────────────────────
            if (_plan.AutoItems.Count > 0)
            {
                var box = MakeSectionBox(scroll);
                box.Add(new Label(L.Tr($"Runs automatically: {_plan.AutoItems.Count} safe fixes ≈ ~{ScannerUtil.Human(_plan.AutoSavingsBytes)}",
                                       $"将自动执行：{_plan.AutoItems.Count} 项安全修复 ≈ 约 {ScannerUtil.Human(_plan.AutoSavingsBytes)}"))
                {
                    style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, whiteSpace = WhiteSpace.Normal }
                });
                box.Add(new Label(L.Tr("Import-settings changes only — undoable via Edit > Undo. Commit to version control first.",
                                       "均为导入设置类改动，可通过 Edit > Undo 撤销。建议先提交版本控制。"))
                {
                    style = { fontSize = 10, opacity = 0.65f, whiteSpace = WhiteSpace.Normal, marginTop = 2 }
                });
                foreach (var g in _plan.AutoItems.GroupBy(f => f.GroupTitleOrTitle)
                                                 .Select(x => new { Title = x.Key, Count = x.Count(), Bytes = x.Sum(f => OptimizePlan.SavingsOf(f, _plan.Dimension)) })
                                                 .OrderByDescending(x => x.Bytes)
                                                 .Take(6))
                {
                    box.Add(new Label($"· {g.Title} ×{g.Count} ≈ ~{ScannerUtil.Human(g.Bytes)}")
                    {
                        style = { fontSize = 11, marginTop = 2, whiteSpace = WhiteSpace.Normal }
                    });
                }
            }

            // ── Decision tier ─────────────────────────────────────
            if (_plan.DecisionGroups.Count > 0)
            {
                var box = MakeSectionBox(scroll);
                box.Add(new Label(L.Tr($"Your call (off by default) — up to another ~{ScannerUtil.Human(_plan.DecisionSavingsBytes)}:",
                                       $"需要你决定（默认不执行）——最多可再省约 {ScannerUtil.Human(_plan.DecisionSavingsBytes)}："))
                {
                    style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, whiteSpace = WhiteSpace.Normal }
                });
                box.Add(new Label(L.Tr("Each checked item shows its own confirmation before running.",
                                       "勾选的项执行前会再弹出该操作自己的确认框。"))
                {
                    style = { fontSize = 10, opacity = 0.65f, whiteSpace = WhiteSpace.Normal, marginTop = 2 }
                });
                foreach (var g in _plan.DecisionGroups)
                {
                    string count = g.Findings.Count > 1 ? $" ×{g.Findings.Count}" : "";
                    var t = new Toggle($"{g.Label}{count} ≈ ~{ScannerUtil.Human(g.SavingsBytes)}")
                    {
                        value = false,
                        style = { marginTop = 6, whiteSpace = WhiteSpace.Normal }
                    };
                    t.RegisterValueChangedCallback(_ => RefreshStartButton());
                    box.Add(t);
                    if (!string.IsNullOrEmpty(g.Caution))
                    {
                        box.Add(new Label(g.Caution)
                        {
                            style = { fontSize = 10, opacity = 0.7f, whiteSpace = WhiteSpace.Normal, marginLeft = 18, color = new Color(0.95f, 0.78f, 0.30f) }
                        });
                    }
                    _choices.Add((t, g));
                }
            }

            // ── Manual tier (informational) ───────────────────────
            // Report-only findings with estimates: not runnable, but they ARE counted in the panel's scene figure,
            // so omitting them here made "29.8 MB next to the button, 0.27 MB in the dialog" (user-reported gap).
            if (_plan.ManualGroups.Count > 0)
            {
                var box = MakeSectionBox(scroll);
                box.style.opacity = 0.8f;
                box.Add(new Label(L.Tr($"Needs manual work — up to another ~{ScannerUtil.Human(_plan.ManualSavingsBytes)} (no one-click):",
                                       $"需手动处理——最多还可省约 {ScannerUtil.Human(_plan.ManualSavingsBytes)}（无一键）："))
                {
                    style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, whiteSpace = WhiteSpace.Normal }
                });
                box.Add(new Label(L.Tr("These fixes are judgment calls (resizing, restructuring…) — find the items in the results list, each explains its fix.",
                                       "这些修复需要人工决策（改尺寸、调结构等）——请在结果列表中查看对应条目，每条都写明了修法。"))
                {
                    style = { fontSize = 10, opacity = 0.65f, whiteSpace = WhiteSpace.Normal, marginTop = 2 }
                });
                foreach (var m in _plan.ManualGroups.Take(6))
                {
                    box.Add(new Label($"· {m.Title} ×{m.Count} ≈ ~{ScannerUtil.Human(m.SavingsBytes)}")
                    {
                        style = { fontSize = 11, marginTop = 2, whiteSpace = WhiteSpace.Normal }
                    });
                }
            }

            // ── Footer ────────────────────────────────────────────
            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 10, flexShrink = 0 } };
            _startButton = new Button(OnStart) { text = L.Tr("Start optimizing", "开始优化") };
            var cancel = new Button(Close) { text = L.Tr("Cancel", "取消") };
            footer.Add(_startButton);
            footer.Add(cancel);
            root.Add(footer);
            RefreshStartButton();
        }

        /// <summary>Enabled only when the run would actually do something (auto items exist or at least one decision item is checked).</summary>
        private void RefreshStartButton()
        {
            if (_startButton == null) return;
            _startButton.SetEnabled(_plan.AutoItems.Count > 0 || _choices.Any(c => c.toggle.value));
        }

        private void OnStart()
        {
            var chosen = _choices.Where(c => c.toggle.value).Select(c => c.group).ToList();
            var owner = _owner;
            var plan = _plan;
            Close(); // close first — the run pops its own progress bars / confirmation dialogs
            if (owner != null) owner.RunOptimizePlan(plan, chosen);
        }

        private static VisualElement MakeSectionBox(VisualElement parent)
        {
            var box = new VisualElement
            {
                style =
                {
                    marginBottom = 8, paddingTop = 8, paddingBottom = 8, paddingLeft = 10, paddingRight = 10,
                    backgroundColor = new Color(1f, 1f, 1f, 0.03f),
                    borderTopLeftRadius = 8, borderTopRightRadius = 8,
                    borderBottomLeftRadius = 8, borderBottomRightRadius = 8,
                }
            };
            parent.Add(box);
            return box;
        }
    }
}
