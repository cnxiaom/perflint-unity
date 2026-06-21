using System;
using System.Collections.Generic;
using System.Linq;

namespace PerfLint.Core
{
    /// <summary>
    /// The aggregated result of a complete scan. Holds all Findings and computes
    /// the "project health score" (a shareable hook for cold-start engagement).
    /// </summary>
    public sealed class ScanResult
    {
        public IReadOnlyList<Finding> Findings { get; }
        public DateTime CompletedAtUtc { get; }
        public TimeSpan Duration { get; }

        /// <summary>
        /// Scanner name → all RuleIds produced by that scanner in this run (deduplicated). Used for
        /// "after a fix, re-scan only the affected group(s)": look up the owning scanner(s) by the
        /// fixed finding's RuleId, re-run only those, and replace their results — avoiding a full
        /// re-scan (see ScanRunner.RescanRules).
        /// Populated during a full Scan; incremental results reuse this map and update in-place the
        /// entries for re-run scanners. May be null (results produced by the old code path).
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> ScannerRuleMap { get; }

        public ScanResult(IReadOnlyList<Finding> findings, TimeSpan duration,
            IReadOnlyDictionary<string, IReadOnlyList<string>> scannerRuleMap = null,
            DateTime? completedAtUtc = null)
        {
            Findings = findings ?? Array.Empty<Finding>();
            Duration = duration;
            // Defaults to now; when restoring from disk, pass the original completion time so that
            // "last scanned at" reflects the real scan time rather than the moment of restoration.
            CompletedAtUtc = completedAtUtc ?? DateTime.UtcNow;
            ScannerRuleMap = scannerRuleMap;
        }

        public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
        public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
        public int InfoCount => Findings.Count(f => f.Severity == Severity.Info);
        public int AutoFixableCount => Findings.Count(f => f.CanAutoFix);

        public IEnumerable<IGrouping<Domain, Finding>> ByDomain() =>
            Findings.GroupBy(f => f.Domain).OrderBy(g => g.Key);

        /// <summary>
        /// Health score from 0 to 100.
        ///
        /// Model: **deduct per rule kind with per-kind saturation cap**, not a linear deduction
        /// per instance count.
        /// - Driven by "how many *kinds* of problems the project has hit and how severe each is",
        ///   not by how many instances a single kind has accumulated.
        ///   (2000 instances of DUP001 are still fundamentally one kind of problem — "duplicate
        ///   assets" — and should not be penalized 2000× more than a single instance.)
        /// - Each rule's contribution = severity base × saturation factor(instance count);
        ///   the saturation factor asymptotically approaches 1 but is capped, so a single noisy
        ///   rule cannot crush the score, while hitting many distinct problem kinds genuinely
        ///   accumulates penalty.
        /// - Info base is very low and barely affects the headline score.
        ///
        /// This is a "perceived" metric (for reports and sharing), not a precise measurement;
        /// base values can be tuned later.
        /// </summary>
        public int HealthScore()
        {
            double penalty = 0;
            foreach (var rule in Findings.GroupBy(f => f.RuleId))
            {
                Severity sev = rule.Max(f => f.Severity);
                int count = rule.Count();

                double basePenalty = sev switch
                {
                    Severity.Critical => 25.0,
                    Severity.Warning => 9.0,
                    _ => 1.5
                };

                // Saturation factor: 1 instance ≈ 0.55, gradually approaching 1 (caps around 50 instances).
                // Even a single instance produces a meaningful deduction, but piling up more
                // instances of the same rule is always capped at basePenalty.
                double saturation = 0.45 + 0.55 * (1.0 - Math.Exp(-count / 5.0));
                penalty += basePenalty * saturation;
            }

            return (int)Math.Round(Math.Max(0, 100 - penalty));
        }

        public string HealthGrade()
        {
            int s = HealthScore();
            if (s >= 90) return "A";
            if (s >= 75) return "B";
            if (s >= 60) return "C";
            if (s >= 40) return "D";
            return "F";
        }
    }
}
