using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PerfLint.L10n;

namespace PerfLint.Core
{
    /// <summary>
    /// Exports a single scan result as a [self-contained single-file HTML] — a cold-start shareable acquisition hook:
    /// developers share it with teammates or the community, and it spreads naturally.
    ///
    /// Hard constraints (consistent with the product's "local-first / zero telemetry" trust foundation):
    ///   · Fully self-contained: inline CSS, no external CSS/JS/fonts/images, no network requests — viewable offline, never phones home.
    ///   · All dynamic content (project name, rule titles, paths, detail) is HTML-escaped to prevent &lt;&gt;&amp; in content from corrupting the page.
    ///   · Pure function, does not touch the Unity API (project name / timestamp are passed in by the caller) → unit-testable.
    ///
    /// Score/counts are computed from the full result set (the headline reflects overall health);
    /// the per-item list is grouped by rule with a per-rule cap (instances can number in the tens of thousands — capped to prevent file bloat).
    /// Copy goes through L.Tr to follow the UI language; finding titles/details are already generated in the UI language at scan time, so the whole page stays language-consistent.
    /// </summary>
    public static class HtmlReport
    {
        /// <summary>Maximum number of instance rows listed per rule (the remainder is collapsed with "… N more", preventing tens of thousands of entries from bloating the file).</summary>
        public const int MaxRowsPerRule = 50;

        public static string Build(ScanResult result, string projectName, string generatedAtLocal)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            projectName = string.IsNullOrEmpty(projectName) ? "Unity project" : projectName;
            generatedAtLocal ??= "";

            int score = result.HealthScore();
            string grade = result.HealthGrade();

            var sb = new StringBuilder(64 * 1024);
            sb.Append("<!DOCTYPE html><html lang=\"")
              .Append(L.Current == Lang.Chinese ? "zh" : "en")
              .Append("\"><head><meta charset=\"utf-8\">")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
              .Append("<title>PerfLint — ").Append(Esc(projectName)).Append("</title>");
            sb.Append("<style>").Append(Css(grade)).Append("</style></head><body>");

            // ── Header: brand + project + score ──
            sb.Append("<div class=\"wrap\">");
            sb.Append("<header><div class=\"brand\">PerfLint <span class=\"sub\">for Unity</span></div>")
              .Append("<div class=\"meta\">").Append(Esc(projectName));
            if (!string.IsNullOrEmpty(generatedAtLocal))
                sb.Append(" · ").Append(Esc(generatedAtLocal));
            sb.Append("</div></header>");

            sb.Append("<section class=\"score\">")
              .Append("<div class=\"grade\">").Append(Esc(grade)).Append("</div>")
              .Append("<div class=\"scoreinfo\"><div class=\"num\">").Append(score)
              .Append("<span class=\"den\">/100</span></div>")
              .Append("<div class=\"label\">").Append(L.Tr("Project health score", "项目健康度评分")).Append("</div></div>")
              .Append("</section>");

            // ── Count badges ──
            sb.Append("<section class=\"counts\">");
            Badge(sb, "crit", result.CriticalCount, L.Tr("Critical", "严重"));
            Badge(sb, "warn", result.WarningCount, L.Tr("Warning", "警告"));
            Badge(sb, "info", result.InfoCount, L.Tr("Info", "提示"));
            Badge(sb, "fix", result.AutoFixableCount, L.Tr("One-click fixable", "可一键修复"));
            sb.Append("</section>");

            // ── Per-domain → per-rule ──
            foreach (var domainGroup in result.ByDomain())
            {
                var rules = domainGroup
                    .GroupBy(f => f.RuleId)
                    .OrderByDescending(g => g.Max(f => f.Severity))
                    .ThenByDescending(g => g.Count())
                    .ToList();

                sb.Append("<h2>").Append(Esc(domainGroup.Key.ToString()))
                  .Append(" <span class=\"dimcount\">(").Append(domainGroup.Count()).Append(")</span></h2>");

                foreach (var rule in rules)
                {
                    var items = rule.ToList();
                    Severity sev = items.Max(f => f.Severity);
                    string sevClass = sev == Severity.Critical ? "crit" : sev == Severity.Warning ? "warn" : "info";
                    // Group header title: prefer GroupTitleOrTitle (which excludes quantities unique to a single instance).
                    string title = items[0].GroupTitleOrTitle;

                    sb.Append("<div class=\"rule\"><div class=\"rulehead\"><span class=\"dot ").Append(sevClass).Append("\"></span>")
                      .Append("<span class=\"rid\">").Append(Esc(rule.Key)).Append("</span> ")
                      .Append("<span class=\"rtitle\">").Append(Esc(title)).Append("</span>")
                      .Append("<span class=\"rcount\">").Append(items.Count).Append("</span></div>");

                    int shown = Math.Min(items.Count, MaxRowsPerRule);
                    sb.Append("<ul>");
                    for (int i = 0; i < shown; i++)
                    {
                        var f = items[i];
                        string loc = f.TargetPath ?? f.CodeFile;
                        sb.Append("<li><span class=\"li-title\">").Append(Esc(f.Title)).Append("</span>");
                        if (!string.IsNullOrEmpty(loc))
                            sb.Append("<span class=\"li-path\">").Append(Esc(loc)).Append("</span>");
                        sb.Append("</li>");
                    }
                    if (items.Count > shown)
                        sb.Append("<li class=\"more\">… ").Append(items.Count - shown).Append(' ')
                          .Append(L.Tr("more", "条更多")).Append("</li>");
                    sb.Append("</ul></div>");
                }
            }

            if (result.Findings.Count == 0)
                sb.Append("<p class=\"empty\">").Append(L.Tr("No issues found. 🎉", "未发现问题。🎉")).Append("</p>");

            // ── Footer: trust copy ──
            sb.Append("<footer>")
              .Append(L.Tr(
                  "Generated locally by PerfLint for Unity. Scans run entirely on your machine and are never uploaded — this report contains only what you see here.",
                  "由 PerfLint for Unity 在本地生成。扫描全程在你的机器上完成、永不上传——本报告只包含你在此看到的内容。"))
              .Append("</footer>");

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private static void Badge(StringBuilder sb, string cls, int n, string label)
        {
            sb.Append("<div class=\"badge ").Append(cls).Append("\"><div class=\"bn\">").Append(n)
              .Append("</div><div class=\"bl\">").Append(Esc(label)).Append("</div></div>");
        }

        /// <summary>HTML escaping: &amp; &lt; &gt; " in dynamic content must be escaped; otherwise special characters in paths/titles will corrupt the page or be interpreted as tags.</summary>
        internal static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string GradeColor(string grade)
        {
            switch (grade)
            {
                case "A": return "#3fb950";
                case "B": return "#56d364";
                case "C": return "#d29922";
                case "D": return "#db6d28";
                default: return "#f85149"; // F
            }
        }

        private static string Css(string grade) =>
            "*{box-sizing:border-box}" +
            "body{margin:0;background:#0d1117;color:#c9d1d9;font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}" +
            ".wrap{max-width:900px;margin:0 auto;padding:32px 20px 64px}" +
            "header{display:flex;justify-content:space-between;align-items:baseline;border-bottom:1px solid #21262d;padding-bottom:12px}" +
            ".brand{font-size:20px;font-weight:700;color:#f0f6fc}.brand .sub{font-weight:400;color:#8b949e;font-size:14px}" +
            ".meta{color:#8b949e;font-size:12px}" +
            ".score{display:flex;align-items:center;gap:20px;margin:28px 0}" +
            ".grade{width:84px;height:84px;border-radius:50%;display:flex;align-items:center;justify-content:center;" +
            "font-size:44px;font-weight:800;color:#fff;background:" + GradeColor(grade) + "}" +
            ".scoreinfo .num{font-size:34px;font-weight:700;color:#f0f6fc}.scoreinfo .den{font-size:16px;color:#8b949e;font-weight:400}" +
            ".scoreinfo .label{color:#8b949e;font-size:13px}" +
            ".counts{display:flex;gap:12px;flex-wrap:wrap;margin:8px 0 24px}" +
            ".badge{flex:1;min-width:110px;background:#161b22;border:1px solid #21262d;border-radius:8px;padding:12px 14px}" +
            ".badge .bn{font-size:24px;font-weight:700;color:#f0f6fc}.badge .bl{font-size:12px;color:#8b949e}" +
            ".badge.crit .bn{color:#f85149}.badge.warn .bn{color:#d29922}.badge.fix .bn{color:#3fb950}" +
            "h2{margin:28px 0 10px;font-size:16px;color:#f0f6fc;font-weight:600}.dimcount{color:#8b949e;font-weight:400;font-size:13px}" +
            ".rule{background:#161b22;border:1px solid #21262d;border-radius:8px;margin:8px 0;overflow:hidden}" +
            ".rulehead{display:flex;align-items:center;gap:8px;padding:10px 12px;background:#1c2128}" +
            ".dot{width:9px;height:9px;border-radius:50%;flex:none}.dot.crit{background:#f85149}.dot.warn{background:#d29922}.dot.info{background:#58a6ff}" +
            ".rid{font-family:ui-monospace,Consolas,monospace;font-size:12px;color:#8b949e}" +
            ".rtitle{flex:1;color:#f0f6fc}.rcount{background:#30363d;color:#c9d1d9;border-radius:10px;padding:1px 8px;font-size:12px}" +
            ".rule ul{list-style:none;margin:0;padding:4px 0}" +
            ".rule li{padding:5px 12px 5px 29px;border-top:1px solid #21262d;display:flex;flex-direction:column}" +
            ".li-title{color:#c9d1d9}.li-path{color:#6e7681;font-size:12px;word-break:break-all}" +
            ".rule li.more{color:#6e7681;font-style:italic}" +
            ".empty{color:#3fb950;font-size:16px;text-align:center;padding:40px}" +
            "footer{margin-top:36px;padding-top:16px;border-top:1px solid #21262d;color:#6e7681;font-size:12px}";
    }
}
