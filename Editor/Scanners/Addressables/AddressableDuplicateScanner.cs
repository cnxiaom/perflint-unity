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
    /// Assets domain (bundle size optimization), second pass: Addressables implicit-dependency duplicate packing detection.
    ///   ASSET.AADUP000 — Summary: how many shared assets are packed once into each of multiple groups.
    ///   ASSET.AADUP001 — Per-asset: an asset is not marked Addressable but is implicitly referenced by multiple groups, each producing its own copy.
    ///
    /// **Data source: reuses the official Addressables Analyze rule "Check Duplicate Bundle Dependencies"**
    /// (`CheckBundleDupeDependencies`). It runs a real build-dependency simulation and **authoritatively identifies
    /// "which assets are duplicated across bundles"** — including SpriteAtlas reverse-inclusion, object-level sharing,
    /// and other cases that a `GetDependencies` approximation cannot catch (confirmed: two UI Prefabs sharing one UI
    /// texture — the approximation misses it, the official rule reports it). The asset list is consistent with the
    /// official Analyze output.
    ///
    /// **Size metrics (sort vs display)**: `RefreshAnalysis` yields asset names only, not byte counts, so two metrics are
    /// derived per asset:
    ///   · **In-memory estimate** (`ScannerUtil.StorageMemoryBytes`) — textures use `TextureUtil.GetStorageMemorySizeLong`
    ///     (= the Inspector value, the platform's real compressed format + mips); other types fall back to runtime memory.
    ///     **This is the sort key** (most memory-impactful duplicates first); falls back to file size when unavailable.
    ///   · **Source file size** (`ScannerUtil.FileSizeBytes`) — the asset's raw on-disk bytes, **directly verifiable** in
    ///     Explorer / Project view. Always shown alongside the memory estimate.
    /// Both are displayed (memory first as the sort basis, source file as the verifiable anchor). Earlier the list sorted
    /// by source file size only; "width × height × 4 uncompressed" (overestimates compressed textures ~8×) was abandoned,
    /// and the memory figure is labeled as an approximate Inspector-equivalent — never presented as exact "wasted MB".
    ///
    /// **Duplicate count is based on group count** (not bundle count): one group can be built into multiple bundles,
    /// so the copy count may be lower than the "Number of times duplicated" shown in the official Build Report —
    /// this is noted in the copy.
    ///
    /// Only compiled / produces results when Addressables is installed (`PERFLINT_ADDRESSABLES`) and settings have
    /// been initialized (zero noise). Report-only; does not auto-fix.
    /// Cost: `RefreshAnalysis` triggers a real build-dependency computation — slower than GetDependencies
    /// (seconds to tens of seconds on large projects) and cannot be cancelled mid-way.
    /// </summary>
    public sealed class AddressableDuplicateScanner : IScanner
    {
        public string Name => "Addressables Duplicates";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) yield break;            // Addressables not initialized

            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(Name, 0.05f);

            // 1) Run the official Analyze rule and aggregate (group, assetPath) pairs into assetPath → set of referencing groups.
            //    RefreshAnalysis is a one-shot blocking call; yield is not allowed inside an iterator catch block,
            //    so we set a failed flag and handle it outside the try.
            var assetToGroups = new Dictionary<string, HashSet<string>>();
            bool failed = false;
            var rule = new CheckBundleDupeDependencies();
            try
            {
                var results = rule.RefreshAnalysis(settings);
                context.ReportProgress(Name, 0.85f);
                if (results != null)
                {
                    foreach (var r in results)
                    {
                        if (r == null) continue;
                        if (!TryParseResult(r.resultName, out string group, out string assetPath)) continue;
                        if (!assetToGroups.TryGetValue(assetPath, out var set))
                        {
                            set = new HashSet<string>();
                            assetToGroups[assetPath] = set;
                        }
                        set.Add(group);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr($"Addressables duplicate analysis failed (rule skipped): {e}", $"Addressables 重复分析失败（已跳过该规则）：{e}"));
                failed = true;
            }
            finally
            {
                try { rule.ClearAnalysis(); } catch { /* cleanup failure does not affect results */ }
            }

            if (failed) yield break;

            // 2) Referenced by ≥2 groups = cross-group duplicate. **Sort by estimated in-memory size descending**
            //    (textures ≈ the Inspector storage value, accounting for the platform's real compressed format) so the
            //    most memory-impactful duplicates surface first. We still *display* the verifiable source file size too
            //    (see below): memory is a derived estimate, source file is the on-disk bytes the user can check directly.
            //    When the memory estimate is unavailable (Mem == 0), the source file size is used as the sort fallback.
            //    **Do not say "wasted MB"**: actual redundancy is runtime memory / built bundle space; neither metric × copies
            //    equals that exactly, so we report both sizes + duplicate group count and avoid an absolute "wasted" claim.
            var dups = new List<DupEntry>();
            foreach (var kv in assetToGroups)
            {
                if (kv.Value.Count < 2) continue;
                dups.Add(new DupEntry
                {
                    Path = kv.Key,
                    Groups = kv.Value.OrderBy(g => g, StringComparer.Ordinal).ToList(),
                    Size = ScannerUtil.FileSizeBytes(kv.Key),
                    Mem = ScannerUtil.StorageMemoryBytes(kv.Key)
                });
            }
            if (dups.Count == 0) yield break;

            dups.Sort((a, b) =>
            {
                long am = a.Mem > 0 ? a.Mem : a.Size;   // fall back to file size when memory is unknown
                long bm = b.Mem > 0 ? b.Mem : b.Size;
                int byMem = bm.CompareTo(am);
                return byMem != 0 ? byMem : string.CompareOrdinal(a.Path, b.Path);
            });

            // No longer emitting a summary finding (ASSET.AADUP000): once "wasted MB" is removed, it only contains
            // a count (the rule group header already shows (N)) plus a top-list that duplicates the per-asset findings —
            // purely redundant. Per-asset AADUP001 findings plus the group-header "Extract all to shared group" action are sufficient.
            foreach (var d in dups)
            {
                string sizeHuman = d.Size > 0 ? ScannerUtil.Human(d.Size) : L.Tr("unknown", "未知");
                string memHuman = d.Mem > 0 ? ScannerUtil.Human(d.Mem) : null;
                // Dual size phrase: in-memory estimate (the sort basis; textures ≈ Inspector value) + verifiable source
                // file size. When the memory estimate is unavailable, fall back to source-file-only (the old wording).
                string sizePhrase = memHuman != null
                    ? L.Tr($"~{memHuman} in memory, source file ~{sizeHuman}", $"内存约 {memHuman}、源文件约 {sizeHuman}")
                    : L.Tr($"source file ~{sizeHuman}", $"源文件约 {sizeHuman}");
                int copies = d.Groups.Count;
                string path = d.Path;
                yield return new Finding(
                    ruleId: "ASSET.AADUP001",
                    domain: Domain.Assets,
                    severity: Severity.Warning,
                    // Title shows both sizes for one copy (in-memory estimate ≈ Inspector + verifiable source file) + duplicate group count.
                    // Do not say "wasted" — actual redundancy is runtime memory / built bundle space and is hard to estimate exactly (maintainer requirement).
                    title: L.Tr($"Asset ({sizePhrase}) duplicated across {copies} groups", $"资源（{sizePhrase}）被 {copies} 个 group 重复打包"),
                    // Group header stays generic (no per-instance size/count), matching every other rule group.
                    groupTitle: L.Tr("Asset duplicated across Addressable groups", "资源被多个 Addressable group 重复打包"),
                    detail: L.Tr($"{d.Path} ({sizePhrase}) is not marked Addressable; it's an implicit dependency packed once into each of the following {copies} groups:", $"{d.Path}（{sizePhrase}）未标记为 Addressable、是隐式依赖，被以下 {copies} 个 group 各打包一份：") +
                            $"\n  {string.Join("\n  ", d.Groups)}" +
                            L.Tr("\n(After dedup, only one copy is kept. Sizes are approximate — the in-memory figure ≈ the Inspector value; the source file is the verifiable on-disk size. We don't claim an exact \"wasted MB\".)", "\n（去重后只保留一份。大小为约值——内存约等于 Inspector 显示值；源文件是可核对的磁盘大小。不写精确「浪费 MB」。）") +
                            L.Tr("\nNote: one group can be built into multiple bundles, so the actual number of duplicated bundles may exceed the group count.", "\n注意：一个 group 可能打成多个 bundle，实际重复 bundle 数可能多于 group 数。") +
                            L.Tr("\nRecommend marking it Addressable (in a shared group) so the other groups just reference it.", "\n建议把它设为 Addressable（公共 group），其余 group 引用即可。"),
                    targetPath: d.Path,
                    ping: () => ScannerUtil.PingAsset(d.Path),
                    action: new FindingAction(
                        label: L.Tr("Extract to shared group", "提取到公共 group"),
                        confirmMessage:
                            L.Tr($"Mark the following asset as Addressable and move it into the \"{AddressableSharedGroup.SharedGroupName}\" group to eliminate cross-group duplicate packing:\n{path}\n\n", $"把以下资源设为 Addressable、移入「{AddressableSharedGroup.SharedGroupName}」group，消除跨 group 重复打包：\n{path}\n\n") +
                            L.Tr("Only adds the Addressable mark, does not modify any references—low risk.\n", "仅添加 Addressable 标记、不修改任何引用，低风险。\n") +
                            PerfLintWarnings.Irreversible +
                            L.Tr(" To roll back, use Tools > PerfLint > Revert \"PerfLint Shared\" Extraction.", " 回退请用 Tools > PerfLint > Revert「PerfLint Shared」Extraction 菜单。"),
                        run: () => AddressableSharedGroup.Extract(path)));
            }
        }

        /// <summary>
        /// Parses the official `CheckBundleDupeDependencies` `AnalyzeResult.resultName`.
        /// Known format (Addressables 1.16–1.21):
        ///   "Check Duplicate Bundle Dependencies:&lt;GroupName&gt;:&lt;FileName&gt;:&lt;AssetPath&gt;"
        /// Segmented by ':'; ruleName (segment 0) contains no ':', group is segment 1, last is the full asset path.
        /// Defensive: scan from the right for the first segment starting with Assets/ or Packages/ as the asset path
        /// (tolerates minor format variations / paths containing ':'); if group or asset path cannot be extracted,
        /// treat as a summary / no-issue line and skip.
        /// </summary>
        private static bool TryParseResult(string resultName, out string group, out string assetPath)
        {
            group = null;
            assetPath = null;
            if (string.IsNullOrEmpty(resultName)) return false;

            var parts = resultName.Split(':');
            if (parts.Length < 3) return false; // need at least ruleName:group:...:assetPath

            for (int i = parts.Length - 1; i >= 1; i--)
            {
                if (parts[i].StartsWith("Assets/") || parts[i].StartsWith("Packages/"))
                {
                    assetPath = string.Join(":", parts.Skip(i)); // 万一路径含 ':'
                    group = parts[1];
                    return !string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(assetPath);
                }
            }
            return false;
        }

        private sealed class DupEntry
        {
            public string Path;
            public List<string> Groups;
            public long Size;  // raw on-disk source file bytes (verifiable in Explorer / Project view)
            public long Mem;   // estimated in-memory size (textures ≈ Inspector value); 0 if unavailable
        }
    }
}
#endif
