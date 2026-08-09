using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 asset domain: duplicate asset detection.
    ///   ASSET.DUP001 — multiple asset files are byte-for-byte identical, wasting build size.
    /// Optimization: group by file size first; only compute hashes for candidates within the same size group,
    /// avoiding a full-project hash pass.
    /// Offers a Pro "real dedup" <see cref="FindingAction"/> (keep one copy, redirect references, delete the rest —
    /// see <see cref="DuplicateAssetMerger"/>); it is an Action (config-changing, not Edit&gt;Undo-able, excluded from
    /// Fix All), never an auto-Fix, because deletion is destructive and the survivor choice is the user's.
    /// </summary>
    public sealed class DuplicateAssetScanner : IScanner
    {
        public string Name => "Duplicate Assets";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var paths = new List<string>();
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(p)) continue;
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".cs" || ext == ".asmdef" || ext == ".asmref" || ext == ".meta") continue;
                paths.Add(p);
            }

            // 1) Group by file size — files with a unique size cannot possibly duplicate another, so skip hashing them.
            var bySize = new Dictionary<long, List<string>>();
            for (int i = 0; i < paths.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, 0.5f * i / paths.Count);
                long size = ScannerUtil.FileSizeBytes(paths[i]);
                if (size <= 0) continue;
                if (!bySize.TryGetValue(size, out var list)) { list = new List<string>(); bySize[size] = list; }
                list.Add(paths[i]);
            }

            // 2) Hash only files within same-size groups (a unique-size file can't duplicate anything). Reading each whole
            //    file to MD5 it is the scanner's dominant cost on large projects — and HashFile is pure file I/O + MD5 with
            //    no Unity API, so we hash the candidates in PARALLEL across cores. Determinism is preserved: MD5 is
            //    deterministic and the size:hash grouping is order-independent (findings are sorted downstream). We can't call
            //    ReportProgress from worker threads (it drives editor UI), so progress is reported coarsely around the block.
            var candidates = new List<KeyValuePair<long, string>>(); // (size, path)
            foreach (var kv in bySize)
                if (kv.Value.Count >= 2)
                    foreach (var p in kv.Value)
                        candidates.Add(new KeyValuePair<long, string>(kv.Key, p));

            context.ReportProgress(Name, 0.5f);
            var hashes = new string[candidates.Count];
            // ParallelOptions.CancellationToken makes Parallel.For throw a plain OperationCanceledException (not wrapped in
            // AggregateException) on cancel, so ScanRunner's cancel path still catches it.
            var parallelOpts = new System.Threading.Tasks.ParallelOptions { CancellationToken = context.CancellationToken };
            System.Threading.Tasks.Parallel.For(0, candidates.Count, parallelOpts,
                i => { hashes[i] = ScannerUtil.ContentHash(candidates[i].Value); });

            var byHash = new Dictionary<string, List<string>>();
            for (int i = 0; i < candidates.Count; i++)
            {
                string hash = hashes[i];
                if (hash == null) continue;
                string key = candidates[i].Key + ":" + hash;
                if (!byHash.TryGetValue(key, out var list)) { list = new List<string>(); byHash[key] = list; }
                list.Add(candidates[i].Value);
            }
            context.ReportProgress(Name, 1f);

            // 3) Emit one Finding per group of identical-content files.
            foreach (var kv in byHash)
            {
                var group = kv.Value;
                if (group.Count < 2) continue;
                group.Sort(StringComparer.Ordinal);

                string rep = group[0];
                var sb = new StringBuilder();
                sb.Append(L.Tr($"This file is byte-for-byte identical to {group.Count - 1} other file(s):", $"该文件与另 {group.Count - 1} 个文件内容完全相同："));
                int listCount = Math.Min(group.Count, 6);
                for (int i = 1; i < listCount; i++) sb.Append("\n  · ").Append(group[i]);
                if (group.Count > listCount) sb.Append(L.Tr($"\n  · … ({group.Count} total)", $"\n  · …（共 {group.Count} 个）"));
                sb.Append(L.Tr("\nMerge them into a single asset, update the references, and delete the extra copies to reduce build size.", "\n建议合并为单一资源并更新引用，删除多余副本以减小包体。"));

                int extraCopies = group.Count - 1;
                // Byte-identical copies grouped by (size, hash): the group's shared file size is the size-group key.
                // Dedup deletes the extras, so the on-disk saving is exact per copy.
                long groupFileSize = long.Parse(kv.Key.Substring(0, kv.Key.IndexOf(':')));

                // …but the BUILD saving isn't. A copy under an Editor folder never enters a player build, and this
                // figure feeds the panel's "you could save X" total and the ranking — so an editor-only duplicate
                // would otherwise buy its way to the top of a build-size list while saving nothing at all.
                int editorOnlyCopies = 0;
                foreach (var g in group) if (ScannerUtil.IsEditorOnlyPath(g)) editorOnlyCopies++;
                if (editorOnlyCopies > 0)
                    sb.Append(L.Tr($"\n{editorOnlyCopies} of these live in an editor-only folder and never enter a build — merging those reclaims repository space, not build size.",
                                   $"\n其中 {editorOnlyCopies} 个位于仅编辑器目录、不会进入构建——合并它们省的是仓库空间，不是包体。"));

                // What the merge can actually remove here, decided now rather than 86 seconds into a batch run.
                var plan = PlanMerge(group);
                if (plan.IsNoOp)
                    sb.Append(NoMergeReason(group));
                else if (plan.Deletable < extraCopies)
                    sb.Append(L.Tr($"\nOnly {plan.Deletable} of the {extraCopies} extra copies can be removed automatically; the rest are loaded by path / name / address and are kept.",
                                   $"\n另 {extraCopies} 份副本中只有 {plan.Deletable} 份可自动删除，其余按路径/名称/address 加载、会被保留。"));

                yield return new Finding(
                    ruleId: "ASSET.DUP001",
                    domain: Domain.Assets,
                    severity: Severity.Warning,
                    title: L.Tr($"Duplicate asset ({group.Count} identical copies)", $"重复资产（{group.Count} 份内容相同）"),
                    detail: sb.ToString(),
                    targetPath: rep,
                    ping: () => ScannerUtil.PingAsset(rep),
                    group: group,
                    // Pro "real dedup": keep one copy (the user picks in the chooser via runWithChoice; batch/default
                    // keeps a path-loaded copy when there is one, else the most-referenced), redirect every
                    // project-wide GUID reference to it, delete the rest.
                    // Destructive and not Edit>Undo-able — see DuplicateAssetMerger for the guards.
                    // No action at all when nothing is deletable: a button whose only possible outcome is "merged 0"
                    // is worse than no button, and the state is knowable here rather than after the run.
                    action: plan.IsNoOp ? null : new FindingAction(
                        label: L.Tr("Merge duplicates (redirect refs + delete copies)", "合并去重（重定向引用＋删副本）"),
                        confirmMessage:
                            L.Tr($"Merge {plan.Deletable} identical copy/copies, keeping the one the others redirect to.\n\n", $"合并另 {plan.Deletable} 份相同副本，保留其余副本重定向到的那一份。\n\n") +
                            L.Tr("Every reference across the project (scenes, prefabs, materials, .meta) is redirected to the kept copy, then the redundant files are deleted.\n", "全工程的引用（场景、预制体、材质、.meta）都会重定向到保留副本，随后删除多余文件。\n") +
                            L.Tr("Requires Asset Serialization = Force Text. ", "要求 Asset Serialization = Force Text。") + PerfLintWarnings.Irreversible,
                        run: () => DuplicateAssetMerger.Merge(group),
                        runWithChoice: keep => DuplicateAssetMerger.Merge(group, keep)),
                    estimatedBuildSavingsBytes: BuildSavingsBytes(group, groupFileSize));
            }
        }

        /// <summary>
        /// What a merge of this group can actually do, from the same two facts the merger itself decides on: a copy
        /// loaded by path / name / address can never be deleted (a GUID redirect cannot repair those loads), and a
        /// copy under an editor-only folder was never in the build to begin with.
        /// </summary>
        internal readonly struct MergePlan
        {
            /// <summary>Copies the merge can actually delete.</summary>
            public readonly int Deletable;
            /// <summary>Of those, the ones that would have entered a player build — the only bytes a build gets back.</summary>
            public readonly int ShippingDeletable;

            public MergePlan(int deletable, int shippingDeletable)
            {
                Deletable = deletable;
                ShippingDeletable = shippingDeletable;
            }

            /// <summary>Nothing is removable: offering the merge here is a button whose only outcome is "merged 0".</summary>
            public bool IsNoOp => Deletable <= 0;
        }

        /// <summary>
        /// Decides <see cref="MergePlan"/> at scan time. Pure apart from the two injected facts, which default to
        /// the AssetDatabase / merger so the promise and the execution cannot drift apart.
        ///
        /// Written after the two halves HAD drifted, twice over. On a real project this rule quoted ~221 MB of
        /// reclaimable build size across 32 groups and "merge all" deduplicated 0 of them after 86 seconds:
        ///   · 8 groups had BOTH copies under Resources/, so nothing in them was ever deletable — yet each still
        ///     contributed a whole copy's bytes and still got a button (the PKG001 lesson: audit the states that
        ///     make an action a guaranteed no-op, and don't offer it unless it is executable);
        ///   · and **97% of the bytes** came from three groups whose copies were imported as DIFFERENT asset types —
        ///     `Env_Daylight 1.png` alone promised 157 MB as a Texture2D/Cubemap pair. A copy cannot be redirected
        ///     into an asset that does not expose its objects, so those bytes were never on the table either.
        /// Two copies of different main type can never merge, and the type is metadata — no asset load, so the
        /// scanner can afford to ask. Sub-object differences WITHIN one type (an FBX whose copies expose different
        /// child objects) still need the objects themselves, so they stay the merge's business to refuse; the
        /// estimate is an upper bound on those, and <see cref="DuplicateAssetMerger.MergeAll"/> now says so by name.
        /// </summary>
        internal static MergePlan PlanMerge(IReadOnlyList<string> group, Func<string, bool> isPathLoaded = null,
                                            Func<string, Type> mainTypeOf = null)
        {
            if (group == null || group.Count < 2) return new MergePlan(0, 0);
            isPathLoaded ??= DuplicateAssetMerger.IsLoadedByStringPath;
            mainTypeOf ??= AssetDatabase.GetMainAssetTypeAtPath;

            // Copies only ever merge into a copy of the SAME main type, so the group is really one sub-group per
            // type and the merge happens inside whichever one the kept copy belongs to.
            string keep = null;
            foreach (var p in group)                       // a path-loaded copy is kept when there is one…
                if (isPathLoaded(p)) { keep = p; break; }

            if (keep == null)
            {
                // …otherwise the kept copy is a plain one, and the chooser maximises what it can absorb — which for
                // the type split means it lands in the biggest same-type class. Count that class.
                var perType = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in group)
                {
                    string key = TypeKey(mainTypeOf, p);
                    perType.TryGetValue(key, out int n);
                    perType[key] = n + 1;
                }
                // Biggest class first; inside it prefer a SHIPPING copy as the survivor. Assuming an editor-only
                // survivor would count a shipping copy as reclaimed that the merge may well keep — the estimate has
                // to be the floor of what the merge delivers, never the ceiling.
                int bestCount = 0;
                bool bestShips = false;
                foreach (var p in group)
                {
                    int n = perType[TypeKey(mainTypeOf, p)];
                    bool ships = !ScannerUtil.IsEditorOnlyPath(p);
                    if (n > bestCount || (n == bestCount && ships && !bestShips))
                    {
                        bestCount = n;
                        bestShips = ships;
                        keep = p;
                    }
                }
                if (keep == null) return new MergePlan(0, 0);
            }

            string keepType = TypeKey(mainTypeOf, keep);
            int deletable = 0, shippingDeletable = 0;
            foreach (var p in group)
            {
                if (ReferenceEquals(p, keep) || p == keep) continue;
                if (isPathLoaded(p)) continue;                          // undeletable whatever we keep
                if (!string.Equals(TypeKey(mainTypeOf, p), keepType, StringComparison.Ordinal)) continue;
                deletable++;
                if (!ScannerUtil.IsEditorOnlyPath(p)) shippingDeletable++;
            }
            return new MergePlan(deletable, shippingDeletable);
        }

        /// <summary>
        /// Why this group has no one-click merge, and what would give it one — said per cause, because the causes need
        /// different work and the old single sentence named all four at once ("Resources, StreamingAssets, an
        /// AssetBundle, or Addressables … dedup it by hand"). Measured on one project: of 8 such groups, 3 were held by
        /// Resources folders and 5 by Addressables entries, so for 5 of them the first half of that sentence pointed at
        /// a folder they were never in — and "dedup it by hand" is not an instruction, it is a shrug.
        ///
        /// Each branch names an action that MAKES THE GROUP MERGEABLE rather than describing the blockage. Deliberately
        /// does not cite a rule id: ASSET.AARES001 only exists when Addressables is installed, and a finding that
        /// points at a rule this scan did not produce is the dangling cross-reference the project rule forbids.
        /// </summary>
        internal static string NoMergeReason(IReadOnlyList<string> group,
                                             Func<string, DuplicateAssetMerger.PathLoadKind> kindOf = null)
        {
            kindOf ??= DuplicateAssetMerger.PathLoadKindOf;

            int resources = 0, streaming = 0, bundles = 0, entries = 0, plain = 0;
            foreach (var p in group)
                switch (kindOf(p))
                {
                    case DuplicateAssetMerger.PathLoadKind.Resources: resources++; break;
                    case DuplicateAssetMerger.PathLoadKind.StreamingAssets: streaming++; break;
                    case DuplicateAssetMerger.PathLoadKind.AssetBundle: bundles++; break;
                    case DuplicateAssetMerger.PathLoadKind.AddressablesEntry: entries++; break;
                    default: plain++; break;
                }

            // Not a load at all: the copies are different KINDS of asset, so nothing can be redirected between them.
            if (resources + streaming + bundles + entries == 0)
                return L.Tr("\nNo copy can be merged into another: they were imported as different kinds of asset (the same bytes brought in once as a texture and once as a cubemap, say), and a reference cannot be redirected between incompatible objects. If that difference is not deliberate, align the import settings and re-scan.",
                            "\n没有副本能合进另一份：它们被导入成了不同种类的资产（同样的字节，一处当贴图、一处当 Cubemap），不兼容的对象之间无法重定向引用。如果这个差别不是有意为之，请统一导入设置后重新扫描。");

            if (resources > 0 && streaming + bundles + entries == 0)
                return L.Tr($"\nAll {resources} copies live under a Resources folder, which ships whole in the player and is read by name at runtime — a reference redirect cannot repair a `Resources.Load(\"path\")`, so none of them can be deleted. Move all but one out of Resources and update the call sites that load it; the group becomes mergeable on the next scan. Doing this first also unblocks the rest of the duplication work.",
                            $"\n{resources} 份副本全部位于 Resources 目录——该目录整体进包、运行时按名字读取，引用重定向修不了 `Resources.Load(\"路径\")`，所以一份都删不掉。把其中除一份外的都移出 Resources 并改掉加载它的调用点，下次扫描这一组就能合并。先做这件事也会解开后续的去重工作。");

            if (entries > 0 && resources + streaming + bundles == 0)
                return L.Tr($"\nAll {entries} copies are Addressables entries, loaded by address — a reference redirect cannot repair that, so none of them can be deleted. They hold identical bytes, so one entry is enough: remove all but one from its Addressables group, then merge on the next scan. If PerfLint's \"Extract to shared group\" put them there, Tools ▸ PerfLint ▸ Revert \"PerfLint Shared\" Extraction takes them back out.",
                            $"\n{entries} 份副本全部是 Addressables 条目、按 address 加载——引用重定向修不了这类加载，所以一份都删不掉。它们内容完全相同，留一个条目就够：把其余的从各自的 Addressables group 里移除，下次扫描即可合并。如果是 PerfLint 的「提取到公共 group」放进去的，用 Tools ▸ PerfLint ▸ Revert「PerfLint Shared」Extraction 取出来。");

            if (bundles > 0 && resources + streaming + entries == 0)
                return L.Tr($"\nAll {bundles} copies are assigned to an AssetBundle and loaded by name — `bundle.LoadAsset(\"name\")` is not a GUID reference, so a redirect cannot repair it and none of them can be deleted. Clear the AssetBundle assignment on all but one (Inspector ▸ bottom ▸ AssetBundle), then merge on the next scan.",
                            $"\n{bundles} 份副本都被分配到了 AssetBundle、按名字加载——`bundle.LoadAsset(\"名字\")` 不是 GUID 引用，重定向修不了，所以一份都删不掉。把其中除一份外的 AssetBundle 分配清空（Inspector ▸ 底部 ▸ AssetBundle），下次扫描即可合并。");

            return L.Tr("\nEvery copy is pinned by a different kind of string-keyed load (Resources / StreamingAssets / AssetBundle / Addressables), and a reference redirect repairs none of them. Free all but one — move it out of the folder, clear its AssetBundle name, or drop its Addressables entry, whichever applies to that copy — and the group becomes mergeable on the next scan.",
                        "\n每一份副本各自被不同形式的字符串加载钉住（Resources / StreamingAssets / AssetBundle / Addressables），引用重定向对它们都无效。把其中除一份外的都解开——按各自情况移出目录、清空 AssetBundle 名字、或去掉 Addressables 条目——下次扫描这一组就能合并。");
        }

        /// <summary>Main asset type as a comparison key; unresolvable types compare equal to each other only.</summary>
        static string TypeKey(Func<string, Type> mainTypeOf, string path)
        {
            Type t;
            try { t = mainTypeOf(path); } catch { t = null; }
            return t == null ? "?" : t.FullName;
        }

        /// <summary>
        /// Pure helper (unit-tested): build bytes a merge of this duplicate group would actually reclaim — the
        /// copies it can delete AND that would have shipped, since neither an undeletable copy nor an editor-only
        /// one gives a player build anything back.
        /// </summary>
        internal static long BuildSavingsBytes(IReadOnlyList<string> group, long fileSizeBytes)
        {
            if (group == null || fileSizeBytes <= 0) return 0;
            return PlanMerge(group).ShippingDeletable * fileSizeBytes;
        }

    }
}
