using System;
using PerfLint.L10n;
using PerfLint.Llm;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// Renders the "diff block" of an AI fix proposal — a read-only display of −original / ＋fixed / ＋new field at top of class / ＋new using at top of file.
    /// Shared by three call sites (single AI Fix in the main panel, single AI Fix in the runtime panel, and the batch review window) to avoid three duplicate copies (previously written separately and prone to drift).
    /// Does not include an "Apply" button and does not clear the container — those are left to each caller's own interaction (single fix applies directly / batch applies after selection).
    /// </summary>
    public static class AiFixDiffView
    {
        private static readonly Color RedHeader = new Color(0.93f, 0.45f, 0.45f);
        private static readonly Color GreenHeader = new Color(0.45f, 0.80f, 0.50f);

        private static readonly Color RiskColor = new Color(0.95f, 0.70f, 0.20f);

        /// <summary>Appends the proposal's diff block to <paramref name="area"/> (without clearing it or adding an apply button).</summary>
        public static void BuildDiffBlocks(VisualElement area, ScriptFixProposal p)
        {
            if (area == null || p == null) return;

            // When the semantic self-check judges the fix as "possibly changes behavior" → a prominent ⚠ banner at the top plus the reason. This is the last human gate for fixes that compile but are semantically wrong: the review window leaves these unchecked by default,
            // but they can still be applied manually, so the risk must be spelled out clearly here for the user to glance at before deciding.
            if (p.BehaviorRisk)
            {
                var warn = new Label("⚠ " + L.Tr("AI flagged a possible behavior change: ", "AI 提示此修复可能改变行为：") +
                                     (string.IsNullOrEmpty(p.RiskReason) ? L.Tr("review carefully before applying.", "请仔细复核后再应用。") : p.RiskReason))
                {
                    style =
                    {
                        whiteSpace = WhiteSpace.Normal, color = RiskColor, unityFontStyleAndWeight = FontStyle.Bold,
                        marginTop = 6, marginBottom = 2,
                        paddingTop = 3, paddingBottom = 3, paddingLeft = 6, paddingRight = 6,
                        backgroundColor = new Color(0.95f, 0.70f, 0.20f, 0.12f)
                    }
                };
                area.Add(warn);
            }

            area.Add(Header(L.Tr("− Original", "− 原始"), RedHeader, marginTop: 6));
            area.Add(ReadOnlyCode(p.Original));

            area.Add(Header(L.Tr("＋ Fixed", "＋ 修复后"), GreenHeader, marginTop: 4));
            area.Add(ReadOnlyCode(p.Fixed));

            // Cache-style fixes: the field declaration is deterministically inserted at the top of the class body by the tool (not part of the snippet diff above), so it is shown separately.
            if (!string.IsNullOrEmpty(p.FieldDecl))
            {
                area.Add(Header(L.Tr("＋ New field at top of class", "＋ 类顶部新增字段"), GreenHeader, marginTop: 4));
                area.Add(ReadOnlyCode(p.FieldDecl));
            }

            // Migration/rename-style fixes: missing usings are inserted at the top of the file by the tool (not part of the snippet diff), so they are shown separately.
            if (p.Usings != null && p.Usings.Length > 0)
            {
                area.Add(Header(L.Tr("＋ New using at top of file", "＋ 文件顶部新增 using"), GreenHeader, marginTop: 4));
                area.Add(ReadOnlyCode(string.Join("\n", Array.ConvertAll(p.Usings, ns => "using " + ns + ";"))));
            }
        }

        private static Label Header(string text, Color color, float marginTop) => new Label(text)
        {
            style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = marginTop, color = color }
        };

        private static TextField ReadOnlyCode(string value)
        {
            var tf = new TextField { multiline = true, isReadOnly = true, value = value };
            tf.style.whiteSpace = WhiteSpace.Normal;
            return tf;
        }
    }
}
