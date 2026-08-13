using System;
using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;
using PerfLint.Scanners;
using Unity.Pipeline.Commands;

namespace PerfLint.Ci.Pipeline
{
    /// <summary>
    /// perflint_list_findings — the itemized detail behind perflint_scan's counts, exposed over the wire so an agent
    /// talking to your OPEN editor can enumerate "which problems exactly" (rolled up by rule + a paged item list)
    /// instead of reaching AROUND the tools to parse Library/PerfLint/last-scan.json by hand — a workaround that only
    /// a local agent can even do (that file is outside Assets/) and that re-introduces the manual-aggregation the
    /// deterministic tool exists to replace.
    ///
    /// Reads the LAST persisted scan (what perflint_scan and the editor window save); it never triggers a scan, so
    /// it's instant and coherent with a preceding perflint_scan. Read-only, free, uploads nothing.
    /// </summary>
    public static class PerfLintListCommands
    {
        [CliCommand("perflint_list_findings",
            "List the findings behind perflint_scan's counts: a rule-level rollup (counts + estimated savings) plus a filtered, paged item list. Filter by domain / severity / rule. Run perflint_scan first. Local, free, read-only.")]
        public static ListFindingsDto PerfLintListFindings(
            [CliArg("domain", "Filter by domain: performance / assets / migration / projectsettings / runtime. Empty = all.")] string domain = null,
            [CliArg("min_severity", "Only findings at or above this severity: info / warning / critical. Default info (= all).")] string minSeverity = "info",
            [CliArg("rule_id", "Filter to a rule id — exact, prefix, or the short form without its domain prefix: 'PERF.TEX', 'PERF.TEX002' and 'TEX002' all work ('GC003' finds 'PERF.GC003'). If nothing matches, the response names the closest rule ids in the scan. Empty = all.")] string ruleId = null,
            [CliArg("limit", "Max individual items to return (paging). Default 50, capped at 200. The rule rollup is always complete.")] int limit = 50,
            [CliArg("offset", "Skip this many matching items before returning (paging). Default 0.")] int offset = 0)
        {
            var loaded = ScanResultStore.Load();
            return ListFindingsDto.From(loaded?.Result, domain, minSeverity, ruleId, limit, offset);
        }
    }

    // ── Result DTOs (public fields; serialized to JSON by the pipeline server) ──

    public sealed class ListFindingsDto
    {
        public string status;        // "ok" | "no_scan" | "empty"
        public int total;            // individual findings matching the filter (before paging)
        public int ruleCount;        // distinct rules matching
        public int score;            // context from the scan being listed
        public string grade;

        public RuleRollupDto[] rules; // rule-level rollup (complete, most-useful-first) — the overview
        public int offset, limit, returned;
        public FindingItemDto[] items; // paged individual findings (drill-down)

        public string hint;
        public string privacy;

        const string PrivacyNote = "Finding paths and line numbers are returned to the agent you connected. The scan runs locally and uploads no code or assets.";

        public static ListFindingsDto From(ScanResult scan, string domain, string minSeverity, string ruleId, int limit, int offset)
        {
            if (limit <= 0) limit = 50;
            if (limit > 200) limit = 200;
            if (offset < 0) offset = 0;

            if (scan == null)
                return Envelope("no_scan", 0, 0, 0, null, offset, limit,
                    Array.Empty<RuleRollupDto>(), Array.Empty<FindingItemDto>(),
                    "No scan found — run perflint_scan first.");

            var filter = FindingQuery.Filter.Parse(minSeverity, domain, ruleId);
            var matched = FindingQuery.Match(scan.Findings, filter);

            if (matched.Count == 0)
                return Envelope("empty", 0, 0, scan.HealthScore(), scan.HealthGrade(), offset, limit,
                    Array.Empty<RuleRollupDto>(), Array.Empty<FindingItemDto>(),
                    "No findings match this filter (the scan itself found " + (scan.Findings?.Count ?? 0) + ")."
                    + UnknownRuleHint(scan.Findings, ruleId));

            var rules = FindingQuery.Rollup(matched).Select(RuleRollupDto.From).ToArray();
            var page = matched.Skip(offset).Take(limit).Select(FindingItemDto.From).ToArray();

            string hint = null;
            int shownEnd = offset + page.Length;
            if (shownEnd < matched.Count)
                hint = (matched.Count - shownEnd) + " more item(s) match — page with offset=" + shownEnd
                       + ". The 'rules' rollup already covers all " + matched.Count + ".";

            var dto = Envelope("ok", matched.Count, rules.Length, scan.HealthScore(), scan.HealthGrade(),
                offset, limit, rules, page, hint);
            return dto;
        }

        /// <summary>
        /// Empty when there is no rule filter or it matches at least one finding in the scan (the empty result came
        /// from the other filters); otherwise names the closest rule ids so a miss is never a silent empty list.
        /// Shared with perflint_fix, which narrows by the same filter.
        /// </summary>
        internal static string UnknownRuleHint(IReadOnlyList<Finding> findings, string ruleId)
        {
            ruleId = ruleId?.Trim();
            if (string.IsNullOrEmpty(ruleId)) return "";
            if (findings != null)
                foreach (var f in findings)
                    if (f != null && FindingQuery.RuleMatches(f.RuleId, ruleId)) return "";
            var closest = FindingQuery.SuggestRuleIds(findings, ruleId);
            return closest.Count == 0 ? ""
                : " Rule id '" + ruleId + "' does not appear in this scan — closest rule ids: "
                  + string.Join(", ", closest) + ".";
        }

        static ListFindingsDto Envelope(string status, int total, int ruleCount, int score, string grade,
            int offset, int limit, RuleRollupDto[] rules, FindingItemDto[] items, string hint) => new ListFindingsDto
        {
            status = status,
            total = total,
            ruleCount = ruleCount,
            score = score,
            grade = grade,
            rules = rules,
            offset = offset,
            limit = limit,
            returned = items.Length,
            items = items,
            hint = hint,
            privacy = PrivacyNote,
        };

    }

    public sealed class RuleRollupDto
    {
        public string ruleId, domain, severity, title;
        public int count;
        public long estMemoryBytes, estBuildBytes;
        public string estMemoryHuman, estBuildHuman;
        public bool canAutoFix, hasAction;

        public static RuleRollupDto From(FindingQuery.RuleRollup r) => new RuleRollupDto
        {
            ruleId = r.RuleId,
            domain = r.Domain.ToString().ToLowerInvariant(),
            severity = r.MaxSeverity.ToString().ToLowerInvariant(),
            title = r.Title,
            count = r.Count,
            estMemoryBytes = r.MemoryBytes,
            estMemoryHuman = ScannerUtil.Human(r.MemoryBytes),
            estBuildBytes = r.BuildBytes,
            estBuildHuman = ScannerUtil.Human(r.BuildBytes),
            canAutoFix = r.CanAutoFix,
            hasAction = r.HasAction,
        };
    }

    public sealed class FindingItemDto
    {
        public string id;           // stable handle: ruleId|path[:line]
        public string ruleId, domain, severity, title, detail, path;
        public int line;
        public long estMemoryBytes, estBuildBytes;
        public string estMemoryHuman, estBuildHuman;
        public bool savingsAreCeiling, canAutoFix, hasAction, aiFixable;

        public static FindingItemDto From(Finding f)
        {
            bool canFix = f.CanAutoFix || f.WasAutoFixable;
            return new FindingItemDto
            {
                id = f.RuleId + "|" + (f.TargetPath ?? "") + (f.CodeLine > 0 ? ":" + f.CodeLine : ""),
                ruleId = f.RuleId,
                domain = f.Domain.ToString().ToLowerInvariant(),
                severity = f.Severity.ToString().ToLowerInvariant(),
                title = f.Title,
                detail = f.Detail,
                path = f.TargetPath,
                line = f.CodeLine,
                estMemoryBytes = f.EstimatedMemorySavingsBytes,
                estMemoryHuman = ScannerUtil.Human(f.EstimatedMemorySavingsBytes),
                estBuildBytes = f.EstimatedBuildSavingsBytes,
                estBuildHuman = ScannerUtil.Human(f.EstimatedBuildSavingsBytes),
                savingsAreCeiling = f.SavingsAreCeiling,
                canAutoFix = canFix,
                hasAction = f.HasAction || f.WasActionable,
                aiFixable = !string.IsNullOrEmpty(f.CodeFile) && f.CodeLine > 0 && !canFix,
            };
        }
    }
}
