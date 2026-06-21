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
            root.style.paddingTop = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingBottom = 8;

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
                style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold }
            });
            root.Add(new Label(L.Tr(
                "Review each diff, then apply. Applying writes to files; commit to version control first. Applied fixes are still background-verified and auto-rolled back on compile failure.",
                "逐条审阅 diff 后应用。应用会写入文件，建议先提交版本控制。已应用的仍会后台校验、编译失败自动回滚。"))
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, fontSize = 11, marginTop = 2, marginBottom = 4 }
            });

            // minHeight=0 is critical: flex children default to min-height:auto, so a flexGrow=1 ScrollView won't shrink
            // below its content; dozens of cards would fill it up and push the bottom action bar off-screen (the user
            // wouldn't see the "Apply" button). Setting 0 forces it to shrink and scroll internally.
            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            scroll.contentContainer.style.paddingRight = 14;
            foreach (var c in _candidates) scroll.Add(BuildCard(c));
            root.Add(scroll);

            // Bottom action bar. flexShrink=0: always keeps its full height, never squeezed out by the scroll area above.
            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6, flexShrink = 0 } };
            footer.Add(new VisualElement { style = { flexGrow = 1 } });
            var cancel = new Button(Close) { text = L.Tr("Cancel", "取消") };
            footer.Add(cancel);
            _applyButton = new Button(ApplySelected);
            _applyButton.style.marginLeft = 6;
            footer.Add(_applyButton);
            root.Add(footer);

            RefreshApplyButton();
        }

        private VisualElement BuildCard(AiFixCandidate c)
        {
            var card = new VisualElement
            {
                style =
                {
                    marginBottom = 6, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = new Color(1, 1, 1, 0.04f),
                    borderLeftWidth = 2, borderLeftColor = new Color(0.95f, 0.70f, 0.20f)
                }
            };

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
            var title = new Label(loc) { style = { whiteSpace = WhiteSpace.Normal, flexGrow = 1 } };
            if (!applicable) title.style.opacity = 0.6f;
            headRow.Add(title);
            card.Add(headRow);

            _rows.Add((c, toggle));

            // Skip reason (grayed out) or diff block.
            if (!applicable)
            {
                card.Add(new Label("⏭ " + SkipReason(skip, p))
                {
                    style = { whiteSpace = WhiteSpace.Normal, opacity = 0.6f, marginTop = 4, marginLeft = 22 }
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
