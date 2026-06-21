using System.Collections.Generic;
using System.IO;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 texture import diagnostics. Rules:
    ///   PERF.TEX001 — Large texture has Read/Write enabled (doubles memory usage; the vast majority of textures don't need it).
    ///   PERF.TEX002 — Large texture uses Uncompressed format, multiplying VRAM/build size several times over.
    ///   PERF.TEX003 — Oversized texture (≥4096), high VRAM/memory usage; Info / reporting category.
    ///   PERF.TEX004 — Sprite/UI texture has Mipmap enabled (~33% extra memory), high-confidence waste, auto-fixable.
    /// </summary>
    public sealed class TextureImportScanner : IScanner
    {
        public string Name => "Texture Import Settings";
        public Domain Domain => Domain.Performance;

        private const int ReadWriteSizeThreshold = 512;
        private const int UncompressedSizeThreshold = 256;
        private const int OversizedThreshold = 4096; // Conservative threshold: only report truly enormous textures to keep false-positive rate low.

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // The compression format can be overridden by the "current build platform": once a platform's
            // Override is enabled, that platform uses the override's format/textureCompression, and the Default
            // page's values no longer apply to it. Reading only Default would cause false positives/negatives,
            // so TEX002 resolves the "actually effective" compression state per active platform. (Read/Write and
            // Mipmap are global settings, unaffected by platform.)
            string platform = !string.IsNullOrEmpty(context.TargetPlatform)
                ? context.TargetPlatform
                : ScannerUtil.ActivePlatformName();

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                int w = tex != null ? tex.width : 0;
                int h = tex != null ? tex.height : 0;
                int maxDim = Mathf.Max(w, h);
                string file = Path.GetFileName(path);

                // PERF.TEX001 — Read/Write
                if (importer.isReadable && maxDim >= ReadWriteSizeThreshold)
                {
                    yield return new Finding(
                        ruleId: "PERF.TEX001",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Texture has Read/Write enabled", "纹理开启了 Read/Write"),
                        detail: L.Tr($"'{file}' ({w}x{h}) has Read/Write Enabled, which keeps an uncompressed CPU copy in memory" +
                                " (roughly doubling its footprint). Unless you call GetPixels/Texture2D.Apply at runtime, turn it off.",
                                $"'{file}' ({w}x{h}) 开启了 Read/Write Enabled，会在内存中保留一份未压缩 CPU 副本" +
                                "（约翻倍占用）。除非运行时需要 GetPixels/Texture2D.Apply，否则应关闭。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        fix: new TextureToggleFix(path, readable: false));
                }

                // PERF.TEX002 — Uncompressed format (resolves the effective compression state per active platform)
                if (IsEffectivelyUncompressed(importer, platform) && maxDim >= UncompressedSizeThreshold)
                {
                    yield return new Finding(
                        ruleId: "PERF.TEX002",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Texture is uncompressed", "纹理未压缩"),
                        detail: L.Tr($"'{file}' ({w}x{h}) uses an Uncompressed format, taking several times more VRAM and build size than a compressed one. " +
                                "Switching to Compressed (the platform auto-selects ASTC/ETC2/DXT) is usually visually indistinguishable. " +
                                "For normal maps or images sensitive to banding, use CompressedHQ instead.",
                                $"'{file}' ({w}x{h}) 使用 Uncompressed 格式，显存与包体占用是压缩格式的数倍。" +
                                "改为 Compressed（平台自动选 ASTC/ETC2/DXT）通常肉眼无差。若是法线贴图或" +
                                "对色带敏感的图，可改用 CompressedHQ。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        fix: new TextureToggleFix(path, compression: TextureImporterCompression.Compressed, platform: platform));
                }

                // PERF.TEX003 — Oversized texture (reporting-only; downscaling is a judgment call, not auto-fixed)
                if (maxDim >= OversizedThreshold)
                {
                    yield return new Finding(
                        ruleId: "PERF.TEX003",
                        domain: Domain.Performance,
                        severity: Severity.Info,
                        title: L.Tr("Oversized texture", "超大纹理"),
                        detail: L.Tr($"'{file}' ({w}x{h}) is very large, with high VRAM/memory usage. Confirm you really need it this big; " +
                                "mobile typically does fine with <=2048 (lower Max Size in the import settings, per platform).",
                                $"'{file}' ({w}x{h}) 尺寸很大，显存/内存占用高。确认是否真的需要这么大；" +
                                "移动端通常 ≤2048 即可（可在导入设置降低 Max Size，按平台分别设置）。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path));
                }

                // PERF.TEX004 — Sprite/UI texture has Mipmap enabled (high-confidence waste, fixable)
                if (importer.mipmapEnabled
                    && (importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI))
                {
                    yield return new Finding(
                        ruleId: "PERF.TEX004",
                        domain: Domain.Performance,
                        severity: Severity.Info,
                        title: L.Tr("Sprite/UI texture has Mipmaps enabled", "Sprite/UI 纹理开启了 Mipmap"),
                        detail: L.Tr($"'{file}' is a Sprite/UI texture but has Mipmaps enabled, costing about 33% extra memory. " +
                                "Screen-space UI usually doesn't need mipmaps; only world-space or heavily scaled sprites do.",
                                $"'{file}' 是 Sprite/UI 纹理却开启了 Mipmap，会多占约 33% 内存。" +
                                "屏幕空间 UI 通常不需要 Mipmap；仅世界空间或会大幅缩放的精灵才需要。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        fix: new TextureToggleFix(path, mipmap: false));
                }
            }
        }

        /// <summary>
        /// Whether this texture is "uncompressed" when imported for the current build platform. If the platform's
        /// Override is enabled: when format==Automatic, look at that platform's textureCompression; otherwise look
        /// at whether the specific format belongs to the uncompressed family. If not overridden, use Default's textureCompression.
        /// </summary>
        private static bool IsEffectivelyUncompressed(TextureImporter importer, string platform)
        {
            var ps = importer.GetPlatformTextureSettings(platform);
            if (ps != null && ps.overridden)
            {
                return ps.format == TextureImporterFormat.Automatic
                    ? ps.textureCompression == TextureImporterCompression.Uncompressed
                    : IsUncompressedFormat(ps.format);
            }
            return importer.textureCompression == TextureImporterCompression.Uncompressed;
        }

        /// <summary>
        /// Determines by name prefix whether a TextureImporterFormat belongs to the compressed family (DXT/BC/PVRTC/ETC/EAC/ASTC/Crunched).
        /// The rest (RGBA32/RGB24/RGBAHalf/RGBAFloat/BGRA32...) are treated as uncompressed. The prefix approach defaults
        /// newly added uncompressed formats to uncompressed and covers all existing compressed formats; it avoids hard-enumerating
        /// dozens of members, which is easy to miss.
        /// </summary>
        internal static bool IsUncompressedFormat(TextureImporterFormat fmt)
        {
            string n = fmt.ToString();
            bool compressed = n.StartsWith("DXT") || n.StartsWith("BC") || n.StartsWith("PVRTC")
                || n.StartsWith("ETC") || n.StartsWith("EAC") || n.StartsWith("ASTC")
                || n.Contains("Crunched");
            return !compressed;
        }
    }

    /// <summary>
    /// Texture import settings fix. Can modify Read/Write and compression format individually or combined; changes go through TextureImporter and can be undone.
    /// </summary>
    internal sealed class TextureToggleFix : IFix
    {
        private readonly string _path;
        private readonly bool? _readable;
        private readonly TextureImporterCompression? _compression;
        private readonly bool? _mipmap;
        private readonly string _platform;

        public TextureToggleFix(string path, bool? readable = null,
            TextureImporterCompression? compression = null, bool? mipmap = null, string platform = null)
        {
            _path = path;
            _readable = readable;
            _compression = compression;
            _mipmap = mipmap;
            _platform = platform;
        }

        public string Description
        {
            get
            {
                if (_readable.HasValue) return L.Tr($"Set Read/Write to {_readable.Value} and reimport.", $"将 Read/Write 设为 {_readable.Value} 并重新导入。");
                if (_compression.HasValue) return L.Tr($"Set compression to {_compression.Value} and reimport.", $"将压缩格式设为 {_compression.Value} 并重新导入。");
                if (_mipmap.HasValue) return L.Tr($"Set Generate Mip Maps to {_mipmap.Value} and reimport.", $"将 Generate Mip Maps 设为 {_mipmap.Value} 并重新导入。");
                return L.Tr("No action.", "无操作。");
            }
        }

        public string Preview()
        {
            if (_readable.HasValue) return $"{_path}: Read/Write  → {_readable.Value}";
            if (_compression.HasValue) return $"{_path}: Compression → {_compression.Value}";
            if (_mipmap.HasValue) return $"{_path}: Generate Mip Maps → {_mipmap.Value}";
            return _path;
        }

        public FixResult Apply()
        {
            if (AssetImporter.GetAtPath(_path) is not TextureImporter importer)
                return FixResult.Fail(L.Tr($"Importer not found: {_path}", $"找不到导入器: {_path}"));

            bool changed = false;
            // Read/Write and Mipmap are global import settings, not per-platform.
            if (_readable.HasValue && importer.isReadable != _readable.Value)
            {
                importer.isReadable = _readable.Value;
                changed = true;
            }
            if (_mipmap.HasValue && importer.mipmapEnabled != _mipmap.Value)
            {
                importer.mipmapEnabled = _mipmap.Value;
                changed = true;
            }
            // The compression format may be overridden by the platform. Fix it wherever the problem was detected:
            // if the current platform's Override is enabled, change the override (clear the explicit uncompressed
            // format and use Automatic so the platform auto-selects a compressed format); otherwise change Default.
            if (_compression.HasValue)
            {
                bool hasOverride = !string.IsNullOrEmpty(_platform)
                    && importer.GetPlatformTextureSettings(_platform) is { overridden: true };
                if (hasOverride)
                {
                    var ps = importer.GetPlatformTextureSettings(_platform);
                    if (ps.textureCompression != _compression.Value || ps.format != TextureImporterFormat.Automatic)
                    {
                        ps.format = TextureImporterFormat.Automatic;
                        ps.textureCompression = _compression.Value;
                        importer.SetPlatformTextureSettings(ps);
                        changed = true;
                    }
                }
                else if (importer.textureCompression != _compression.Value)
                {
                    importer.textureCompression = _compression.Value;
                    changed = true;
                }
            }

            if (!changed) return FixResult.Ok(L.Tr("Already in the target state; no change needed.", "已是目标状态，无需修改。"));

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return FixResult.Ok(L.Tr($"Import settings updated: {_path}", $"已更新导入设置: {_path}"));
        }
    }
}
