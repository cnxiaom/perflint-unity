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

            // 2) Hash only files within same-size groups; merge by "size:hash" key.
            var byHash = new Dictionary<string, List<string>>();
            int processed = 0;
            foreach (var kv in bySize)
            {
                if (kv.Value.Count < 2) continue;
                foreach (var path in kv.Value)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.ReportProgress(Name, 0.5f + 0.5f * processed / Math.Max(1, paths.Count));
                    processed++;

                    string hash = HashFile(path);
                    if (hash == null) continue;
                    string key = kv.Key + ":" + hash;
                    if (!byHash.TryGetValue(key, out var list)) { list = new List<string>(); byHash[key] = list; }
                    list.Add(path);
                }
            }

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
                    // keeps the most-referenced one), redirect every project-wide GUID reference to it, delete the rest.
                    // Destructive and not Edit>Undo-able — see DuplicateAssetMerger for the guards.
                    action: new FindingAction(
                        label: L.Tr("Merge duplicates (redirect refs + delete copies)", "合并去重（重定向引用＋删副本）"),
                        confirmMessage:
                            L.Tr($"Merge {extraCopies} identical copy/copies, keeping the most-referenced one.\n\n", $"合并另 {extraCopies} 份相同副本，默认保留被引用最多的那份。\n\n") +
                            L.Tr("Every reference across the project (scenes, prefabs, materials, .meta) is redirected to the kept copy, then the redundant files are deleted.\n", "全工程的引用（场景、预制体、材质、.meta）都会重定向到保留副本，随后删除多余文件。\n") +
                            L.Tr("Requires Asset Serialization = Force Text. ", "要求 Asset Serialization = Force Text。") + PerfLintWarnings.Irreversible,
                        run: () => DuplicateAssetMerger.Merge(group),
                        runWithChoice: keep => DuplicateAssetMerger.Merge(group, keep)));
            }
        }

        private static string HashFile(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                if (!File.Exists(full)) return null;
                using (var md5 = MD5.Create())
                using (var fs = File.OpenRead(full))
                {
                    return Convert.ToBase64String(md5.ComputeHash(fs));
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
