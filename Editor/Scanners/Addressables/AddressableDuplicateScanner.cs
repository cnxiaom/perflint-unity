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
    /// **Duplicate count is based on distinct BUNDLE count** (group + bundle-file from the analyze rows). It used to
    /// count groups, which silently dropped same-group cross-bundle duplication — a per-scene-bundle group baking one
    /// font copy into each of 74 scene bundles has group-count 1 (real museum case: 1.16GB potential saving missed).
    /// Bundle count aligns with the "Number of times duplicated" in the official Build Report.
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

            // 1) Run the official Analyze rule and aggregate per asset: the set of referencing GROUPS (for display) and
            //    the set of distinct BUNDLES (group+file — the duplication unit). RefreshAnalysis is a one-shot blocking
            //    call; yield is not allowed inside an iterator catch block, so we set a failed flag and handle it outside.
            var assetToGroups = new Dictionary<string, HashSet<string>>();
            var assetToBundles = new Dictionary<string, HashSet<string>>();
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
                        if (!BundlePacking.TryParseDupeResult(r.resultName, out string group, out string bundleFile, out string assetPath)) continue;
                        if (!assetToGroups.TryGetValue(assetPath, out var gset))
                        {
                            gset = new HashSet<string>();
                            assetToGroups[assetPath] = gset;
                            assetToBundles[assetPath] = new HashSet<string>();
                        }
                        gset.Add(group);
                        // Bundle identity = group + file; fall back to the group when the row carries no file segment.
                        assetToBundles[assetPath].Add(bundleFile != null ? group + "/" + bundleFile : group);
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

            // 2) Packed into ≥2 distinct BUNDLES = duplicate. Counting bundles (not groups) is load-bearing: a
            //    per-scene-bundle group bakes one copy into EACH scene bundle, so an asset duplicated 74× can have
            //    group-count 1 — the old ≥2-groups filter silently dropped exactly those (a real 1.16GB font case).
            //    **Sort by estimated in-memory size descending** (textures ≈ the Inspector storage value) so the most
            //    memory-impactful duplicates surface first; source file size is displayed too as the verifiable anchor.
            //    **Do not say "wasted MB"**: actual redundancy is runtime memory / built bundle space; neither metric ×
            //    copies equals that exactly, so we report sizes + duplicate bundle count and avoid an absolute claim.
            var dups = new List<DupEntry>();
            foreach (var kv in assetToBundles)
            {
                if (kv.Value.Count < 2) continue;
                dups.Add(new DupEntry
                {
                    Path = kv.Key,
                    Groups = assetToGroups[kv.Key].OrderBy(g => g, StringComparer.Ordinal).ToList(),
                    BundleCount = kv.Value.Count,
                    Size = ScannerUtil.FileSizeBytes(kv.Key),
                    Mem = ScannerUtil.StorageMemoryBytes(kv.Key)
                });
            }
            if (dups.Count == 0) yield break;

            dups.Sort((a, b) =>
            {
                // Real waste ranking: max(mem, size) × bundle copies. Max() guards composite assets whose main
                // object measures near-zero in memory (TMP FontAsset: ~496 B mem vs 65.8 MB file — mem-first sorting
                // buried it); × copies ranks "big asset in many bundles" first.
                long am = Math.Max(a.Mem, a.Size) * a.BundleCount;
                long bm = Math.Max(b.Mem, b.Size) * b.BundleCount;
                int byWaste = bm.CompareTo(am);
                return byWaste != 0 ? byWaste : string.CompareOrdinal(a.Path, b.Path);
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
                int bundles = d.BundleCount;
                int groupCount = d.Groups.Count;
                string path = d.Path;
                // Same-group cross-bundle duplication (e.g. one-bundle-per-scene groups) and Resources-folder assets
                // each get a conditional explanation — stated only when actually detected (cross-reference gating).
                string sameGroupNote = groupCount == 1
                    ? L.Tr($"\nAll copies are inside ONE group ('{d.Groups[0]}') that builds multiple bundles (e.g. per-scene bundles) — each bundle bakes its own copy. Extraction still fixes this: once Addressable, those bundles reference the shared copy.",
                           $"\n所有拷贝都在同一个 group（「{d.Groups[0]}」）内——该 group 打出多个 bundle（如一场景一包），每个 bundle 各烘焙一份。提取同样有效：设为 Addressable 后这些 bundle 会改为引用共享副本。")
                    : "";
                string resourcesNote = path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0
                    ? L.Tr("\nNote: this asset lives in a Resources folder — the player ALWAYS ships the Resources copy as well, and code using Resources.Load keeps reading that copy. Extraction still collapses the bundle copies into one, but the Resources copy remains (typical for TMP font assets); moving the asset out of Resources is the complete fix.",
                           "\n注意：该资源位于 Resources 目录——玩家包必然额外带一份 Resources 副本，且 Resources.Load 的代码仍走那份。提取仍可把多份 bundle 拷贝并成一份，但 Resources 副本会保留（TMP 字体资产是典型）；彻底修复需把资源移出 Resources。")
                    : "";
                yield return new Finding(
                    ruleId: "ASSET.AADUP001",
                    domain: Domain.Assets,
                    severity: Severity.Warning,
                    // Title shows both sizes for one copy (in-memory estimate ≈ Inspector + verifiable source file) + duplicate BUNDLE count.
                    // Do not say "wasted" — actual redundancy is runtime memory / built bundle space and is hard to estimate exactly (maintainer requirement).
                    title: L.Tr($"Asset ({sizePhrase}) duplicated across {bundles} bundles", $"资源（{sizePhrase}）被 {bundles} 个 bundle 重复打包"),
                    // Group header stays generic (no per-instance size/count), matching every other rule group.
                    groupTitle: L.Tr("Asset duplicated across Addressable bundles", "资源被多个 Addressable bundle 重复打包"),
                    detail: L.Tr($"{d.Path} ({sizePhrase}) is not marked Addressable; it's an implicit dependency packed once into each of {bundles} bundles, from the following {groupCount} group(s):", $"{d.Path}（{sizePhrase}）未标记为 Addressable、是隐式依赖，被 {bundles} 个 bundle 各打包一份，来自以下 {groupCount} 个 group：") +
                            $"\n  {string.Join("\n  ", d.Groups)}" +
                            sameGroupNote +
                            resourcesNote +
                            L.Tr("\n(After dedup, only one copy is kept. Sizes are approximate — the in-memory figure ≈ the Inspector value; the source file is the verifiable on-disk size. We don't claim an exact \"wasted MB\".)", "\n（去重后只保留一份。大小为约值——内存约等于 Inspector 显示值；源文件是可核对的磁盘大小。不写精确「浪费 MB」。）") +
                            L.Tr("\nRecommend marking it Addressable (in a shared group) so the referencing bundles just reference it.", "\n建议把它设为 Addressable（公共 group），引用它的 bundle 改为引用即可。"),
                    targetPath: d.Path,
                    // Duplication findings bypass the ignore-path filter: ignoring "Dependencies/" etc. is meant to
                    // silence import-settings advice on third-party assets, but third-party duplication (TMP fonts,
                    // AVProVideo demo mats) bloats the USER's build and the fix never edits the third-party asset.
                    ignoreExempt: true,
                    ping: () => ScannerUtil.PingAsset(d.Path),
                    action: new FindingAction(
                        label: L.Tr("Extract to shared group", "提取到公共 group"),
                        confirmMessage:
                            L.Tr($"Mark the following asset as Addressable and move it into the \"{AddressableSharedGroup.SharedGroupName}\" group to eliminate cross-group duplicate packing:\n{path}\n\n", $"把以下资源设为 Addressable、移入「{AddressableSharedGroup.SharedGroupName}」group，消除跨 group 重复打包：\n{path}\n\n") +
                            L.Tr("Only adds the Addressable mark, does not modify any references—low risk.\n", "仅添加 Addressable 标记、不修改任何引用，低风险。\n") +
                            PerfLintWarnings.Irreversible +
                            L.Tr(" To roll back, use Tools > PerfLint > Revert \"PerfLint Shared\" Extraction.", " 回退请用 Tools > PerfLint > Revert「PerfLint Shared」Extraction 菜单。"),
                        run: () => AddressableSharedGroup.Extract(path),
                        // Rule-level "Extract all" hands the whole path list here: one SaveAssets instead of N, plus a
                        // before/after official-duplicate count so the user sees whether it actually deduplicated.
                        batchRun: paths => AddressableSharedGroup.ExtractMany(paths),
                        // Kept deliberately terse: Unity's DisplayDialog truncates around ~500 characters (mid-sentence,
                        // with an unhelpful "see the editor log file" hint), so the WHOLE body incl. the "Will run …"
                        // prefix must stay under that. Verified against a real truncation in smoke testing — twice.
                        batchConfirmMessage:
                            L.Tr($"Marks each asset as Addressable in \"{AddressableSharedGroup.SharedGroupName}\", so bundles share one copy instead of baking their own. No references are modified; read-only package assets are skipped. The result dialog shows the duplicate count before vs. after.\n" +
                                 "Not undoable via Edit > Undo — commit to version control first. Roll back: Tools > PerfLint > Revert \"PerfLint Shared\" Extraction.",
                                 $"把每个资源设为 Addressable 并入「{AddressableSharedGroup.SharedGroupName}」——bundle 将共享一份、不再各自烘焙。不修改任何引用；只读包资源自动跳过。结果框会显示重复数的前后对比。\n" +
                                 "无法用 Edit > Undo 撤销——请先提交版本控制。回退：Tools > PerfLint > Revert「PerfLint Shared」Extraction。")),
                    // Estimate only, kept OUT of the finding text (the "no exact wasted-MB claim" wording above
                    // stands). Source bytes × extra bundle copies feeds the panel's aggregate "up to ~X (est.)" line.
                    estimatedBuildSavingsBytes: d.Size > 0 ? d.Size * (d.BundleCount - 1) : 0);
            }
        }

        // resultName parsing lives in BundlePacking.TryParseDupeResult (main assembly — pure string logic, unit-tested
        // in batchmode where this module can't be compiled).

        private sealed class DupEntry
        {
            public string Path;
            public List<string> Groups;
            public int BundleCount;   // distinct bundles containing a copy — the duplication unit (≥ group count)
            public long Size;         // raw on-disk source file bytes (verifiable in Explorer / Project view)
            public long Mem;          // estimated in-memory size (textures ≈ Inspector value); 0 if unavailable
        }
    }
}
#endif
