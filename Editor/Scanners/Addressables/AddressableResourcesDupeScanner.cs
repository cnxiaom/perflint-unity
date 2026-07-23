#if PERFLINT_ADDRESSABLES
using System;
using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEngine;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Assets domain: Resources ↔ Addressables duplication.
    ///   ASSET.AARES001 — An asset living in a Resources folder is ALSO baked into Addressables content. Resources
    ///     folders always ship whole in the player, and code using Resources.Load reads that copy — so every
    ///     addressable bundle that implicitly depends on the asset carries an EXTRA copy on top of the guaranteed
    ///     Resources one. This is where the biggest real-world offenders hide: TMP font atlases under
    ///     "TextMesh Pro/Resources" referenced by every scene (a real project measured one font duplicated 74× ≈ 1.16GB
    ///     of build size).
    ///
    /// **Why AADUP001 cannot catch these**: it wraps the official "Check Duplicate Bundle Dependencies", which filters
    /// out assets that are not valid Addressable candidates — everything under Resources/ is excluded, so these rows
    /// never appear there (they ARE visible in the official Build Report's Duplicated Assets view, which reads the real
    /// build layout). This scanner wraps the official "Check Resources to Addressable Duplicate Dependencies"
    /// (CheckResourcesDupeDependencies) instead, which exists precisely for this case.
    ///
    /// Report-only, deliberately: the fix is a refactor decision — move the asset out of Resources and make it
    /// Addressable (updating Resources.Load call sites), or for TMP fonts route them via TMP settings / direct
    /// references. Marking-addressable-in-place cannot help (the Resources copy still ships), so no one-click action.
    /// Row format of this rule isn't pinned across versions → tolerant parsing (BundlePacking.TryExtractAssetPath,
    /// unit-tested in the main assembly). Only compiled with PERFLINT_ADDRESSABLES; silent when settings are absent.
    /// Cost: one extra RefreshAnalysis (a real dependency simulation, seconds to tens of seconds on large projects).
    /// </summary>
    public sealed class AddressableResourcesDupeScanner : IScanner
    {
        public string Name => "Addressables vs Resources";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) yield break; // Addressables not initialized

            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(Name, 0.05f);

            // Aggregate asset path → number of duplicate rows (≈ how many bundles carry an extra copy).
            var rowsPerAsset = new Dictionary<string, int>();
            bool failed = false;
            var rule = new CheckResourcesDupeDependencies();
            try
            {
                var results = rule.RefreshAnalysis(settings);
                context.ReportProgress(Name, 0.85f);
                if (results != null)
                {
                    foreach (var r in results)
                    {
                        if (r == null) continue;
                        if (!BundlePacking.TryExtractAssetPath(r.resultName, out string assetPath)) continue;
                        // Only Resources-resident assets are this rule's subject; be defensive about row shapes.
                        if (assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        rowsPerAsset.TryGetValue(assetPath, out int n);
                        rowsPerAsset[assetPath] = n + 1;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr($"Resources↔Addressables duplicate analysis failed (rule skipped): {e}", $"Resources↔Addressables 重复分析失败（已跳过该规则）：{e}"));
                failed = true;
            }
            finally
            {
                try { rule.ClearAnalysis(); } catch { /* cleanup best-effort */ }
            }

            if (failed || rowsPerAsset.Count == 0) yield break;

            // Sort by REAL waste: max(in-memory estimate, source file size) × duplicate rows. Max() matters for
            // composite assets — a TMP FontAsset's main object measures ~496 B in memory while its source file is
            // 65.8 MB (the atlas lives in sub-assets), and mem-first sorting buried the single biggest offender at
            // the bottom of the list. Weighting by rows ranks "65MB × 74 copies" above "11MB × 57".
            var entries = rowsPerAsset
                .Select(kv => new
                {
                    Path = kv.Key,
                    Rows = kv.Value,
                    Size = ScannerUtil.FileSizeBytes(kv.Key),
                    Mem = ScannerUtil.StorageMemoryBytes(kv.Key)
                })
                .OrderByDescending(e => Math.Max(e.Mem, e.Size) * e.Rows)
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .ToList();

            foreach (var d in entries)
            {
                string sizeHuman = d.Size > 0 ? ScannerUtil.Human(d.Size) : L.Tr("unknown", "未知");
                string memHuman = d.Mem > 0 ? ScannerUtil.Human(d.Mem) : null;
                string sizePhrase = memHuman != null
                    ? L.Tr($"~{memHuman} in memory, source file ~{sizeHuman}", $"内存约 {memHuman}、源文件约 {sizeHuman}")
                    : L.Tr($"source file ~{sizeHuman}", $"源文件约 {sizeHuman}");
                string path = d.Path;
                bool looksLikeTmp = path.IndexOf("TextMesh Pro", StringComparison.OrdinalIgnoreCase) >= 0;
                yield return new Finding(
                    ruleId: "ASSET.AARES001",
                    domain: Domain.Assets,
                    severity: Severity.Warning,
                    title: L.Tr($"Resources asset ({sizePhrase}) is also baked into Addressables content ×{d.Rows}", $"Resources 资源（{sizePhrase}）同时被烘焙进 Addressables 内容 ×{d.Rows}"),
                    groupTitle: L.Tr("Resources assets duplicated into Addressables content", "Resources 资源被重复烘焙进 Addressables 内容"),
                    detail: L.Tr($"{path} ({sizePhrase}) lives in a Resources folder — the player ALWAYS ships that copy — and Addressables content implicitly depends on it, baking additional copies ({d.Rows} duplicate row(s) in the official Analyze). " +
                                 "These are the duplicates the Build Report shows that AADUP001 cannot list (assets under Resources/ are not valid Addressable candidates, so the bundle-dupe rule skips them).",
                                 $"{path}（{sizePhrase}）位于 Resources 目录——玩家包必然带这一份——而 Addressables 内容又隐式依赖它，额外烘焙了拷贝（官方 Analyze 报 {d.Rows} 行重复）。" +
                                 "这正是 Build Report 里能看到、但 AADUP001 列不出的那类重复（Resources/ 下的资产不是合法 Addressable 候选，bundle 重复规则会跳过它们）。") +
                            L.Tr("\nFix (a refactor decision, so no one-click here): move the asset OUT of Resources and make it Addressable, updating Resources.Load call sites to Addressables loading. Marking it Addressable in place cannot help — the Resources copy still ships.",
                                 "\n修法（属重构决策，故不提供一键）：把资源移出 Resources 并设为 Addressable，把 Resources.Load 调用点改为 Addressables 加载。原地标记 Addressable 无效——Resources 那份照样进包。") +
                            (looksLikeTmp
                                ? L.Tr("\nTMP note: TextMesh Pro looks up default fonts via Resources. Prefer referencing font assets directly on components / via TMP Settings, and consider moving shared font atlases out of the TMP Resources folder per TMP's Addressables guidance.",
                                       "\nTMP 提示：TextMesh Pro 默认经 Resources 查找字体。优先在组件/TMP Settings 里直接引用字体资产，并按 TMP 的 Addressables 指引把共享字体图集移出其 Resources 目录。")
                                : ""),
                    targetPath: d.Path,
                    // Bypass the ignore-path filter — the real offenders live in ignored third-party folders
                    // (TMP fonts under Dependencies/), and the fix never edits the third-party asset itself.
                    ignoreExempt: true,
                    ping: () => ScannerUtil.PingAsset(path),
                    // The Resources copy always ships, so the baked Addressables copies are the removable part:
                    // source bytes × duplicate rows. Estimate only — feeds the aggregate "up to ~X (est.)" line.
                    estimatedBuildSavingsBytes: d.Size > 0 ? d.Size * d.Rows : 0);
            }
        }
    }
}
#endif
