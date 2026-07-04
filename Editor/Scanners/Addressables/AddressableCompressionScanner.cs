#if PERFLINT_ADDRESSABLES
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Assets domain: Addressables group compression check.
    ///   ASSET.AACOMP001 — A LOCAL group uses LZMA compression. LZMA must be fully decompressed before anything in the
    ///     bundle can load (slow loads, decompression memory spike) and its smaller file only helps DOWNLOAD size —
    ///     local content never downloads, so a local LZMA group is all cost, no benefit. Unity's own guidance: LZ4 for
    ///     local content. Warning + one-click "Switch to LZ4" action (Pro-gated config change, like PROJ003/005).
    ///
    /// Deliberately NOT reported: LZMA on REMOTE groups (smaller download is a legitimate trade-off, and the
    /// Addressables download cache recompresses to LZ4 locally anyway) — remote/local is decided via
    /// <see cref="BundlePacking.IsRemoteLoadPath"/> on the group's evaluated LoadPath (pure logic, unit-tested in the
    /// main assembly; this optional module stays thin API glue, mirroring AddressableDuplicateScanner).
    /// Only compiled when Addressables is installed (PERFLINT_ADDRESSABLES); zero noise otherwise.
    /// </summary>
    public sealed class AddressableCompressionScanner : IScanner
    {
        public string Name => "Addressables Compression";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || settings.groups == null) yield break; // Addressables not initialized

            foreach (var group in settings.groups)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (group == null || group.entries == null || group.entries.Count == 0) continue;

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null) continue;                     // e.g. Built In Data — never built into bundles
                if (schema.Compression != BundledAssetGroupSchema.BundleCompressionMode.LZMA) continue;

                string loadPath = null;
                try { loadPath = schema.LoadPath != null ? schema.LoadPath.GetValue(settings) : null; }
                catch { /* unresolved profile variable — treat as local (the conservative direction is to report) */ }
                if (BundlePacking.IsRemoteLoadPath(loadPath)) continue; // remote LZMA = legitimate download-size trade-off

                string groupName = group.Name;
                var capturedSchema = schema;
                yield return new Finding(
                    ruleId: "ASSET.AACOMP001",
                    domain: Domain.Assets,
                    severity: Severity.Warning,
                    title: L.Tr($"Local Addressables group '{groupName}' uses LZMA compression", $"本地 Addressables group「{groupName}」使用 LZMA 压缩"),
                    groupTitle: L.Tr("Local Addressables group uses LZMA compression", "本地 Addressables group 使用 LZMA 压缩"),
                    detail: L.Tr($"Group '{groupName}' ({group.entries.Count} entries) is built with LZMA, but its load path is local ({loadPath}). " +
                                 "LZMA bundles must be fully decompressed before any asset in them can load — slower loads and a decompression memory spike — " +
                                 "while the smaller file size only helps content that is downloaded. For local content Unity recommends LZ4 (chunk-based: loads " +
                                 "directly, only the accessed chunks are decompressed). Especially important on mobile.",
                                 $"group「{groupName}」（{group.entries.Count} 个条目）用 LZMA 压缩构建，但其加载路径是本地（{loadPath}）。" +
                                 "LZMA 包必须完整解压后才能加载其中任何资源——加载更慢且有解压内存尖峰；而更小的文件体积只对需要下载的内容有意义。" +
                                 "本地内容 Unity 官方推荐 LZ4（按块加载、只解压访问到的块）。移动端尤其重要。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Switch to LZ4", "改为 LZ4"),
                        confirmMessage: L.Tr($"Set Bundle Compression of group '{groupName}' to LZ4.\n" +
                                        "This modifies the group's schema asset and cannot be reverted with Edit > Undo; commit to version control first. " +
                                        "Takes effect on the next Addressables build.",
                                        $"将 group「{groupName}」的 Bundle Compression 设为 LZ4。\n" +
                                        "此操作修改 group 的 schema 资产，无法用 Edit > Undo 撤销；建议先提交版本控制。下次 Addressables 构建生效。"),
                        run: () =>
                        {
                            capturedSchema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
                            EditorUtility.SetDirty(capturedSchema);
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr($"Group '{groupName}' compression set to LZ4.", $"group「{groupName}」压缩已设为 LZ4。"));
                        }));
            }
        }
    }
}
#endif
