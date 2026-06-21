using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.L10n;
using PerfLint.Scanners;
using UnityEngine;

namespace PerfLint.Core
{
    /// <summary>
    /// Persists a single scan result to disk (<c>Library/PerfLint/last-scan.json</c>) so that the report
    /// survives domain reloads, window reopens, and editor restarts.
    ///
    /// Background: an AI fix modifies code → recompile → Unity domain reload wipes _lastResult from window
    /// memory. The only previous recovery path was an ~86-second full rescan ("Refresh Report" button),
    /// which is worthless to the user. After persistence, a window reload can restore the baseline from disk
    /// and then incrementally rescan only the modified files — no full rescan needed.
    ///
    /// Limitations: the Ping / Fix / Action members of <see cref="Finding"/> are delegates / interface
    /// instances and cannot be serialized.
    /// - Ping is reconstructed generically from CodeFile/TargetPath (the vast majority were already
    ///   <see cref="ScannerUtil.OpenScriptAtLine"/> / <see cref="ScannerUtil.PingAsset"/>). Group is a
    ///   plain path list and is restored as-is; "select group" remains functional.
    /// - Fix / Action cannot be reconstructed → a restored finding carries no one-click fix; callers
    ///   expose a "Refresh this rule to re-enable fixes" action for rules that previously had a fix
    ///   (triggers an incremental rescan of that rule to get back the live instances). AiFixable depends
    ///   only on CodeFile/CodeLine and remains usable after restore.
    /// </summary>
    public static class ScanResultStore
    {
        /// <summary>Restored result plus metadata.</summary>
        public sealed class Restored
        {
            public ScanResult Result;
            /// <summary>Set of rule IDs that had a Fix or Action before restore — their one-click fix must be revived by rescanning that rule.</summary>
            public HashSet<string> FixableRuleIds;
        }

        private static string FilePath
        {
            get
            {
                // Application.dataPath = <project>/Assets → one level up is the project root; Library sits next to Assets.
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint", "last-scan.json");
            }
        }

        /// <summary>Persists the current scan result. Any IO/serialization failure is swallowed — persistence is an optimization and must never drag down the main scan pipeline.</summary>
        public static void Save(ScanResult result)
        {
            if (result == null) return;
            try
            {
                var dto = new Dto
                {
                    completedTicks = result.CompletedAtUtc.Ticks,
                    durationTicks = result.Duration.Ticks,
                    findings = result.Findings.Select(ToDto).ToArray(),
                    ruleMap = (result.ScannerRuleMap ?? new Dictionary<string, IReadOnlyList<string>>())
                        .Select(kv => new RuleMapDto { scanner = kv.Key, ruleIds = kv.Value?.ToArray() ?? Array.Empty<string>() })
                        .ToArray()
                };

                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(dto));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr($"Failed to persist scan results (does not affect usage): {ex.Message}", $"扫描结果持久化失败（不影响使用）：{ex.Message}"));
            }
        }

        /// <summary>Restores the last scan result from disk. Returns null if no file exists or parsing fails (caller falls back to the no-result state).</summary>
        public static Restored Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return null;

                var dto = JsonUtility.FromJson<Dto>(File.ReadAllText(path));
                if (dto?.findings == null) return null;

                var findings = dto.findings.Select(FromDto).ToList();

                var map = new Dictionary<string, IReadOnlyList<string>>();
                if (dto.ruleMap != null)
                    foreach (var e in dto.ruleMap)
                        if (!string.IsNullOrEmpty(e.scanner))
                            map[e.scanner] = e.ruleIds ?? Array.Empty<string>();

                var fixableRules = new HashSet<string>(
                    dto.findings.Where(f => f.hadFix || f.hadAction).Select(f => f.ruleId));

                var result = new ScanResult(
                    findings,
                    TimeSpan.FromTicks(dto.durationTicks),
                    map.Count > 0 ? map : null,
                    new DateTime(dto.completedTicks, DateTimeKind.Utc));

                return new Restored { Result = result, FixableRuleIds = fixableRules };
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr($"Failed to restore scan results (treating as not scanned): {ex.Message}", $"扫描结果恢复失败（将作未扫描处理）：{ex.Message}"));
                return null;
            }
        }

        /// <summary>Returns whether a persisted scan result exists (does not read the file, only checks for its presence). The change tracker uses this to decide whether it is worth recording an incremental delta.</summary>
        public static bool Exists()
        {
            try { return File.Exists(FilePath); }
            catch { return false; }
        }

        /// <summary>Deletes the persisted file (e.g. when the user explicitly resets). Failures are silently ignored.</summary>
        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* ignore */ }
        }

        // ── Serialization mapping ──────────────────────────────

        private static FindingDto ToDto(Finding f) => new FindingDto
        {
            ruleId = f.RuleId,
            domain = (int)f.Domain,
            severity = (int)f.Severity,
            title = f.Title,
            groupTitle = f.GroupTitle,
            detail = f.Detail,
            targetPath = f.TargetPath,
            group = f.Group?.ToArray(),
            codeFile = f.CodeFile,
            codeLine = f.CodeLine,
            // "currently fixable || was previously fixable": a restored finding loses its Fix instance
            // (CanAutoFix=false) but carries WasAutoFixable, so that re-persisting from the restored state
            // does not erase a rule that was once fixable (otherwise the "Refresh to enable fix" button
            // would disappear permanently).
            hadFix = f.CanAutoFix || f.WasAutoFixable,
            hadAction = f.HasAction || f.WasActionable
        };

        private static Finding FromDto(FindingDto d)
        {
            // Generic Ping reconstruction: ① has a code location → open the script and jump to that line;
            // ② TargetPath looks like "X.cs:line" (report-style script findings such as GC004 that have no
            // CodeFile) → parse out path and line number and open to that line; ③ plain asset path →
            // highlight in the Project window.
            Action ping = null;
            if (!string.IsNullOrEmpty(d.codeFile) && d.codeLine > 0)
            {
                string cf = d.codeFile; int cl = d.codeLine;
                ping = () => ScannerUtil.OpenScriptAtLine(cf, cl);
            }
            else if (TryParsePathLine(d.targetPath, out string sp, out int sl))
            {
                ping = () => ScannerUtil.OpenScriptAtLine(sp, sl);
            }
            else if (LooksLikeAssetPath(d.targetPath))
            {
                string tp = d.targetPath;
                ping = () => ScannerUtil.PingAsset(tp);
            }

            return new Finding(
                ruleId: d.ruleId,
                domain: (Domain)d.domain,
                severity: (Severity)d.severity,
                title: d.title,
                groupTitle: string.IsNullOrEmpty(d.groupTitle) ? null : d.groupTitle,
                detail: d.detail,
                targetPath: d.targetPath,
                ping: ping,
                fix: null,        // not serializable: rescanning the rule on demand will swap in a finding with a live Fix instance
                group: d.group != null && d.group.Length > 0 ? d.group : null,
                // JsonUtility serializes null strings as ""; normalize back to null on restore — otherwise
                // report-style rules (no CodeFile) would have CodeFile set to "" after restore, which is
                // inconsistent with the live state (AiFixable etc. use IsNullOrEmpty so they are unaffected,
                // but keeping things clean is better).
                codeFile: string.IsNullOrEmpty(d.codeFile) ? null : d.codeFile,
                codeLine: d.codeLine,
                action: null,     // same as Fix
                // Remember "was auto-fixable / was actionable" so that hadFix/hadAction are not erased
                // when re-persisting (the fix entry point must not be lost).
                wasAutoFixable: d.hadFix,
                wasActionable: d.hadAction);
        }

        private static bool LooksLikeAssetPath(string p) =>
            !string.IsNullOrEmpty(p) &&
            (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal));

        /// <summary>Splits the "Assets/X.cs:42" form used by script findings into a path and line number. Returns false if the input does not match this pattern.</summary>
        private static bool TryParsePathLine(string s, out string path, out int line)
        {
            path = null; line = 0;
            if (!LooksLikeAssetPath(s)) return false;
            int colon = s.LastIndexOf(':');
            if (colon <= 0 || colon == s.Length - 1) return false;
            if (!int.TryParse(s.Substring(colon + 1), out line) || line <= 0) return false;
            path = s.Substring(0, colon);
            return path.EndsWith(".cs", StringComparison.Ordinal);
        }

        // ── JsonUtility DTOs (supports only public fields / arrays / nested serializable classes; no Dictionary support) ──

        [Serializable]
        private sealed class Dto
        {
            public long completedTicks;
            public long durationTicks;
            public FindingDto[] findings;
            public RuleMapDto[] ruleMap;
        }

        [Serializable]
        private sealed class FindingDto
        {
            public string ruleId;
            public int domain;
            public int severity;
            public string title;
            public string groupTitle;
            public string detail;
            public string targetPath;
            public string[] group;
            public string codeFile;
            public int codeLine;
            public bool hadFix;
            public bool hadAction;
        }

        [Serializable]
        private sealed class RuleMapDto
        {
            public string scanner;
            public string[] ruleIds;
        }
    }
}
