using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// The look of a finding, and the row of things you can do to it — in one place, for every panel.
    ///
    /// It began as a shell the Autopilot could share with the two older panels while they went on drawing their own
    /// copy of it. They no longer do: this is what a finding looks like in PerfLint, and the last of the parallel
    /// versions is gone along with the third copy of the severity palette.
    ///
    /// The card is <b>washed in its severity</b> rather than marked with a 3 px stripe down its left edge. Same fact,
    /// seen first instead of noticed last — and it is what stops a list reading as a stack of identical grey slabs.
    /// Both the wash and the surface under it come from <see cref="PerfLintStyle"/>, so they follow the editor skin;
    /// the fill this replaces was <c>rgba(255,255,255,0.035)</c>, which is invisible on a light one.
    /// </summary>
    public static class FindingCardUI
    {
        public static Color SeverityColor(Severity s) => PerfLintStyle.SeverityColor(s);

        /// <summary>The card shell: the theme's own surface, washed and ruled in the finding's severity.</summary>
        public static VisualElement Card(Severity severity)
        {
            var card = new VisualElement
            {
                style =
                {
                    marginTop = 6,
                    paddingTop = 8, paddingBottom = 8, paddingLeft = 10, paddingRight = 8
                }
            };
            card.AddToClassList("pl-card");
            card.AddToClassList(PerfLintStyle.SeverityClass(severity));
            PerfLintStyle.Round(card, 8);
            return card;
        }

        /// <summary>The severity dot the other panels lead every card with. A text glyph the editor fonts all have — never an emoji.</summary>
        public static Label Dot(Severity severity) => new Label("●")
        {
            style = { color = SeverityColor(severity), marginRight = 6, minWidth = 12, flexShrink = 0 }
        };

        /// <summary>What a card is allowed to offer, and what to do when the project changes underneath it.</summary>
        public sealed class Context
        {
            /// <summary>The scan the finding came from — needed to revive a fix that did not survive disk.</summary>
            public ScanResult Scan;
            /// <summary>
            /// The whole diagnosis, when the surface has one. Only needed to answer "where do I look" for a finding
            /// with no location, which needs to know what else was measured and scanned before it can offer anywhere.
            /// </summary>
            public CurrentDiagnosis Diagnosis;
            /// <summary>Called after a fix is applied, with the message to show and the rescanned result.</summary>
            public Action<string, ScanResult> OnApplied;

        }

        /// <summary>
        /// The buttons for one finding, in the order the other panels use them.
        ///
        /// Only what can actually be done appears. A one-click fix is offered even when the finding came back from
        /// disk without its delegate — <see cref="FindingActions.ApplyRule"/> revives it first, which is the
        /// difference between a button that works after a domain reload and one that silently does nothing.
        /// </summary>
        public static VisualElement Actions(Finding f, Context ctx)
        {
            var row = new VisualElement
            { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0 } };
            if (f == null) return row;

            var loc = FindingActions.LocationOf(f);

            // How many places this rule covers. A ranked row stands for its whole RULE, so revealing the one finding
            // the ranking happened to keep sent somebody looking at one of twenty-four uncompressed textures with no
            // way to reach the other twenty-three.
            int places = ctx?.Scan != null ? FindingActions.PathsFor(f.RuleId, ctx.Scan).Count : 0;
            int fixablePlaces = ctx?.Scan != null ? FindingActions.FixablePlacesFor(f.RuleId, ctx.Scan) : 0;

            // Decided up front because it is what the buttons above have to avoid duplicating. The decision tier ends
            // this row with "Do this in full panel" -> FocusOnRule(rule); Locate lands on exactly that for an asset
            // finding, and WhereToLook's last resort is literally the same call. On the Autopilot's decision rows all
            // three appeared at once — "定位 / 查看详情 / 去完整面板处理 都是一个意思", reported by Tim, and he was
            // right: three labels, one destination. Whichever others are available, the one that says what you can DO
            // there is the one worth keeping.
            bool handsOffToPanel = !(ctx != null && FindingActions.IsFixable(f)) && FindingActions.NeedsDecision(f);

            // Where Locate actually lands, decided by the same rule RevealRule uses — a line opens the file, a scene
            // object gets selected, and anything that is an asset FILE goes to the panel, because the subject of
            // those findings is an import setting the file itself does not name.
            bool locateOpensPanel = !loc.HasLine && loc.HasPath;

            if ((f.Ping != null || loc.HasPath) && !(locateOpensPanel && handsOffToPanel))
            {
                string rid = f.RuleId;
                var scan = ctx?.Scan;
                bool opensPanel = locateOpensPanel;

                var locate = new Button(() => FindingActions.RevealRule(rid, scan, f))
                {
                    // Naming the line is the point: it is the difference between "somewhere in this project" and a
                    // place to put the cursor, and the finding knew it all along. Anything else keeps the plain word
                    // and lets the tooltip say where it lands.
                    text = loc.HasLine && places <= 1
                         ? L.Tr($"Open {loc.Display}", $"打开 {loc.Display}")
                         : L.Tr("Locate", "定位"),
                    // A tooltip that was just the asset path described a destination this no longer goes to.
                    tooltip = opensPanel ? LocateRuleTooltip(places, fixablePlaces)
                            : loc.HasPath ? loc.Path : null
                };
                locate.style.marginLeft = 4;
                row.Add(locate);
            }

            // Nothing to Locate: a whole-frame measurement. Offer the place its own detail text points at instead of
            // leaving the most important cards on the screen with no button at all.
            if (f.Ping == null && !loc.HasPath && ctx?.Diagnosis != null)
            {
                var next = FindingActions.WhereToLook(f, ctx.Diagnosis);
                // Its last-resort route is FocusOnRule on this very rule. When the row already ends in a button that
                // goes there and says what it is for, "Read the details" is the same click with a vaguer promise.
                if (next.Exists && !(next.OpensPanelOnThisRule && handsOffToPanel))
                {
                    var go = new Button(next.Go) { text = next.Label, tooltip = next.Tooltip };
                    go.style.marginLeft = 4;
                    row.Add(go);
                }
            }

            if (loc.IsScript)
            {
                // The label names the FILE, not just the action. On a card headed "RUN.HOT001" a bare
                // "Line-level analysis" reads as "analyse this runtime rule", and the panel it opens holds only the
                // static scan — so the click looks like it went to the wrong place. Naming the file says what is
                // actually about to be analysed, and incidentally that it is not the rule.
                //
                // Reported by Tim on a card pair where items 2 and 3 were both RUN.*: "只是条目2/3 都是 RUN 开头,
                // 因为 Scan 中没有 Runtime 的部分, 让用户很容易想到应该跳 Runtime 面板?" — the expectation was
                // reasonable and the label was what created it.
                string fileName = System.IO.Path.GetFileName(loc.Path);
                var analyze = new Button(() => FindingActions.OpenLineLevelAnalysis(f))
                {
                    text = string.IsNullOrEmpty(fileName)
                        ? L.Tr("Line-level analysis", "逐行分析")
                        : L.Tr($"Line-level analysis of {fileName}", $"逐行分析 {fileName}"),
                    // Where it lands is the full panel on this script — which is also where the AI Fix for this
                    // finding is, and the card's tag says "AI Fix, in the full panel". Saying so here is what makes
                    // the tag and the button read as the same thing rather than two unrelated offers.
                    tooltip = f.AiFixable
                        ? L.Tr("Re-scans this one file and opens the full panel on it — where the AI Fix for this finding is.",
                               "只重扫这一个文件并在完整面板中打开它——这条 finding 的 AI Fix 就在那里。")
                        : L.Tr("Re-scans this one file and opens the full panel on it, line by line.",
                               "只重扫这一个文件，并在完整面板中逐行打开。")
                };
                analyze.style.marginLeft = 4;
                row.Add(analyze);
            }

            if (ctx != null && FindingActions.IsFixable(f))
            {
                // A genuine reversible one-click fix (import/project settings). Safe to apply inline and undo.
                //
                // interactive: true is what makes this the same operation the main panel offers rather than a quieter
                // copy of it — the Pro gate, the dialog naming how many assets get re-imported and telling you to
                // commit first, and a progress bar instead of a frozen editor. Reaching batch auto-fix from here used
                // to do none of those.
                var fix = new Button(() =>
                {
                    string rid = f.RuleId;
                    string msg = FindingActions.ApplyRule(rid, ctx.Scan, out var updated, interactive: true);
                    ctx.OnApplied?.Invoke(msg, updated);
                })
                { text = fixablePlaces > 1 ? L.Tr($"Fix ({fixablePlaces})", $"修复（{fixablePlaces}）") : L.Tr("Fix", "修复") };
                fix.style.marginLeft = 4;
                row.Add(fix);
            }
            else if (FindingActions.NeedsDecision(f))
            {
                // An Action: deletes files, needs a chooser, or needs a package-disable verifier. It must NOT be run
                // inline — the full panel owns the flow that does it safely. Route there instead of offering a Fix
                // button that would bypass every guard (and, for ASSET.DUP001, crash the editor).
                // FocusOnRule, not OpenWindow: landing at the top of six hundred findings is not a hand-off.
                string ruleId = f.RuleId;
                var open = new Button(() => PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnRule(ruleId))
                {
                    text = FindingActions.IsIrreversible(f)
                        ? L.Tr("Review in full panel", "去完整面板处理")
                        : L.Tr("Do this in full panel", "去完整面板处理")
                };
                open.style.marginLeft = 4;
                open.tooltip = FindingActions.IsIrreversible(f)
                    ? L.Tr("Deletes files and can't be undone — the full panel lets you choose which copy to keep first.",
                           "会删除文件、不可撤销——完整面板会先让你选择保留哪一份。")
                    : L.Tr("Needs a confirmation step the full panel provides.", "需要完整面板里的确认步骤。");
                row.Add(open);
            }

            // One tier of button, applied where the row is built rather than by whichever window happens to draw it.
            // Fix used to be marked bold here and the rest left Normal — same size, same fill, same text colour, so
            // the only difference between two buttons on one row was weight, and side by side the Normal one reads
            // as disabled. Which action is the consequential one is said by the word on it.
            return PerfLintStyle.CompactActions(row);
        }

        /// <summary>
        /// Where Locate lands for a rule with more than one place — and, when a rule withholds its own fix from some
        /// of them, why the count differs from the Fix button beside it.
        ///
        /// Live example: "Mesh compression is off — 239 places" with a Locate offering 254. Both are right (254 have
        /// it off, 239 can be changed safely) and an unexplained disagreement between two numbers on one line reads
        /// as a bug, so it is stated rather than left to be guessed — and it is the useful half, because those 15 are
        /// precisely the ones the panel is worth opening for.
        /// </summary>
        static string LocateRuleTooltip(int places, int fixablePlaces)
        {
            // One place is not "all 1 places". It is also the case that needs the reason spelled out: the reader is
            // being sent to a list of one instead of to the file, because the file does not say which setting.
            if (places <= 1)
                return L.Tr("Opens the full panel on this rule, where the finding says which setting is wrong and why — with Locate and Fix on the row.",
                            "在完整面板中打开这条规则：那里写着到底是哪个设置不对、为什么——该行自带定位与修复。");

            string head = L.Tr($"Opens the full panel on this rule — all {places} places, one row each, with Locate and Fix per row.",
                               $"在完整面板中打开这条规则——{places} 处逐条列出，每行都有自己的定位与修复。");
            if (fixablePlaces <= 0 || fixablePlaces >= places) return head;
            return head + L.Tr($" PerfLint applies its own fix to {fixablePlaces} of them; the other {places - fixablePlaces} need your eye, and that is where you would do them.",
                               $" PerfLint 只对其中 {fixablePlaces} 项应用一键修复；另外 {places - fixablePlaces} 项需要你自己判断，正好在那里手动处理。");
        }

    }
}
