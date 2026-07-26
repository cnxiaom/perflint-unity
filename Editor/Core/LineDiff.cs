using System;
using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>One line that differs between two versions of a file.</summary>
    [Serializable]
    public sealed class DiffLine
    {
        /// <summary>1-based. The line in the NEW file for changed/added, in the OLD file for removed.</summary>
        public int line;
        /// <summary>"changed" | "added" | "removed" — say it explicitly rather than infer it from a null string,
        /// because JsonUtility turns nulls into empty strings and a removed line would read as a change to "".</summary>
        public string kind;
        public string before;
        public string after;
    }

    public sealed class LineDiffResult
    {
        public DiffLine[] Changes = Array.Empty<DiffLine>();
        /// <summary>How many differing lines there were before <see cref="Truncated"/> capping.</summary>
        public int TotalChanges;
        public bool Truncated;
        /// <summary>The inputs were too big to diff exactly; Changes is empty and TotalChanges is 0.</summary>
        public bool TooLarge;
    }

    /// <summary>
    /// A line-level diff, so a whole-file rewrite can report what it actually changed.
    ///
    /// Why this exists: the editor shows a diff and the user approves it before anything is written. Over the wire
    /// nobody sees one — and a compile check proves the result BUILDS, not that it only did what was asked. A real
    /// case that motivated this (2026-07-25): asked to fix one broken call, the model also swapped a healthy
    /// GetShadowFade for GetMainLightShadowFade. Legitimate per its playbook, unrequested, and invisible in a
    /// response that reported only line counts.
    ///
    /// Kept in Core, not in the pipeline layer, deliberately: the pipeline assembly is gated behind
    /// PERFLINT_PIPELINE and the EditMode host doesn't compile it, so logic living there cannot be unit-tested.
    /// </summary>
    public static class LineDiff
    {
        /// <summary>Above this many cells the LCS table stops being worth it. Recipes cap files at ~600 lines,
        /// so this is a guard against pathological input, not a limit anyone should meet.</summary>
        private const long MaxCells = 4_000_000;

        public static LineDiffResult Compute(string original, string migrated, int maxChanges = 40)
        {
            var a = SplitLines(original);
            var b = SplitLines(migrated);

            List<DiffLine> changes;
            if (a.Length == b.Length)
            {
                // The common shape by far: a minimal rewrite that preserves the line count. Exact, and O(n).
                changes = new List<DiffLine>();
                for (int i = 0; i < a.Length; i++)
                    if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                        changes.Add(new DiffLine { line = i + 1, kind = "changed", before = a[i], after = b[i] });
            }
            else if ((long)a.Length * b.Length > MaxCells)
            {
                return new LineDiffResult { TooLarge = true };
            }
            else
            {
                changes = LcsDiff(a, b);
            }

            int total = changes.Count;
            bool truncated = total > maxChanges && maxChanges > 0;
            if (truncated) changes = changes.GetRange(0, maxChanges);
            return new LineDiffResult { Changes = changes.ToArray(), TotalChanges = total, Truncated = truncated };
        }

        /// <summary>Split on newlines with \r\n and lone \r normalized away, so a line-ending change alone never
        /// reads as "every line changed".</summary>
        internal static string[] SplitLines(string s) =>
            string.IsNullOrEmpty(s) ? Array.Empty<string>()
                                    : s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        /// <summary>
        /// Classic LCS edit script, with adjacent delete+insert folded into a single "changed" entry — that
        /// pairing is what makes the output readable as "this line became that line" instead of two unrelated ops.
        /// </summary>
        private static List<DiffLine> LcsDiff(string[] a, string[] b)
        {
            int n = a.Length, m = b.Length;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            var ops = new List<(char op, int ai, int bi)>();
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (string.Equals(a[x], b[y], StringComparison.Ordinal)) { x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('-', x, -1)); x++; }
                else { ops.Add(('+', -1, y)); y++; }
            }
            while (x < n) { ops.Add(('-', x, -1)); x++; }
            while (y < m) { ops.Add(('+', -1, y)); y++; }

            var result = new List<DiffLine>();
            for (int k = 0; k < ops.Count; k++)
            {
                if (ops[k].op == '-' && k + 1 < ops.Count && ops[k + 1].op == '+')
                {
                    result.Add(new DiffLine
                    {
                        line = ops[k + 1].bi + 1,
                        kind = "changed",
                        before = a[ops[k].ai],
                        after = b[ops[k + 1].bi]
                    });
                    k++;
                }
                else if (ops[k].op == '-')
                {
                    result.Add(new DiffLine { line = ops[k].ai + 1, kind = "removed", before = a[ops[k].ai], after = null });
                }
                else
                {
                    result.Add(new DiffLine { line = ops[k].bi + 1, kind = "added", before = null, after = b[ops[k].bi] });
                }
            }
            return result;
        }
    }
}
