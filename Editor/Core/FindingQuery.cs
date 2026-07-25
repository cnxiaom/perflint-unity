using System;
using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// Pure, deterministic filtering / ordering / rule-rollup over a scan's findings. Extracted from the Pipeline
    /// <c>perflint_list_findings</c> command so the query logic is unit-testable in the EditMode host WITHOUT the
    /// optional <c>com.unity.pipeline</c> package (the command assembly is defineConstraint-gated and never compiles
    /// there). The command layer only does DTO projection (human strings, id handles) on top of these results.
    /// </summary>
    public static class FindingQuery
    {
        /// <summary>Filter criteria. Empty/none fields = "match anything".</summary>
        public readonly struct Filter
        {
            public readonly Severity MinSeverity;      // include findings at or above this severity
            public readonly Domain? Domain;            // null = any domain
            public readonly string RuleIdPrefix;       // case-insensitive prefix (exact id is a full-length prefix); null = any

            public Filter(Severity minSeverity, Domain? domain, string ruleIdPrefix)
            {
                MinSeverity = minSeverity;
                Domain = domain;
                RuleIdPrefix = string.IsNullOrEmpty(ruleIdPrefix) ? null : ruleIdPrefix;
            }

            /// <summary>True when nothing is actually filtered (matches every finding) — lets a caller distinguish
            /// "whole project" from a narrowed run, which an executing command must report honestly.</summary>
            public bool IsAll => MinSeverity == Severity.Info && !Domain.HasValue && RuleIdPrefix == null;

            /// <summary>One-line English description of the scope, for command output ("whole project", "rule PERF.TEX, severity ≥ warning"…).</summary>
            public string Describe()
            {
                if (IsAll) return "whole project";
                var parts = new List<string>();
                if (RuleIdPrefix != null) parts.Add("rule " + RuleIdPrefix);
                if (Domain.HasValue) parts.Add("domain " + Domain.Value.ToString().ToLowerInvariant());
                if (MinSeverity != Severity.Info) parts.Add("severity ≥ " + MinSeverity.ToString().ToLowerInvariant());
                return string.Join(", ", parts.ToArray());
            }

            /// <summary>Builds a filter from the loose string forms the CLI/agent passes (empty = no constraint).</summary>
            public static Filter Parse(string minSeverity, string domain, string ruleIdPrefix) =>
                new Filter(ParseSeverity(minSeverity), ParseDomain(domain), ruleIdPrefix?.Trim());
        }

        /// <summary>Loose severity parsing: "crit*" / "warn*" / anything else (incl. empty) = Info (no threshold).</summary>
        public static Severity ParseSeverity(string s)
        {
            if (!string.IsNullOrEmpty(s))
            {
                var t = s.Trim().ToLowerInvariant();
                if (t.StartsWith("crit", StringComparison.Ordinal)) return Severity.Critical;
                if (t.StartsWith("warn", StringComparison.Ordinal)) return Severity.Warning;
            }
            return Severity.Info;
        }

        /// <summary>Loose domain parsing by prefix; null (= no domain constraint) for empty OR unrecognized input —
        /// a typo must never silently filter everything away.</summary>
        public static Domain? ParseDomain(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim().ToLowerInvariant();
            if (t.StartsWith("perf", StringComparison.Ordinal)) return PerfLint.Core.Domain.Performance;
            if (t.StartsWith("asset", StringComparison.Ordinal)) return PerfLint.Core.Domain.Assets;
            if (t.StartsWith("mig", StringComparison.Ordinal)) return PerfLint.Core.Domain.Migration;
            if (t.StartsWith("proj", StringComparison.Ordinal)) return PerfLint.Core.Domain.ProjectSettings;
            if (t.StartsWith("run", StringComparison.Ordinal)) return PerfLint.Core.Domain.Runtime;
            return null;
        }

        /// <summary>
        /// Findings matching the filter, ordered most-useful-first: severity desc, then total (memory+build) savings
        /// desc, then a stable tiebreak (ruleId / path / line) so paging is deterministic across calls. Never null.
        /// </summary>
        public static List<Finding> Match(IReadOnlyList<Finding> findings, Filter f)
        {
            var result = new List<Finding>();
            if (findings == null) return result;
            foreach (var x in findings)
            {
                if (x == null) continue;
                if ((int)x.Severity < (int)f.MinSeverity) continue;
                if (f.Domain.HasValue && x.Domain != f.Domain.Value) continue;
                if (f.RuleIdPrefix != null && !x.RuleId.StartsWith(f.RuleIdPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(x);
            }
            result.Sort(CompareFindings);
            return result;
        }

        static int CompareFindings(Finding a, Finding b)
        {
            int s = ((int)b.Severity).CompareTo((int)a.Severity);                  // severity desc
            if (s != 0) return s;
            long sa = a.EstimatedMemorySavingsBytes + a.EstimatedBuildSavingsBytes;
            long sb = b.EstimatedMemorySavingsBytes + b.EstimatedBuildSavingsBytes;
            if (sa != sb) return sb.CompareTo(sa);                                 // savings desc
            int r = string.CompareOrdinal(a.RuleId, b.RuleId);
            if (r != 0) return r;
            int p = string.CompareOrdinal(a.TargetPath ?? "", b.TargetPath ?? "");
            if (p != 0) return p;
            return a.CodeLine.CompareTo(b.CodeLine);
        }

        /// <summary>
        /// Rule-level rollup of the given findings (the itemized-to-categories aggregation an agent otherwise has to
        /// hand-roll off the raw JSON), ordered most-useful-first: max severity desc, total savings desc, count desc,
        /// ruleId. Never null.
        /// </summary>
        public static List<RuleRollup> Rollup(IReadOnlyList<Finding> findings)
        {
            var map = new Dictionary<string, RuleRollup>(StringComparer.Ordinal);
            var order = new List<RuleRollup>();
            if (findings != null)
                foreach (var f in findings)
                {
                    if (f == null) continue;
                    if (!map.TryGetValue(f.RuleId, out var r))
                    {
                        r = new RuleRollup(f.RuleId, f.Domain, f.GroupTitleOrTitle);
                        map[f.RuleId] = r;
                        order.Add(r);
                    }
                    r.Add(f);
                }
            order.Sort(CompareRollups);
            return order;
        }

        static int CompareRollups(RuleRollup a, RuleRollup b)
        {
            int s = ((int)b.MaxSeverity).CompareTo((int)a.MaxSeverity);            // max severity desc
            if (s != 0) return s;
            long ta = a.MemoryBytes + a.BuildBytes, tb = b.MemoryBytes + b.BuildBytes;
            if (ta != tb) return tb.CompareTo(ta);                                 // total savings desc
            if (a.Count != b.Count) return b.Count.CompareTo(a.Count);            // count desc
            return string.CompareOrdinal(a.RuleId, b.RuleId);
        }

        /// <summary>One rule's aggregated findings: count, worst severity, summed savings, and whether any carry a fix/action.</summary>
        public sealed class RuleRollup
        {
            public string RuleId { get; }
            public Domain Domain { get; }
            public string Title { get; }
            public int Count { get; private set; }
            public Severity MaxSeverity { get; private set; }
            public long MemoryBytes { get; private set; }
            public long BuildBytes { get; private set; }
            public bool CanAutoFix { get; private set; }   // any finding fixable (live OR was-fixable-before-restore)
            public bool HasAction { get; private set; }

            public RuleRollup(string ruleId, Domain domain, string title)
            {
                RuleId = ruleId;
                Domain = domain;
                Title = title;
                MaxSeverity = Severity.Info;
            }

            public void Add(Finding f)
            {
                Count++;
                if ((int)f.Severity > (int)MaxSeverity) MaxSeverity = f.Severity;
                MemoryBytes += f.EstimatedMemorySavingsBytes;
                BuildBytes += f.EstimatedBuildSavingsBytes;
                CanAutoFix |= f.CanAutoFix || f.WasAutoFixable;
                HasAction |= f.HasAction || f.WasActionable;
            }
        }
    }
}
