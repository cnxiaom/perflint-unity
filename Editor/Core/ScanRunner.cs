using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using PerfLint.L10n;

namespace PerfLint.Core
{
    /// <summary>
    /// Scan scheduler: discovers all IScanner implementations via reflection → executes them in sequence → aggregates into a ScanResult.
    /// An exception thrown by a single scanner must not abort the entire scan (isolate the failure, log it, and continue).
    /// </summary>
    public static class ScanRunner
    {
        private static bool? _deepScriptAvailable;

        /// <summary>
        /// Whether deep script analysis (Roslyn: ScriptGcScanner / PerFrameAllocationWalker, rules GC001-004 / UPD001-003 / CPU001)
        /// has been compiled into the project. This rule set lives in the separate assembly PerfLint.Editor.Roslyn and is only
        /// compiled when the project defines the PERFLINT_ROSLYN scripting symbol and includes
        /// Microsoft.CodeAnalysis(.CSharp).dll. The main assembly does not have that symbol, so #if cannot be used;
        /// instead, we probe via reflection whether the scanner type exists. When unavailable, the UI should notify
        /// the user that script analysis has fallen back to text-level only (regex rules such as LOG001).
        /// The result is cached per session (the set of loaded assemblies does not change before a domain reload,
        /// and a domain reload resets static fields).
        /// </summary>
        public static bool IsDeepScriptAnalysisAvailable()
        {
            if (_deepScriptAvailable.HasValue) return _deepScriptAvailable.Value;

            bool found = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType("PerfLint.Scripting.ScriptGcScanner", throwOnError: false) != null)
                    {
                        found = true;
                        break;
                    }
                }
                catch { /* some assemblies throw on GetType — ignore and continue */ }
            }

            _deepScriptAvailable = found;
            return found;
        }

        /// <summary>Discovers and instantiates all non-abstract IScanner implementations that have a parameterless constructor, via reflection.</summary>
        public static List<IScanner> DiscoverScanners()
        {
            var scannerType = typeof(IScanner);
            var result = new List<IScanner>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var t in types)
                {
                    // Wrap the entire body in try/catch: not just instantiation — even IsAssignableFrom/GetConstructor
                    // can throw TypeLoadException when a type's base class or field type fails to load
                    // (typical case: optional Roslyn module has a mismatched dependency version, e.g.
                    // System.Collections.Immutable version mismatch). A type that cannot be loaded must never
                    // bring down the entire scanner discovery pass (the core scan must continue running normally).
                    try
                    {
                        if (t == null || t.IsAbstract || t.IsInterface) continue;
                        if (!scannerType.IsAssignableFrom(t)) continue;
                        if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                        result.Add((IScanner)Activator.CreateInstance(t));
                    }
                    catch (Exception ex)
                    {
                        string name = t != null ? t.FullName : "<unknown>";
                        UnityEngine.Debug.LogWarning("[PerfLint] " + L.Tr($"Skipped type that could not be loaded/instantiated {name}: {ex.Message}", $"跳过无法加载/实例化的类型 {name}: {ex.Message}"));
                    }
                }
            }

            return result.OrderBy(s => s.Domain).ThenBy(s => s.Name).ToList();
        }

        public static ScanResult Run(
            ScanContext context = null,
            IReadOnlyList<IScanner> scanners = null)
        {
            context ??= new ScanContext();
            scanners ??= DiscoverScanners();

            var findings = new List<Finding>();
            var ruleMap = new Dictionary<string, IReadOnlyList<string>>();
            var sw = Stopwatch.StartNew();
            int total = Math.Max(1, scanners.Count);

            for (int i = 0; i < scanners.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var scanner = scanners[i];
                context.ReportProgress(scanner.Name, (float)i / total);

                try
                {
                    var produced = (scanner.Scan(context) ?? Enumerable.Empty<Finding>()).ToList();
                    findings.AddRange(produced);
                    // Record the set of RuleIds produced by this scanner so that post-fix group incremental re-scans can look up ownership.
                    ruleMap[scanner.Name] = produced.Select(f => f.RuleId).Distinct().ToList();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[PerfLint] " + L.Tr($"Scanner '{scanner.Name}' failed: {ex}", $"Scanner '{scanner.Name}' 执行出错: {ex}"));
                }
            }

            sw.Stop();

            // Centralized filtering: discard findings whose path matches an ignored path (third-party directories, etc.) —
            // applied in one place so every rule benefits automatically.
            // Sorting: severity descending first, then by domain and rule ID, to guarantee a stable, reproducible report.
            var ordered = findings
                .Where(f => !IgnoreSettings.ShouldIgnore(f.TargetPath ?? f.CodeFile))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Domain)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ToList();

            return new ScanResult(ordered, sw.Elapsed, ruleMap);
        }

        /// <summary>
        /// Post-fix group incremental re-scan: given the RuleIds of the fixed findings, look up their owning scanners
        /// (using previous.ScannerRuleMap), re-run only those scanners, replace all findings they produced last time
        /// with the new results, and keep everything else unchanged. This avoids a full re-scan (which can take ~86s).
        ///
        /// Precondition: import-setting / config / Addressables-group fixes only affect **their own scanner's** rules
        /// and do not alter the verdicts of other scanners (e.g. changing texture compression does not affect duplicate
        /// asset detection). If previous has no ScannerRuleMap (produced by an older code path), fall back to a full Run.
        /// </summary>
        public static ScanResult RescanRules(
            IEnumerable<string> ruleIds, ScanResult previous,
            ScanContext context = null, IReadOnlyList<IScanner> scanners = null)
        {
            if (previous == null) return null;

            var wanted = new HashSet<string>(
                (ruleIds ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)));
            if (wanted.Count == 0) return previous;

            // No ownership map (e.g. previously went through the old RescanFile code path, or lost during deserialization) → fall back safely to a full re-scan.
            var map = previous.ScannerRuleMap;
            if (map == null) return Run(context, scanners);

            context ??= new ScanContext();
            scanners ??= DiscoverScanners();

            // Reverse-lookup: which scanners produced at least one rule in `wanted` during the previous scan.
            var ownerNames = new HashSet<string>(
                map.Where(kv => kv.Value != null && kv.Value.Any(wanted.Contains)).Select(kv => kv.Key));
            if (ownerNames.Count == 0) return previous; // no owners found (rule produced 0 findings last time) → nothing to change

            var owners = scanners.Where(s => ownerNames.Contains(s.Name)).ToList();

            // Re-run the owning scanners and collect fresh findings along with each scanner's new RuleId set.
            var fresh = new List<Finding>();
            var freshRuleSets = new Dictionary<string, IReadOnlyList<string>>();
            for (int i = 0; i < owners.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var scanner = owners[i];
                context.ReportProgress(scanner.Name, (float)i / Math.Max(1, owners.Count));
                try
                {
                    var produced = (scanner.Scan(context) ?? Enumerable.Empty<Finding>()).ToList();
                    fresh.AddRange(produced);
                    freshRuleSets[scanner.Name] = produced.Select(f => f.RuleId).Distinct().ToList();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[PerfLint] " + L.Tr($"Group re-scan '{scanner.Name}' failed: {ex}", $"按组重扫 '{scanner.Name}' 出错: {ex}"));
                }
            }

            // Old findings to remove from previous: the union of all RuleIds produced by the owning scanners last time ∪ this time (covers rules that appeared or disappeared).
            var removeRules = new HashSet<string>();
            foreach (var name in ownerNames)
            {
                if (map.TryGetValue(name, out var old) && old != null)
                    foreach (var r in old) removeRules.Add(r);
                if (freshRuleSets.TryGetValue(name, out var now) && now != null)
                    foreach (var r in now) removeRules.Add(r);
            }

            var merged = previous.Findings.Where(f => !removeRules.Contains(f.RuleId))
                .Concat(fresh)
                .Where(f => !IgnoreSettings.ShouldIgnore(f.TargetPath ?? f.CodeFile))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Domain)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ToList();

            // Carry over the old ownership map and update in-place the entries for the re-run scanners
            // (their rule sets may have shrunk due to the fix), so that subsequent incremental re-scans remain accurate.
            var newMap = new Dictionary<string, IReadOnlyList<string>>(map);
            foreach (var kv in freshRuleSets) newMap[kv.Key] = kv.Value;

            return new ScanResult(merged, previous.Duration, newMap);
        }

        /// <summary>
        /// Incremental single-file re-scan: recomputes findings for the given file using all IFileScanner implementations,
        /// replaces the old findings for that file in previous, and leaves everything else (including all asset-level findings) unchanged.
        /// Used to quickly refresh after an AI Fix is applied, avoiding a full re-scan (~86s).
        /// Returns null when previous is null (the caller should fall back to a full Scan).
        /// </summary>
        public static ScanResult RescanFile(string assetPath, ScanResult previous, IReadOnlyList<IScanner> scanners = null)
        {
            if (previous == null || string.IsNullOrEmpty(assetPath)) return previous;
            scanners ??= DiscoverScanners();
            var ctx = new ScanContext();

            // Fresh findings for this file.
            bool handled = false;
            var fresh = new List<Finding>();
            foreach (var s in scanners)
            {
                if (!(s is IFileScanner fs) || !fs.Handles(assetPath)) continue;
                handled = true;
                try { fresh.AddRange(fs.ScanFile(assetPath, ctx) ?? Enumerable.Empty<Finding>()); }
                catch (Exception ex) { UnityEngine.Debug.LogError("[PerfLint] " + L.Tr($"Incremental re-scan '{s.Name}' failed: {ex}", $"增量重扫 '{s.Name}' 出错: {ex}")); }
            }

            // No file-level scanner claimed this file → leave previous untouched (avoid accidentally removing its existing findings).
            if (!handled) return previous;

            // A finding belongs to this file if: CodeFile == path, OR TargetPath equals path (bare file path, e.g. old-style LOG001),
            // OR TargetPath starts with "path:" (script rules use "path:line" format, e.g. GC004 / current LOG001).
            // All three conditions are necessary: matching only the "path:" prefix would miss bare-path findings —
            // stale entries from old rule versions or rules that carry no line number would never be removed,
            // causing each incremental re-scan to keep the old finding and append a new one on top
            // (observed in practice: suppressing one Debug.Log actually increased LOG001's count by one).
            string norm = Norm(assetPath);
            bool BelongsToFile(Finding f)
            {
                if (Norm(f.CodeFile) == norm) return true;
                if (f.TargetPath == null) return false;
                string tp = Norm(f.TargetPath);
                return tp == norm || tp.StartsWith(norm + ":", StringComparison.Ordinal);
            }

            var merged = previous.Findings.Where(f => !BelongsToFile(f))
                .Concat(fresh)
                .Where(f => !IgnoreSettings.ShouldIgnore(f.TargetPath ?? f.CodeFile))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Domain)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ToList();

            // Carry over the ownership map: a file-level incremental re-scan does not alter the scanner→rule mapping,
            // so we preserve it to keep subsequent group re-scans functional.
            return new ScanResult(merged, previous.Duration, previous.ScannerRuleMap);
        }

        /// <summary>
        /// Scans a single file and produces a standalone result (with no dependency on any prior scan):
        /// runs all IFileScanner implementations that claim the file.
        /// Used by the runtime panel's "per-line analysis" feature to return results instantly when no full scan has
        /// been performed yet, without requiring a full project scan first.
        /// Returns null if no file-level scanner claims the file (e.g. non-script assets).
        /// The result carries no ScannerRuleMap — a single-file scan is insufficient as an ownership basis
        /// for project-level group re-scans, so it is left null to let subsequent RescanRules calls fall back safely to a full scan.
        /// </summary>
        public static ScanResult ScanFileOnly(string assetPath, ScanContext context = null, IReadOnlyList<IScanner> scanners = null)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            context ??= new ScanContext();
            scanners ??= DiscoverScanners();

            var sw = Stopwatch.StartNew();
            bool handled = false;
            var fresh = new List<Finding>();
            foreach (var s in scanners)
            {
                if (!(s is IFileScanner fs) || !fs.Handles(assetPath)) continue;
                handled = true;
                context.ReportProgress(s.Name, 0f);
                try { fresh.AddRange(fs.ScanFile(assetPath, context) ?? Enumerable.Empty<Finding>()); }
                catch (Exception ex) { UnityEngine.Debug.LogError("[PerfLint] " + L.Tr($"Single-file analysis '{s.Name}' failed: {ex}", $"单文件分析 '{s.Name}' 出错: {ex}")); }
            }
            sw.Stop();

            if (!handled) return null; // no file-level scanner claimed the file (non-script assets, etc.)

            var ordered = fresh
                .Where(f => !IgnoreSettings.ShouldIgnore(f.TargetPath ?? f.CodeFile))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Domain)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ToList();

            return new ScanResult(ordered, sw.Elapsed);
        }

        private static string Norm(string p) => string.IsNullOrEmpty(p) ? "" : p.Replace('\\', '/');
    }
}
