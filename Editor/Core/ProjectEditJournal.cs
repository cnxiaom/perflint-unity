using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.L10n;
using UnityEngine;

namespace PerfLint.Core
{
    /// <summary>
    /// An append-only record of what has changed in the project and when.
    ///
    /// It exists to answer two questions that a before/after measurement cannot answer on its own:
    ///
    /// 1. **Is this measurement still true?** A runtime sample describes the project as it was at the moment it was
    ///    taken. Ten minutes and forty import-setting changes later it describes something that no longer exists —
    ///    but nothing in the sample itself says so, so the panel would keep presenting it as current.
    /// 2. **What is this improvement an improvement FROM?** "24 ms → 16 ms" is only a claim about the user's work if
    ///    we can name the work. Without it the number is a coincidence with a timestamp.
    ///
    /// Deliberately modest about what it knows: it records PerfLint's own fixes by rule id, and everything else as
    /// "N assets changed". It cannot see a hand edit that was never imported, and does not pretend to — the summary
    /// wording says "changes recorded", never "all changes".
    /// </summary>
    public static class ProjectEditJournal
    {
        /// <summary>One recorded change. Coarse on purpose: a journal that stored every path would be a second asset database.</summary>
        [Serializable]
        public sealed class Entry
        {
            public long ticks;
            /// <summary>"fix" = PerfLint applied it and knows the rule; "assets" = something was imported and we only know how many.</summary>
            public string kind;
            /// <summary>Rule id for a fix, or a short description for an import batch.</summary>
            public string label;
            public int count;

            public DateTime AtUtc => new DateTime(ticks, DateTimeKind.Utc);
            public bool IsFix => string.Equals(kind, KindFix, StringComparison.Ordinal);
        }

        public const string KindFix = "fix";
        public const string KindAssets = "assets";

        /// <summary>
        /// Files that changed under <c>Packages/</c> rather than <c>Assets/</c>.
        ///
        /// Kept separate because a package arriving is not something the user did, and reporting it as "22 file
        /// changes" next to their own edits is how "you changed nothing" turns into a figure they can see is wrong.
        /// It is still recorded rather than dropped — upgrading URP absolutely can move a measurement, and a
        /// before/after that spans it should say so.
        /// </summary>
        public const string KindPackages = "packages";

        /// <summary>Oldest entries are dropped past this. The journal answers "what changed recently", not "what ever changed".</summary>
        const int MaxEntries = 400;

        /// <summary>Consecutive records of the same kind and label inside this window are merged, so one user action is one entry.</summary>
        const double CoalesceSeconds = 5.0;

        static string FilePath
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint", "edits.json");
            }
        }

        /// <summary>Records that PerfLint applied a fix for a rule. <paramref name="count"/> is how many findings it covered.</summary>
        public static void RecordFix(string ruleId, int count)
        {
            if (string.IsNullOrEmpty(ruleId) || count <= 0) return;
            Record(KindFix, ruleId, count);
        }

        /// <summary>Records that assets were imported/changed outside a known fix. Only the count is kept.</summary>
        public static void RecordAssetChanges(int count)
        {
            if (count <= 0 || UserEditsSuppressed) return;
            Record(KindAssets, "", count);
        }

        static int _suppressUserEdits;

        /// <summary>
        /// True while PerfLint is applying its own fixes, so the re-imports they cause are not filed as the user's
        /// separate edits.
        /// </summary>
        public static bool UserEditsSuppressed => _suppressUserEdits > 0;

        /// <summary>
        /// Held around applying our own fixes. The postprocessor cannot tell a re-import WE caused from somebody
        /// editing a texture, so a batch of 239 models was recorded twice — once as "PERF.MSH002 ×239", the thing
        /// we did and can name, and again as "239 other file changes", which reads as 478 things happening when
        /// 239 did.
        ///
        /// The count is not cosmetic. "Was anything done here besides our own named fixes?" is what decides whether
        /// a comparison may state what the round was — a report will not say "this round was work no measurement
        /// can see" while an unnamed edit is on record, because that edit could have moved anything. Counting our
        /// own fix as such an edit made that statement unreachable after the very operation it is about.
        ///
        /// Re-entrant, and a scope rather than a flag so an exception mid-batch cannot leave it stuck on.
        /// </summary>
        public static IDisposable SuppressUserEdits() => new SuppressScope();

        sealed class SuppressScope : IDisposable
        {
            public SuppressScope() { _suppressUserEdits++; }
            public void Dispose() { if (_suppressUserEdits > 0) _suppressUserEdits--; }
        }

        /// <summary>Records that files under <c>Packages/</c> changed — a package update, not the user's own editing.</summary>
        public static void RecordPackageChanges(int count)
        {
            if (count <= 0) return;
            Record(KindPackages, "", count);
        }

        static void Record(string kind, string label, int count)
        {
            try
            {
                var all = LoadAll();
                var now = DateTime.UtcNow;

                // One user action often arrives as several import callbacks; merging them keeps "12 changes" from
                // reading as twelve separate decisions the user made.
                var last = all.Count > 0 ? all[all.Count - 1] : null;
                if (last != null
                    && string.Equals(last.kind, kind, StringComparison.Ordinal)
                    && string.Equals(last.label ?? "", label ?? "", StringComparison.Ordinal)
                    && (now - last.AtUtc).TotalSeconds <= CoalesceSeconds)
                {
                    last.count += count;
                    last.ticks = now.Ticks;
                }
                else
                {
                    all.Add(new Entry { ticks = now.Ticks, kind = kind, label = label ?? "", count = count });
                }

                if (all.Count > MaxEntries) all.RemoveRange(0, all.Count - MaxEntries);
                Save(all);
            }
            catch
            {
                // The journal is an aid to honesty, not a dependency. A failure here must never take down the fix
                // that was being applied when it happened.
            }
        }

        /// <summary>Entries recorded strictly after the given moment, oldest first.</summary>
        public static IReadOnlyList<Entry> Since(DateTime utc)
        {
            try
            {
                long t = utc.ToUniversalTime().Ticks;
                return LoadAll().Where(e => e != null && e.ticks > t).ToList();
            }
            catch { return Array.Empty<Entry>(); }
        }

        /// <summary>
        /// Entries recorded after one measurement and no later than another.
        ///
        /// A before/after report is a statement about two captured project states, not about whatever happens to be
        /// in the editor when the report is opened. Without the upper bound, a package refresh or asset edit made
        /// after the "after" run retroactively appeared in that old comparison and could even turn a null comparison
        /// into an attributed result.
        /// </summary>
        public static IReadOnlyList<Entry> Between(DateTime afterExclusiveUtc, DateTime atOrBeforeUtc)
        {
            try { return SelectBetween(LoadAll(), afterExclusiveUtc, atOrBeforeUtc); }
            catch { return Array.Empty<Entry>(); }
        }

        /// <summary>The time-window selection on already-loaded entries. Public so the boundary rule is testable without touching a project's journal.</summary>
        public static IReadOnlyList<Entry> SelectBetween(IReadOnlyList<Entry> entries,
            DateTime afterExclusiveUtc, DateTime atOrBeforeUtc)
        {
            if (entries == null || entries.Count == 0) return Array.Empty<Entry>();

            long after = afterExclusiveUtc.ToUniversalTime().Ticks;
            long through = atOrBeforeUtc.ToUniversalTime().Ticks;
            if (through <= after) return Array.Empty<Entry>();

            var selected = new List<Entry>();
            foreach (var e in entries)
                if (e != null && e.ticks > after && e.ticks <= through)
                    selected.Add(e);
            return selected;
        }

        /// <summary>Total number of individual changes recorded since a moment. 0 means nothing was recorded — not necessarily that nothing happened.</summary>
        public static int CountSince(DateTime utc) => Count(Since(utc));

        public static int Count(IReadOnlyList<Entry> entries)
        {
            int n = 0;
            if (entries == null) return n;
            foreach (var e in entries) if (e != null) n += Math.Max(0, e.count);
            return n;
        }

        /// <summary>
        /// Fixes PerfLint itself applied since a moment. Distinguished from the total because these are the only
        /// changes we can NAME — and therefore the only ones a before/after may point at as a cause. An import we
        /// merely counted is not evidence that anything was done.
        /// </summary>
        public static int FixCountSince(DateTime utc) => FixCount(Since(utc));

        public static int FixCount(IReadOnlyList<Entry> entries)
        {
            int n = 0;
            if (entries == null) return n;
            foreach (var e in entries) if (e != null && e.IsFix) n += Math.Max(0, e.count);
            return n;
        }

        /// <summary>
        /// Changes to the user's own assets and scripts since a moment — everything except our own named fixes and
        /// package updates.
        ///
        /// These cannot be named, but they are evidence that SOMETHING was done, and that distinction was being
        /// thrown away. A hand-edited script counted zero here, so a comparison spanning it was filed as an
        /// observation of drift — the tool banking a real code change as "this is how much the numbers move when
        /// nobody touches them", and telling the person who made it that nothing had happened. Package updates stay
        /// out: those are not the user's work, and mistaking one for a change to judge is a mistake already made.
        /// </summary>
        public static int UserEditCountSince(DateTime utc) => UserEditCount(Since(utc));

        public static int UserEditCount(IReadOnlyList<Entry> entries)
        {
            int n = 0;
            if (entries == null) return n;
            foreach (var e in entries)
                if (e != null && !e.IsFix && !string.Equals(e.kind, KindPackages, StringComparison.Ordinal))
                    n += Math.Max(0, e.count);
            return n;
        }

        /// <summary>
        /// One phrase naming what changed since a moment, or null when nothing was recorded.
        ///
        /// Named rules come first and are listed by name; anything we only saw as an import is folded into a trailing
        /// "and N other file changes", which is the honest shape of what this can know.
        /// </summary>
        /// <summary>
        /// Rules PerfLint fixed since this point, so a report can tell what the round was AIMED at. The summary
        /// string already names them, but only for a human -- parsing it back would be reading our own prose.
        /// </summary>
        public static IReadOnlyList<string> FixedRulesSince(DateTime utc) => FixedRules(Since(utc));

        public static IReadOnlyList<string> FixedRules(IReadOnlyList<Entry> entries)
        {
            var ids = new List<string>();
            if (entries == null) return ids;
            foreach (var e in entries)
                if (e != null && e.IsFix && !string.IsNullOrEmpty(e.label) && !ids.Contains(e.label))
                    ids.Add(e.label);
            return ids;
        }

        public static string SummarySince(DateTime utc) => Summarize(Since(utc));

        /// <summary>The wording rules on their own, over an already-selected set of entries.</summary>
        public static string Summarize(IReadOnlyList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return null;

            var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
            int otherFiles = 0, packageFiles = 0;
            foreach (var e in entries)
            {
                if (e.IsFix && !string.IsNullOrEmpty(e.label))
                    byRule[e.label] = (byRule.TryGetValue(e.label, out int c) ? c : 0) + e.count;
                else if (string.Equals(e.kind, KindPackages, StringComparison.Ordinal))
                    packageFiles += e.count;
                else
                    otherFiles += e.count;
            }

            var parts = new List<string>();
            foreach (var kv in byRule.OrderByDescending(k => k.Value).Take(3))
                parts.Add(kv.Value > 1 ? $"{kv.Key} ×{kv.Value}" : kv.Key);

            int remainingRules = Math.Max(0, byRule.Count - 3);
            if (remainingRules > 0)
                parts.Add(L.Tr($"{remainingRules} more rules", $"另 {remainingRules} 条规则"));

            if (otherFiles > 0)
                parts.Add(L.Tr($"{otherFiles} other file changes", $"另有 {otherFiles} 处文件改动"));

            // Named as what it is. "22 other file changes" for a package that updated itself reads as the user's own
            // editing, which is the opposite of what it tells them.
            // "of N files" rather than "(N files)": this string gets embedded in sentences that already use
            // parentheses, and brackets inside brackets is where copy stops being readable.
            if (packageFiles > 0)
                parts.Add(L.Tr($"a package update of {packageFiles} files", $"一次包更新（{packageFiles} 个文件）"));

            return parts.Count == 0 ? null : string.Join(L.Tr(", ", "、"), parts);
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* ignore */ }
        }

        // ── Storage ───────────────────────────────────────────

        static List<Entry> LoadAll()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return new List<Entry>();
                var dto = JsonUtility.FromJson<Dto>(File.ReadAllText(path));
                return dto?.entries != null ? new List<Entry>(dto.entries) : new List<Entry>();
            }
            catch { return new List<Entry>(); }
        }

        static void Save(List<Entry> entries)
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(new Dto { entries = entries.ToArray() }));
        }

        [Serializable]
        sealed class Dto
        {
            public Entry[] entries;
        }
    }
}
