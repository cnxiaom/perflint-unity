using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Diff blocks for a WHOLE-FILE rewrite (AI Migrate): trims the common prefix/suffix lines and shows only the
        /// changed middle section as −old/＋new, with "N unchanged lines" markers around it — a few hundred identical
        /// lines of context would drown the actual migration otherwise. Falls back gracefully when everything changed.
        /// </summary>
        public static void BuildFileDiffBlocks(VisualElement area, string original, string migrated)
        {
            if (area == null) return;
            var o = (original ?? "").Replace("\r\n", "\n").Split('\n');
            var m = (migrated ?? "").Replace("\r\n", "\n").Split('\n');

            int prefix = 0;
            while (prefix < o.Length && prefix < m.Length && o[prefix] == m[prefix]) prefix++;
            int suffix = 0;
            while (suffix < o.Length - prefix && suffix < m.Length - prefix
                   && o[o.Length - 1 - suffix] == m[m.Length - 1 - suffix]) suffix++;

            // Keep 2 lines of context on each side of the changed section for readability.
            int ctx = 2;
            prefix = Math.Max(0, prefix - ctx);
            suffix = Math.Max(0, suffix - ctx);

            string oldMid = string.Join("\n", o, prefix, o.Length - prefix - suffix);
            string newMid = string.Join("\n", m, prefix, m.Length - prefix - suffix);

            if (prefix > 0)
                area.Add(Dim(L.Tr($"… {prefix} unchanged line(s) at the top of the file …", $"… 文件头 {prefix} 行未改动 …")));

            area.Add(Header(L.Tr($"− Original (lines {prefix + 1}–{o.Length - suffix})", $"− 原始（第 {prefix + 1}–{o.Length - suffix} 行）"), RedHeader, marginTop: 6));
            area.Add(ReadOnlyCode(oldMid));

            area.Add(Header(L.Tr($"＋ Migrated (lines {prefix + 1}–{m.Length - suffix})", $"＋ 迁移后（第 {prefix + 1}–{m.Length - suffix} 行）"), GreenHeader, marginTop: 4));
            area.Add(ReadOnlyCode(newMid));

            // Rename-style migrations often change just a couple of CHARACTERS in a line (DirectBDRF→DirectBRDF) —
            // invisible in a plain line diff (a real user read it as "no change"). Summarize the exact in-line
            // fragments that changed, so the diff reads at a glance.
            var inline = ComputeInlineChanges(
                Slice(o, prefix, o.Length - prefix - suffix),
                Slice(m, prefix, m.Length - prefix - suffix), prefix + 1);
            if (inline.Count > 0)
            {
                area.Add(Header(L.Tr("~ Changed within lines", "~ 行内变化"), RiskColor, marginTop: 4));
                const int maxShown = 8;
                for (int i = 0; i < inline.Count && i < maxShown; i++)
                {
                    var c = inline[i];
                    area.Add(new Label($"  {L.Tr("line", "第")} {c.line}{L.Tr(":", " 行：")}  {c.oldFrag}  →  {c.newFrag}")
                    { style = { whiteSpace = WhiteSpace.Normal, color = RiskColor } });
                }
                if (inline.Count > maxShown)
                    area.Add(Dim(L.Tr($"… and {inline.Count - maxShown} more changed line(s).", $"… 还有 {inline.Count - maxShown} 行有行内变化。")));
            }

            if (suffix > 0)
                area.Add(Dim(L.Tr($"… {suffix} unchanged line(s) at the bottom of the file …", $"… 文件尾 {suffix} 行未改动 …")));
        }

        private static string[] Slice(string[] arr, int start, int count)
        {
            var r = new string[Math.Max(0, count)];
            for (int i = 0; i < r.Length; i++) r[i] = arr[start + i];
            return r;
        }

        /// <summary>
        /// Pure logic: for PAIRED changed lines (equal line counts only — insertions/deletions read fine in the
        /// line diff already), extract the in-line fragment that actually changed, expanded to word boundaries
        /// (so DirectBDRF→DirectBRDF shows whole identifiers, not "DR"→"RD"). Returns (1-based line, old, new).
        /// </summary>
        internal static List<(int line, string oldFrag, string newFrag)> ComputeInlineChanges(
            string[] oldLines, string[] newLines, int firstLineNumber)
        {
            var result = new List<(int, string, string)>();
            if (oldLines == null || newLines == null || oldLines.Length != newLines.Length) return result;

            for (int i = 0; i < oldLines.Length; i++)
            {
                string a = oldLines[i] ?? "", b = newLines[i] ?? "";
                if (a == b) continue;

                int p = 0;
                while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
                int s = 0;
                while (s < a.Length - p && s < b.Length - p && a[a.Length - 1 - s] == b[b.Length - 1 - s]) s++;

                // Expand to word boundaries so renamed identifiers show whole.
                while (p > 0 && IsWordChar(a[p - 1])) p--;
                while (s > 0 && (IsWordChar(a[a.Length - s]) || IsWordChar(b[b.Length - s]))) s--;

                string oldFrag = a.Substring(p, a.Length - p - s).Trim();
                string newFrag = b.Substring(p, b.Length - p - s).Trim();
                result.Add((firstLineNumber + i,
                    oldFrag.Length == 0 ? L.Tr("(nothing)", "（空）") : Clip(oldFrag),
                    newFrag.Length == 0 ? L.Tr("(removed)", "（删除）") : Clip(newFrag)));
            }
            return result;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static string Clip(string s) => s.Length <= 80 ? s : s.Substring(0, 77) + "…";

        private static Label Dim(string text) => new Label(text)
        {
            style = { color = new Color(1, 1, 1, 0.45f), marginTop = 4, unityFontStyleAndWeight = FontStyle.Italic }
        };

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
