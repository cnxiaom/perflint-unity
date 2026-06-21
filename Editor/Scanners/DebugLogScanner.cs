using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 diagnostic for build-leftover Debug.Log calls (lightweight text heuristic; will be made precise and support safe auto-wrapping once Roslyn is integrated in W3).
    ///   PERF.LOG001 — Debug.Log/Debug.LogFormat in runtime scripts.
    /// String concatenation arguments plus the call itself still incur cost in release builds; a high volume of calls can slow down hot paths.
    /// Only runtime scripts are scanned; Editor directories are skipped (they are not included in builds). // line comments, /* */ block comments, and string literals are stripped before matching to reduce false positives.
    /// Auto-fix is not provided for now (auto-wrapping with #if carries non-trivial risk; left for the Roslyn phase where it can be done safely).
    /// </summary>
    public sealed class DebugLogScanner : IScanner, IFileScanner
    {
        public string Name => "Debug.Log Usage";
        public Domain Domain => Domain.Performance;

        private static readonly Regex LogCall = new Regex(
            @"\bDebug\s*\.\s*Log(Format)?\s*\(", RegexOptions.Compiled);

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var guids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!Handles(path)) continue;

                foreach (var f in ScanFile(path, context))
                    yield return f;
            }
        }

        /// <summary>Runtime .cs files (skipping Editor directories, which are not included in builds).</summary>
        public bool Handles(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".cs")) return false;
            return !assetPath.Replace('\\', '/').Contains("/Editor/");
        }

        public IEnumerable<Finding> ScanFile(string assetPath, ScanContext context)
        {
            if (!Handles(assetPath)) yield break;

            int count = CountLogCalls(assetPath, out int firstLine);
            if (count <= 0) yield break;

            var severity = count >= 10 ? Severity.Warning : Severity.Info;
            yield return new Finding(
                ruleId: "PERF.LOG001",
                domain: Domain.Performance,
                severity: severity,
                title: L.Tr($"Runtime script has {count} Debug.Log calls", $"运行时脚本含 {count} 处 Debug.Log"),
                // Group header uses the rule-level title (without the per-file count) — otherwise the rule group header would show the "N occurrences" of the first file, which is misleading for the whole group.
                groupTitle: L.Tr("Runtime scripts contain Debug.Log", "运行时脚本含 Debug.Log"),
                detail: L.Tr($"'{Path.GetFileName(assetPath)}' contains {count} Debug.Log/LogFormat calls. In release builds, the string construction and log calls " +
                        "still cost time, especially on hot paths. Use a logging wrapper guarded by [System.Diagnostics.Conditional], or isolate them with " +
                        "#if UNITY_EDITOR / a custom DEV define. (Text heuristic; // and /* */ comments and string literals are already excluded. W3 will make this precise via Roslyn.)",
                        $"'{Path.GetFileName(assetPath)}' 含 {count} 处 Debug.Log/LogFormat。发布版中字符串构造与日志调用" +
                        "仍有开销，热路径里尤甚。建议用 [System.Diagnostics.Conditional] 包裹的日志封装，或用 " +
                        "#if UNITY_EDITOR / 自定义 DEV 宏隔离。（文本启发式，已剔除 // 与 /* */ 注释及字符串字面量，W3 将用 Roslyn 精确化。）"),
                // The line number is encoded into targetPath ("X.cs:line") so that "jump to first occurrence" still works after the report is restored from disk:
                // on restore the ping delegate cannot be serialized, so ScanResultStore reconstructs a line-aware ping from the ":line" suffix in targetPath.
                // codeFile/codeLine are intentionally not set — LOG001 has no AI fix, keeping AiFixable=false (same pattern as report-only rules like GC004).
                targetPath: $"{assetPath}:{firstLine}",
                // Locate opens the script and jumps to the first Debug.Log occurrence, rather than merely highlighting the file in the Project window.
                ping: () => ScannerUtil.OpenScriptAtLine(assetPath, firstLine));
        }

        /// <summary>Counts the number of Debug.Log calls; <paramref name="firstLine"/> returns the 1-based line number of the first occurrence (0 if no match).</summary>
        private static int CountLogCalls(string assetPath, out int firstLine)
        {
            firstLine = 0;
            string full;
            try
            {
                full = Path.GetFullPath(assetPath);
                if (!File.Exists(full)) return 0;
            }
            catch
            {
                return 0;
            }

            return CountLogCallsInLines(File.ReadLines(full), out firstLine);
        }

        /// <summary>Pure logic: counts Debug.Log/LogFormat calls in an already-read sequence of lines (matching after stripping comments and strings), making end-to-end unit testing straightforward.</summary>
        internal static int CountLogCallsInLines(IEnumerable<string> lines, out int firstLine)
        {
            firstLine = 0;
            int count = 0, lineNo = 0;
            bool inBlock = false; // tracks whether we are currently inside a multi-line /* ... */ block comment
            foreach (var raw in lines)
            {
                lineNo++;
                string line = StripNonCode(raw, ref inBlock);
                if (line.Length == 0) continue;
                int matches = LogCall.Matches(line).Count;
                if (matches > 0 && firstLine == 0) firstLine = lineNo;
                count += matches;
            }
            return count;
        }

        /// <summary>
        /// Strips // line comments, /* */ block comments (multi-line; state tracked via <paramref name="inBlock"/>), and
        /// the contents of ordinary "..." string literals from a single line, returning the remaining text that is safe to match against.
        /// Heuristic: does not cover all edge cases of @"..." verbatim strings and string interpolation boundaries, but eliminates the vast majority of false-positive Debug.Log matches inside comments and strings.
        /// </summary>
        internal static string StripNonCode(string line, ref bool inBlock)
        {
            var sb = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                if (inBlock)
                {
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) return sb.ToString(); // the rest of the line is entirely inside a block comment
                    inBlock = false;
                    i = end + 2;
                    continue;
                }

                char c = line[i];
                char next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (c == '/' && next == '/') break;                            // line comment: discard everything that follows
                if (c == '/' && next == '*') { inBlock = true; i += 2; continue; } // enter block comment
                if (c == '@' && next == '"')                                       // verbatim string @"…" ("" escape, \ not an escape)
                {
                    i += 2;
                    while (i < line.Length)
                    {
                        if (line[i] == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"') { i += 2; continue; }
                            i++; break;
                        }
                        i++;
                    }
                    continue;
                }
                if (c == '"')
                {
                    i++; // skip string literal contents (handling \" escapes) to avoid false-positives from // /* Debug.Log inside strings
                    while (i < line.Length)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == '"') { i++; break; }
                        i++;
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
