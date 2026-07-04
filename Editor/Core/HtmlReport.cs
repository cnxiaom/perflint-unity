using System;
using System.Collections.Generic;
using System.Globalization;
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
            sb.Append("<header><div class=\"brand\"><span class=\"mark\">P</span>PerfLint <span class=\"sub\">for Unity</span></div>")
              .Append("<div class=\"meta\">").Append(Esc(projectName));
            if (!string.IsNullOrEmpty(generatedAtLocal))
                sb.Append(" · ").Append(Esc(generatedAtLocal));
            // Scan duration: a quiet "this was fast & local" signal.
            sb.Append("<br>").Append(L.Tr("scanned in ", "扫描耗时 "))
              .Append(result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
            sb.Append("</div></header>");

            // Score block mirrors the site's sample-report head (site/src/pages/report.astro):
            // a grade-coloured ring around the number, with "Project health: <grade>" beside it.
            // Ring mirrors the editor's gauge: a grade-coloured progress arc (proportional to the score) over a faint
            // track, with the score number in the centre. Pure CSS conic-gradient — still fully self-contained (no JS).
            string ringColor = GradeColor(grade);
            sb.Append("<section class=\"score\">")
              .Append("<div class=\"ring\" style=\"background:conic-gradient(").Append(ringColor).Append(" 0 ").Append(score)
              .Append("%,#2f3742 ").Append(score).Append("% 100%)\"><span class=\"g\">").Append(score).Append("</span></div>")
              .Append("<div class=\"scorehead\">")
              .Append("<div class=\"health\">").Append(L.Tr("Project health", "项目健康"))
              .Append(": <span class=\"grade\">").Append(Esc(grade)).Append("</span></div>")
              .Append("<div class=\"label\">").Append(score).Append(" / 100</div>")
              .Append("</div></section>");

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
                    bool fixable = items.Any(f => f.CanAutoFix || f.WasAutoFixable);

                    sb.Append("<details class=\"rule\"><summary class=\"rulehead\"><span class=\"dot ").Append(sevClass).Append("\"></span>")
                      .Append("<span class=\"rtitle\">").Append(Esc(title)).Append("</span>")
                      .Append("<span class=\"pill sev-").Append(sevClass).Append("\">").Append(Esc(SevLabel(sev))).Append("</span>")
                      .Append("<span class=\"pill\">").Append(Esc(domainGroup.Key.ToString())).Append("</span>")
                      .Append("<span class=\"pill mono\">").Append(Esc(rule.Key)).Append("</span>");
                    if (fixable)
                        sb.Append("<span class=\"pill fix\">").Append(L.Tr("One-click fix", "可一键修复")).Append("</span>");
                    sb.Append("<span class=\"rcount\">").Append(items.Count).Append("</span>")
                      .Append("<span class=\"chev\">▸</span></summary>");

                    // Rule-level impact / fix advice — the "why + how to fix" content, shared across instances.
                    string detail = items[0].Detail;
                    if (!string.IsNullOrEmpty(detail))
                        sb.Append("<div class=\"detail\">").Append(Esc(detail)).Append("</div>");

                    int shown = Math.Min(items.Count, MaxRowsPerRule);
                    sb.Append("<ul>");
                    for (int i = 0; i < shown; i++)
                    {
                        var f = items[i];
                        string loc = f.TargetPath ?? f.CodeFile;
                        // Only repeat the instance title when it carries something the group header doesn't
                        // (e.g. a per-file quantity). Avoids "Oversized texture / Oversized texture" noise.
                        bool showTitle = !string.Equals(f.Title, title, StringComparison.Ordinal);
                        // A single project-wide finding (no path, title == header) would otherwise emit an empty row.
                        if (!showTitle && string.IsNullOrEmpty(loc)) continue;
                        sb.Append("<li>");
                        if (showTitle)
                            sb.Append("<span class=\"li-title\">").Append(Esc(f.Title)).Append("</span>");
                        if (!string.IsNullOrEmpty(loc))
                            sb.Append("<span class=\"li-path\">").Append(Esc(loc)).Append("</span>");
                        sb.Append("</li>");
                    }
                    if (items.Count > shown)
                        sb.Append("<li class=\"more\">… ").Append(items.Count - shown).Append(' ')
                          .Append(L.Tr("more", "条更多")).Append("</li>");
                    sb.Append("</ul></details>");
                }
            }

            if (result.Findings.Count == 0)
                sb.Append("<p class=\"empty\">").Append(L.Tr("No issues found. 🎉", "未发现问题。🎉")).Append("</p>");

            // ── Footer: trust copy first, then a low-key attribution link ──
            sb.Append("<footer>")
              .Append(L.Tr(
                  "Generated locally by PerfLint for Unity. Scans run entirely on your machine and are never uploaded — this report contains only what you see here.",
                  "由 PerfLint for Unity 在本地生成。扫描全程在你的机器上完成、永不上传——本报告只包含你在此看到的内容。"))
              .Append("<br><span class=\"attr\">")
              .Append(L.Tr("Generated by PerfLint for Unity — ", "本报告由 PerfLint for Unity 生成 — "))
              .Append("<a href=\"https://perflint.dev/?ref=report\">perflint.dev</a>")
              .Append("</span></footer>");

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private static string SevLabel(Severity sev) =>
            sev == Severity.Critical ? L.Tr("Critical", "严重")
            : sev == Severity.Warning ? L.Tr("Warning", "警告")
            : L.Tr("Info", "提示");

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
            "body{margin:0;background:#0d1117;color:#e6edf3;font:14px/1.6 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Helvetica,Arial,sans-serif;-webkit-font-smoothing:antialiased}" +
            ".wrap{max-width:900px;margin:0 auto;padding:32px 20px 64px}" +
            "header{display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #2a313c;padding-bottom:14px;gap:16px}" +
            ".brand{display:flex;align-items:center;gap:9px;font-size:19px;font-weight:700;color:#e6edf3}" +
            ".brand .mark{width:24px;height:24px;border-radius:7px;background:#4cc38a;color:#04140d;display:flex;align-items:center;justify-content:center;font-size:15px;font-weight:800}" +
            ".brand .sub{font-weight:400;color:#9aa7b4;font-size:14px}" +
            ".meta{color:#9aa7b4;font-size:12px;text-align:right;line-height:1.5}" +
            ".score{display:flex;align-items:center;gap:24px;margin:30px 0 8px;flex-wrap:wrap}" +
            ".ring{width:96px;height:96px;border-radius:50%;position:relative;display:flex;align-items:center;justify-content:center;flex:none}" +
            ".ring::before{content:'';position:absolute;inset:9px;border-radius:50%;background:#0d1117}" +
            ".ring .g{position:relative;font-size:30px;font-weight:700;color:" + GradeColor(grade) + "}" +
            ".scorehead .health{font-size:24px;font-weight:700;color:#e6edf3}" +
            ".scorehead .health .grade{color:" + GradeColor(grade) + "}" +
            ".scorehead .label{color:#9aa7b4;font-size:13px;margin-top:3px}" +
            ".counts{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0 26px}" +
            ".badge{flex:1;min-width:120px;background:#161b22;border:1px solid #2a313c;border-radius:12px;padding:14px 16px}" +
            ".badge .bn{font-size:26px;font-weight:700;color:#e6edf3}.badge .bl{font-size:12px;color:#9aa7b4}" +
            ".badge.crit .bn{color:#ef5350}.badge.warn .bn{color:#f0b429}.badge.fix .bn{color:#4cc38a}" +
            "h2{margin:30px 0 12px;font-size:16px;color:#e6edf3;font-weight:600}.dimcount{color:#9aa7b4;font-weight:400;font-size:13px}" +
            ".rule{border:1px solid #2a313c;border-radius:10px;margin:10px 0;overflow:hidden}" +
            ".rulehead{display:flex;align-items:center;gap:9px;padding:12px 16px;background:#1a2029;flex-wrap:wrap}" +
            ".rule>summary{cursor:pointer;list-style:none}.rule>summary::-webkit-details-marker{display:none}" +
            ".rule>summary:hover{background:#1f2630}" +
            ".chev{color:#7d8896;font-size:12px;display:inline-block;transition:transform .15s}" +
            ".rule[open]>summary .chev{transform:rotate(90deg)}" +
            ".dot{width:10px;height:10px;border-radius:50%;flex:none}.dot.crit{background:#ef5350}.dot.warn{background:#f0b429}.dot.info{background:#5b9dff}" +
            ".rtitle{font-weight:600;color:#e6edf3}" +
            ".pill{font-size:11px;border:1px solid #2a313c;border-radius:999px;padding:2px 8px;color:#9aa7b4;white-space:nowrap}" +
            ".pill.mono{font-family:ui-monospace,Consolas,monospace}" +
            ".pill.sev-crit{color:#ef5350;border-color:rgba(239,83,80,.4)}" +
            ".pill.sev-warn{color:#f0b429;border-color:rgba(240,180,41,.4)}" +
            ".pill.sev-info{color:#5b9dff;border-color:rgba(91,157,255,.4)}" +
            ".pill.fix{color:#4cc38a;border-color:rgba(76,195,138,.4)}" +
            ".rcount{margin-left:auto;background:#2a313c;color:#e6edf3;border-radius:999px;padding:1px 9px;font-size:12px}" +
            ".detail{padding:11px 16px;color:#9aa7b4;border-top:1px solid #2a313c;font-size:13px}" +
            ".rule ul{list-style:none;margin:0;padding:4px 0;border-top:1px solid #2a313c}.detail+ul{border-top:none}.rule ul:empty{display:none}" +
            ".rule li{padding:6px 16px;display:flex;flex-direction:column;gap:1px}" +
            ".rule li+li{border-top:1px solid rgba(42,49,60,.55)}" +
            ".li-title{color:#e6edf3}.li-path{color:#7d8896;font-size:12px;font-family:ui-monospace,Consolas,monospace;word-break:break-all}" +
            ".rule li.more{color:#7d8896;font-style:italic}" +
            ".empty{color:#4cc38a;font-size:16px;text-align:center;padding:40px}" +
            "footer{margin-top:40px;padding-top:16px;border-top:1px solid #2a313c;color:#7d8896;font-size:12px}" +
            "footer .attr{display:inline-block;margin-top:6px;color:#5b6672}footer .attr a{color:#4cc38a;font-weight:600;text-decoration:none}footer .attr a:hover{text-decoration:underline}";
    }
}
