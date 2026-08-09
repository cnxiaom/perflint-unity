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
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 12; root.style.paddingBottom = 10;
            root.style.paddingLeft = 14; root.style.paddingRight = 14;

            // Memory is scene-scoped ("build this scene and feel it"); build size is project-wide by nature.
            root.Add(new Label(_plan.Dimension == SavingsDimension.Memory
                ? L.Tr($"Up to ~{ScannerUtil.Human(_plan.TotalSavingsBytes)} of memory reclaimable in the open scene(s) (estimate)",
                       $"当前场景预计最多可回收内存约 {ScannerUtil.Human(_plan.TotalSavingsBytes)}（估算）")
                : L.Tr($"Up to ~{ScannerUtil.Human(_plan.TotalSavingsBytes)} of build size reclaimable (estimate)",
                       $"预计最多可回收包体约 {ScannerUtil.Human(_plan.TotalSavingsBytes)}（估算）"))
            {
                style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6,
                          whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink }
            });

            // What the headline figure is NOT. It counts what this dialog can run, which is correct and, on its own,
            // misleading by an order of magnitude: measured on a real project the build plan led with "up to ~428.4 MB"
            // while the manual tier below it held 4.5 GB. Reading only the big number, 428 MB looks like the whole
            // opportunity — the same shape of gap that put the manual tier in this dialog in the first place ("29.8 MB
            // next to the button, 0.27 MB inside it"), now inverted and living in the headline.
            //
            // Its own line rather than appended to the title: the title states what a click delivers and must not turn
            // into a sentence with a "but" in it. Shown only when the manual tier has something in it.
            if (_plan.ManualSavingsBytes > 0)
                root.Add(new Label(L.Tr($"Another ~{ScannerUtil.Human(_plan.ManualSavingsBytes)} needs manual work — no one-click for it. Listed at the bottom.",
                                        $"另有约 {ScannerUtil.Human(_plan.ManualSavingsBytes)} 需手动处理——那部分没有一键，列在下方。"))
                {
                    style = { fontSize = 12, marginBottom = 8, whiteSpace = WhiteSpace.Normal,
                              color = PerfLintStyle.Dim }
                });

            // minHeight = 0 alongside flexGrow: a flex child's min-height defaults to auto, so a ScrollView holding
            // three full tiers refuses to shrink and pushes the footer — the button that runs the batch — off the
            // bottom of a 260 px window. Same fix, same reason, as the AI Fix review list.
            var scroll = new ScrollView { style = { flexGrow = 1, minHeight = 0 } };
            root.Add(scroll);

            // ── Auto tier ─────────────────────────────────────────
            if (_plan.AutoItems.Count > 0)
            {
                var box = MakeSectionBox(scroll);
                int autoCount = _plan.AutoItems.Count; // "1 safe fixes" was on screen until this line existed
                box.Add(SectionHead(L.Tr($"Runs automatically: {autoCount} safe {(autoCount == 1 ? "fix" : "fixes")} ≈ ~{ScannerUtil.Human(_plan.AutoSavingsBytes)}",
                                         $"将自动执行：{autoCount} 项安全修复 ≈ 约 {ScannerUtil.Human(_plan.AutoSavingsBytes)}")));
                box.Add(Note(L.Tr("Import-settings changes only. Edit > Undo does not revert them — commit to version control first.",
                                  "均为导入设置类改动。Edit > Undo 撤销不了——请先提交版本控制。")));
                foreach (var g in _plan.AutoItems.GroupBy(f => f.GroupTitleOrTitle)
                                                 .Select(x => new { Title = x.Key, Count = x.Count(), Bytes = x.Sum(f => OptimizePlan.SavingsOf(f, _plan.Dimension)) })
                                                 .OrderByDescending(x => x.Bytes)
                                                 .Take(6))
                {
                    box.Add(Item($"· {g.Title} ×{g.Count} ≈ ~{ScannerUtil.Human(g.Bytes)}"));
                }
            }

            // ── Decision tier ─────────────────────────────────────
            if (_plan.DecisionGroups.Count > 0)
            {
                var box = MakeSectionBox(scroll);
                box.Add(SectionHead(L.Tr($"Your call (off by default) — up to another ~{ScannerUtil.Human(_plan.DecisionSavingsBytes)}:",
                                         $"需要你决定（默认不执行）——最多可再省约 {ScannerUtil.Human(_plan.DecisionSavingsBytes)}：")));
                box.Add(Note(L.Tr("Each checked item shows its own confirmation before running.",
                                  "勾选的项执行前会再弹出该操作自己的确认框。")));
                foreach (var g in _plan.DecisionGroups)
                {
                    string count = g.Findings.Count > 1 ? $" ×{g.Findings.Count}" : "";
                    // `text`, not the constructor's label. They are two different things in UIElements and they land
                    // on opposite sides: the constructor sets the FIELD label, which BaseField draws in a column
                    // BEFORE the input — so a whole sentence ended up to the left of its own checkbox, and you read
                    // "Duplicate asset (2 identical copies) ×25 ≈ ~15.5 MB [ ]". BaseBoolField.text draws inside the
                    // input, after the checkmark, which is where a checkbox's label belongs.
                    var t = new Toggle
                    {
                        text = $"{g.Label}{count} ≈ ~{ScannerUtil.Human(g.SavingsBytes)}",
                        value = false,
                        style = { marginTop = 6, whiteSpace = WhiteSpace.Normal }
                    };
                    WrapToggleText(t);
                    t.RegisterValueChangedCallback(_ => RefreshStartButton());
                    box.Add(t);
                    // Said before the click, not discovered during it. A group restored from disk carries no runnable
                    // action, so running it re-scans the rule first, and what that costs is per-rule — see
                    // OptimizePlan.RescanNoteFor, which exists because one sentence for all of them was wrong.
                    if (g.NeedsRevive)
                        box.Add(SubNote(OptimizePlan.RescanNoteFor(g.RuleId), PerfLintStyle.Dimmer));
                    // Amber, and it survives the "no text in the colour of its block" rule because the block it sits
                    // in is a NEUTRAL card: a caveat that disagrees with what it is written on is exactly what colour
                    // is for. It would be wrong inside an amber note, which is why that one is a shared class.
                    if (!string.IsNullOrEmpty(g.Caution))
                        box.Add(SubNote(g.Caution, PerfLintStyle.Amber));
                    _choices.Add((t, g));
                }
            }

            // ── Manual tier (informational) ───────────────────────
            // Report-only findings with estimates: not runnable, but they ARE counted in the panel's scene figure,
            // so omitting them here made "29.8 MB next to the button, 0.27 MB in the dialog" (user-reported gap).
            if (_plan.ManualGroups.Count > 0)
            {
                // No blanket opacity on the box. Fading a whole card fades its heading and its figures along with
                // everything else, and on the light skin it fades them towards the page. The tier is quieter because
                // its text is a step dimmer, which is a decision about text rather than about the block.
                var box = MakeSectionBox(scroll);
                box.Add(SectionHead(L.Tr($"Needs manual work — up to another ~{ScannerUtil.Human(_plan.ManualSavingsBytes)} (no one-click):",
                                         $"需手动处理——最多还可省约 {ScannerUtil.Human(_plan.ManualSavingsBytes)}（无一键）：")));
                box.Add(Note(L.Tr("These fixes are judgment calls (resizing, restructuring…) — find the items in the results list, each explains its fix.",
                                  "这些修复需要人工决策（改尺寸、调结构等）——请在结果列表中查看对应条目，每条都写明了修法。")));
                foreach (var m in _plan.ManualGroups.Take(6))
                {
                    box.Add(Item($"· {m.Title} ×{m.Count} ≈ ~{ScannerUtil.Human(m.SavingsBytes)}", PerfLintStyle.Dimmer));
                }
            }

            // ── Footer ────────────────────────────────────────────
            //
            // Cancel first, then the primary at the end of the row: the destination of a rightward read, and the
            // order every confirm dialog in the editor uses. They were the other way round, which put the button
            // that runs the batch in the middle of the row and the escape hatch at its edge.
            var footer = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd,
                          alignItems = Align.Center, marginTop = 10, flexShrink = 0, flexWrap = Wrap.Wrap }
            };
            var cancel = PerfLintStyle.Secondary(L.Tr("Cancel", "取消"), Close);
            cancel.style.flexShrink = 0;
            footer.Add(cancel);
            _startButton = PerfLintStyle.Primary(L.Tr("Start optimizing", "开始优化"), OnStart);
            _startButton.style.marginLeft = 8;
            _startButton.style.flexShrink = 0;
            footer.Add(_startButton);
            root.Add(footer);
            RefreshStartButton();
        }

        // ── small UI helpers ──
        //
        // Tints rather than opacities, and one definition per role so six labels cannot drift into six sizes. Every
        // one of these was written inline three times over, at 10 / 11 / 12 px with 0.65 / 0.7 / 0.75 opacity.

        /// <summary>A tier's heading: what this block of the plan is and what it is worth.</summary>
        private static Label SectionHead(string text) => new Label(text)
        {
            style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, whiteSpace = WhiteSpace.Normal,
                      color = PerfLintStyle.Ink }
        };

        /// <summary>The line under a heading that says what the tier means for you.</summary>
        private static Label Note(string text) => new Label(text)
        {
            style = { fontSize = 11, marginTop = 2, whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Dimmer }
        };

        /// <summary>One rule's line inside a tier.</summary>
        private static Label Item(string text, Color? tint = null) => new Label(text)
        {
            style = { fontSize = 11, marginTop = 2, whiteSpace = WhiteSpace.Normal, color = tint ?? PerfLintStyle.Dim }
        };

        /// <summary>A line that belongs to the checkbox above it — indented past the box, in its own tint.</summary>
        private static Label SubNote(string text, Color tint) => new Label(text)
        {
            style = { fontSize = 11, marginLeft = 18, marginTop = 1, whiteSpace = WhiteSpace.Normal, color = tint }
        };

        /// <summary>
        /// Makes the text beside a checkbox wrap instead of running off the right edge.
        ///
        /// Every Label under the toggle that actually carries text, rather than <c>labelElement</c>: with
        /// <c>Toggle.text</c> the writing lives in a Label inside the input row and <c>labelElement</c> is the empty
        /// field label, so styling that one would silently do nothing. Guarded and never assumed — on 2021.3 a
        /// query straight after construction has come back empty before, and the failure that would cause here is
        /// only "a long line does not wrap" (the checkbox is first now, so it can no longer be pushed out of reach).
        /// </summary>
        private static void WrapToggleText(Toggle t)
        {
            foreach (var lbl in t.Query<Label>().ToList())
            {
                if (string.IsNullOrEmpty(lbl.text)) continue;
                lbl.style.whiteSpace = WhiteSpace.Normal;
                lbl.style.flexShrink = 1;
                lbl.style.minWidth = 0;
                lbl.style.color = PerfLintStyle.Ink;
            }
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

        /// <summary>
        /// One tier of the plan, on the shared card surface.
        ///
        /// It used to be a white overlay at 0.03 — an ADDITION to whatever is behind it, so it lifts a dark editor
        /// by a hair and is invisible on a light one, where the page is already white. The shared card takes the
        /// theme's own helpbox fill and is correct on both.
        /// </summary>
        private static VisualElement MakeSectionBox(VisualElement parent)
        {
            var box = PerfLintStyle.Card(8);
            parent.Add(box);
            return box;
        }
    }
}
