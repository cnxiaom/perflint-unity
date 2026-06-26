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
    ///   PERF.TEX005 — Compression was requested but the imported texture is actually uncompressed (the importer silently fell back, multiplying VRAM/memory). The usual cause is dimensions a block format can't handle: ETC/ETC2 need multiples of 4, PVRTC needs square power-of-two. Evaluated against the active build target's real imported format → the engine's literal verdict, so zero false positives.
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

                // PERF.TEX005 — Compression requested but the imported texture is actually uncompressed (silent fallback).
                // Judged against the ACTIVE build target's real imported format (Texture2D.format) — the engine's own verdict,
                // so there are no false positives. The intent is read for the active platform too (so the two sides match);
                // this rule therefore ignores context.TargetPlatform (which is about hypothetical other platforms, not what was actually built).
                string activePlatform = ScannerUtil.ActivePlatformName();
                if (tex != null
                    && IsSilentCompressionFallback(!IsEffectivelyUncompressed(importer, activePlatform), tex.format))
                {
                    yield return new Finding(
                        ruleId: "PERF.TEX005",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Texture compression silently failed", "纹理压缩静默失败"),
                        detail: L.Tr($"'{file}' ({w}x{h}) is set to Compressed, but the importer fell back to an uncompressed format ({tex.format}), " +
                                "multiplying its VRAM and memory use. The usual cause is dimensions a block format can't compress: ETC/ETC2 require both sides to be multiples of 4, " +
                                "and PVRTC requires square power-of-two. Resize to a compatible size (a multiple of 4, ideally power-of-two), or switch to a format that allows these dimensions (e.g. ASTC).",
                                $"'{file}' ({w}x{h}) 设为 Compressed，但导入器回退成了未压缩格式（{tex.format}），显存与内存占用翻数倍。" +
                                "常见原因是尺寸不满足块压缩要求：ETC/ETC2 要求宽高均为 4 的倍数，PVRTC 要求正方形且为 2 的幂。" +
                                "请改为兼容尺寸（4 的倍数，最好是 2 的幂），或改用允许该尺寸的格式（如 ASTC）。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path));
                }
            }
        }

        /// <summary>
        /// The TEX005 decision as a pure predicate (separated out so it is deterministically unit-testable without depending on
        /// whether a given engine/platform actually falls back on NPOT — desktop DXT pads NPOT and compresses fine, while mobile
        /// ETC2/PVRTC genuinely fall back to uncompressed). Fires only when compression was requested yet the imported format is uncompressed.
        /// </summary>
        internal static bool IsSilentCompressionFallback(bool compressionRequested, TextureFormat actual)
            => compressionRequested && IsUncompressedRuntimeFormat(actual);

        /// <summary>
        /// Whether a runtime <see cref="TextureFormat"/> belongs to the uncompressed family. Mirrors <see cref="IsUncompressedFormat"/>
        /// (which works on the import-time TextureImporterFormat) but for the actually-imported runtime format. Compressed families
        /// are detected by name prefix (DXT/BC/PVRTC/ETC/EAC/ASTC/Crunched); everything else (RGBA32/RGB24/RGBAHalf/Alpha8/R8…) is uncompressed.
        /// </summary>
        internal static bool IsUncompressedRuntimeFormat(TextureFormat fmt)
        {
            string n = fmt.ToString();
            bool compressed = n.StartsWith("DXT") || n.StartsWith("BC") || n.StartsWith("PVRTC")
                || n.StartsWith("ETC") || n.StartsWith("EAC") || n.StartsWith("ASTC")
                || n.Contains("Crunched");
            return !compressed;
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
