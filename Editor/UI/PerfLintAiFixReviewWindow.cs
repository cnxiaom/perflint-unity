using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Llm;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>A candidate for AI batch fixing: the original finding (used to show title/location) plus the generated proposal.</summary>
    public sealed class AiFixCandidate
    {
        public Finding Finding;
        public ScriptFixProposal Proposal;
    }

    /// <summary>
    /// AI batch fix review window (since [0.21.x]). Changes "AI Fix All" from the old "generate each one and
    /// auto-write to disk" to "generate all → review each diff here and check them → write only the checked ones
    /// after confirmation", handing the "breaks something" risk back to the user to confirm on the diff.
    ///
    /// This window only handles display and selection, **it does not write files** — after the user confirms,
    /// the selected proposals are handed back to the main panel (onApply) for unified application; writing /
    /// incremental rescan / compile-check rollback are still handled by the main panel's existing logic (the
    /// same path as a single AI Fix).
    /// </summary>
    public sealed class PerfLintAiFixReviewWindow : EditorWindow
    {
        private string _ruleId;
        private List<AiFixCandidate> _candidates;
        private Action<List<ScriptFixProposal>> _onApply;
        private readonly List<(AiFixCandidate c, Toggle t)> _rows = new List<(AiFixCandidate, Toggle)>();
        private Button _applyButton;

        public static void Open(string ruleId, List<AiFixCandidate> candidates, Action<List<ScriptFixProposal>> onApply)
        {
            var w = CreateInstance<PerfLintAiFixReviewWindow>();
            w.titleContent = new GUIContent(L.Tr("AI Fix — Review", "AI 修复 — 审阅"));
            w._ruleId = ruleId;
            w._candidates = candidates ?? new List<AiFixCandidate>();
            w._onApply = onApply;
            w.minSize = new Vector2(540, 420);
            w.BuildUi();
            w.ShowUtility();
        }

        private void BuildUi()
        {
            var root = rootVisualElement;
            root.Clear();
            _rows.Clear(); // BuildUi is callable twice on one instance; stale rows would let a dead toggle vote on Apply
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 12;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingBottom = 10;

            int applicable = _candidates.Count(c => AiFixBatch.IsApplicable(c.Proposal));
            int skipped = _candidates.Count - applicable;
            int flagged = _candidates.Count(c => AiFixBatch.IsApplicable(c.Proposal) && c.Proposal != null && c.Proposal.BehaviorRisk);

            string flaggedNote = flagged > 0
                ? L.Tr($" {flagged} flagged for possible behavior change (left unchecked — review the ⚠ note).",
                       $" 其中 {flagged} 条经 AI 自检疑似改变行为（默认未勾，请看⚠说明）。")
                : "";
            root.Add(new Label(L.Tr(
                $"Rule {_ruleId}: {applicable} applicable fix(es), {skipped} skipped. Only the checked ones will be written.",
                $"规则 {_ruleId}：{applicable} 条可应用，{skipped} 条跳过。仅勾选的会被写入。") + flaggedNote)
            {
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16,
                          color = PerfLintStyle.Ink }
            });
            root.Add(new Label(L.Tr(
                "Review each diff, then apply. Applying writes to files; commit to version control first. Applied fixes are still background-verified and auto-rolled back on compile failure.",
                "逐条审阅 diff 后应用。应用会写入文件，建议先提交版本控制。已应用的仍会后台校验、编译失败自动回滚。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 4, marginBottom = 6,
                          color = PerfLintStyle.Dim }
            });

            // minHeight=0 is critical: flex children default to min-height:auto, so a flexGrow=1 ScrollView won't shrink
            // below its content; dozens of cards would fill it up and push the bottom action bar off-screen (the user
            // wouldn't see the "Apply" button). Setting 0 forces it to shrink and scroll internally.
            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            scroll.contentContainer.style.paddingRight = 14;
            foreach (var c in _candidates) scroll.Add(BuildCard(c));
            root.Add(scroll);

            // Bottom action bar. flexShrink=0: always keeps its full height, never squeezed out by the scroll area above.
            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8, flexShrink = 0 } };
            footer.Add(new VisualElement { style = { flexGrow = 1 } });
            var cancel = PerfLintStyle.Secondary(L.Tr("Cancel", "取消"), Close);
            cancel.style.flexShrink = 0;
            footer.Add(cancel);
            _applyButton = PerfLintStyle.Primary("", ApplySelected); // label carries the count — see RefreshApplyButton
            _applyButton.style.marginLeft = 8;
            _applyButton.style.flexShrink = 0;
            footer.Add(_applyButton);
            root.Add(footer);

            RefreshApplyButton();
        }

        private VisualElement BuildCard(AiFixCandidate c)
        {
            // The shared card. It used to be a white overlay at 0.04 with a 2 px amber stripe down the left edge —
            // the same fixed amber on EVERY card, so the stripe distinguished nothing and told you only that this
            // was a card. State is carried by what is written in the card (the skip line, the flagged banner), and
            // the surface is the theme's own.
            var card = PerfLintStyle.Card(8);
            card.style.marginBottom = 6;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;

            var p = c.Proposal;
            var skip = AiFixBatch.Classify(p);
            bool applicable = skip == AiFixBatch.Skip.None;

            // Header row: checkbox + title/location. Non-applicable ones are disabled and grayed out; ones flagged as risky by the semantic self-check are checkable but unchecked by default (the diff has a ⚠ note).
            var headRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };
            var toggle = new Toggle { value = AiFixBatch.ShouldDefaultCheck(p) };
            toggle.SetEnabled(applicable);
            toggle.style.marginRight = 6;
            toggle.RegisterValueChangedCallback(_ => RefreshApplyButton());
            headRow.Add(toggle);

            string loc = c.Finding != null
                ? $"{c.Finding.Title}  —  {ShortPath(c.Finding.CodeFile)}:{c.Finding.CodeLine}"
                : ShortPath(p?.FilePath);
            // A tint, not an opacity: fading a label pushes it towards whatever is behind it, and on the light skin
            // that is a near-white page. A skipped candidate is written in the quiet tint; an applicable one in ink.
            var title = new Label(loc)
            {
                style = { whiteSpace = WhiteSpace.Normal, flexGrow = 1, flexShrink = 1, minWidth = 0,
                          color = applicable ? PerfLintStyle.Ink : PerfLintStyle.Dimmer }
            };
            headRow.Add(title);
            card.Add(headRow);

            _rows.Add((c, toggle));

            // Skip reason (quiet) or diff block.
            if (!applicable)
            {
                // No leading glyph. The one that was here (U+23ED, "skip to next") is not in the 2021/2022 editor
                // font — it is not EMOJI, so the glyph guard's ranges never caught it, and it would have shipped as
                // a tofu box on exactly the editors we support and never on the one it was written against. The
                // sentence after it already says the fix was skipped.
                card.Add(new Label(SkipReason(skip, p))
                {
                    style = { whiteSpace = WhiteSpace.Normal, marginTop = 4, marginLeft = 22,
                              color = PerfLintStyle.Dimmer }
                });
                // When it can't be located, still show the diff for manual reference; for generation failure / no change needed it's pointless.
                if (skip == AiFixBatch.Skip.NotLocatable && p != null && p.Ok)
                {
                    var diff = new VisualElement { style = { marginLeft = 22 } };
                    AiFixDiffView.BuildDiffBlocks(diff, p);
                    card.Add(diff);
                }
            }
            else
            {
                var diff = new VisualElement { style = { marginLeft = 22 } };
                AiFixDiffView.BuildDiffBlocks(diff, p);
                card.Add(diff);
            }

            return card;
        }

        private void RefreshApplyButton()
        {
            int sel = _rows.Count(r => r.t.value);
            _applyButton.text = $"{L.Tr("Apply selected", "应用勾选项")} ({sel})";
            _applyButton.SetEnabled(sel > 0);
        }

        private void ApplySelected()
        {
            var selected = _rows.Where(r => r.t.value && AiFixBatch.IsApplicable(r.c.Proposal))
                                 .Select(r => r.c.Proposal)
                                 .ToList();
            var cb = _onApply;
            Close();
            cb?.Invoke(selected);
        }

        private static string SkipReason(AiFixBatch.Skip skip, ScriptFixProposal p)
        {
            switch (skip)
            {
                case AiFixBatch.Skip.GenFailed:
                    return L.Tr("Generation failed: ", "生成失败：") + (p?.Error ?? L.Tr("the model didn't return a usable fix", "模型未返回可用修复"));
                case AiFixBatch.Skip.NoChange:
                    return L.Tr("AI judged no change is needed (likely a false positive).", "AI 判断无需改动（可能是误报）。");
                case AiFixBatch.Skip.NotLocatable:
                    return L.Tr("Couldn't locate the original snippet in the file; apply manually.", "无法在文件中定位原始片段，请手动应用。");
                default:
                    return "";
            }
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            return Path.GetFileName(path);
        }
    }
}
