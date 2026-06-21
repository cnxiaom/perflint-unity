using System.Collections.Generic;
using System.IO;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 audio import diagnostics. Rules:
    ///   PERF.AUD001 — A moderately long clip uses Decompress On Load, decompressing the whole clip into memory, large footprint.
    ///   PERF.AUD002 — A very long clip (background music) does not use Streaming, staying resident in memory.
    ///   PERF.AUD003 — Uses uncompressed PCM format (largest size; almost never necessary on mobile).
    /// </summary>
    public sealed class AudioImportScanner : IScanner
    {
        public string Name => "Audio Import Settings";
        public Domain Domain => Domain.Performance;

        // Empirical thresholds: > 5s should not be fully decompressed; > 30s is typically music and should stream.
        private const float DecompressLengthThreshold = 5f;
        private const float StreamingLengthThreshold = 30f;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // The effective import settings depend on the currently active build platform: first check whether that
            // platform has an override; if not, fall back to Default.
            // Platforms also impose hard format constraints — the most notable is WebGL: the only supported compression
            // format is AAC, and Streaming is not supported. So even if Default is set to PCM, a WebGL build will
            // force it to AAC (the greyed-out AAC shown on the platform page in the Inspector is the effective value).
            // Reporting "uncompressed PCM" in that case would be a false positive; suggesting Streaming would likewise
            // be an invalid recommendation. Therefore both of those rules are skipped when targeting WebGL.
            string platform = !string.IsNullOrEmpty(context.TargetPlatform)
                ? context.TargetPlatform
                : BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString();
            bool isWebGL = platform == "WebGL";

            var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (AssetImporter.GetAtPath(path) is not AudioImporter importer) continue;

                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                float length = clip != null ? clip.length : 0f;
                string file = Path.GetFileName(path);
                var settings = EffectiveSettings(importer, platform);

                // PERF.AUD002 — Long clip not using Streaming (checked before AUD001 to avoid double-reporting the same file; WebGL does not support Streaming, so skip).
                if (!isWebGL && length >= StreamingLengthThreshold && settings.loadType != AudioClipLoadType.Streaming)
                {
                    yield return new Finding(
                        ruleId: "PERF.AUD002",
                        domain: Domain.Performance,
                        severity: Severity.Info,
                        title: L.Tr("Long audio clip not using Streaming", "长音频未使用 Streaming"),
                        detail: L.Tr($"'{file}' ({length:0.0}s) is long and likely background music. Set Load Type to Streaming " +
                                "so it decodes while playing instead of staying resident in memory.",
                                $"'{file}'（{length:0.0}s）较长，多为背景音乐，建议 Load Type 设为 Streaming，" +
                                "边播边解码，避免常驻内存。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        fix: new AudioLoadTypeFix(path, AudioClipLoadType.Streaming, platform));
                }
                // PERF.AUD001 — Moderately long clip uses Decompress On Load.
                else if (length >= DecompressLengthThreshold && settings.loadType == AudioClipLoadType.DecompressOnLoad)
                {
                    yield return new Finding(
                        ruleId: "PERF.AUD001",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Audio clip uses Decompress On Load", "音频使用 Decompress On Load"),
                        detail: L.Tr($"'{file}' ({length:0.0}s) uses Decompress On Load, which decompresses the whole clip to PCM resident in memory, " +
                                "a large footprint. Switch to Compressed In Memory (decode on playback). Decompress On Load only fits short, frequently-played SFX.",
                                $"'{file}'（{length:0.0}s）使用 Decompress On Load，会整段解压为 PCM 常驻内存，占用大。" +
                                "建议改为 Compressed In Memory（播放时解码）。短促高频音效才适合 Decompress On Load。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        fix: new AudioLoadTypeFix(path, AudioClipLoadType.CompressedInMemory, platform));
                }

                // PERF.AUD003 — Uncompressed PCM format (evaluated independently of load type; largest size; almost never necessary on mobile; always AAC on WebGL, so skip).
                if (!isWebGL && settings.compressionFormat == AudioCompressionFormat.PCM)
                {
                    yield return new Finding(
                        ruleId: "PERF.AUD003",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Audio clip uses uncompressed PCM format", "音频使用未压缩 PCM 格式"),
                        detail: L.Tr($"'{file}' ({length:0.0}s) has Compression Format PCM (uncompressed), the largest on disk and in memory. " +
                                "On mobile, prefer Vorbis (good for both SFX and music); short, frequent SFX (footsteps, gunshots) can use ADPCM, which is much smaller than PCM and decodes fast. " +
                                "Only very short SFX needing zero decode latency justify keeping PCM.",
                                $"'{file}'（{length:0.0}s）的 Compression Format 为 PCM（未压缩），磁盘与内存占用最大。" +
                                "移动端建议改为 Vorbis（音效/音乐通用）；短促高频音效（脚步、枪声）可用 ADPCM——比 PCM 小很多且解码快。" +
                                "仅极短、追求零解码延迟的音效才有保留 PCM 的理由。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        fix: new AudioCompressionFix(path, AudioCompressionFormat.Vorbis, platform));
                }
            }
        }

        /// <summary>
        /// Returns the effective import settings for the current build platform: uses the platform override if one
        /// exists, otherwise falls back to Default.
        /// The platform name is the BuildTargetGroup string ("Standalone"/"WebGL"/"iOS"/"Android"…); for unknown
        /// names ContainsSampleSettingsOverride returns false, safely falling back to Default.
        /// </summary>
        private static AudioImporterSampleSettings EffectiveSettings(AudioImporter importer, string platform)
        {
            return importer.ContainsSampleSettingsOverride(platform)
                ? importer.GetOverrideSampleSettings(platform)
                : importer.defaultSampleSettings;
        }
    }

    internal sealed class AudioLoadTypeFix : IFix
    {
        private readonly string _path;
        private readonly AudioClipLoadType _target;
        private readonly string _platform;

        public AudioLoadTypeFix(string path, AudioClipLoadType target, string platform = null)
        {
            _path = path;
            _target = target;
            _platform = platform;
        }

        public string Description => L.Tr($"Set Load Type to {_target} and reimport.", $"将 Load Type 设为 {_target} 并重新导入。");
        public string Preview() => $"{_path}: Load Type → {_target}";

        public FixResult Apply()
        {
            if (AssetImporter.GetAtPath(_path) is not AudioImporter importer)
                return FixResult.Fail(L.Tr($"Importer not found: {_path}", $"找不到导入器: {_path}"));

            // Fix whichever setting the issue was found in: apply to the platform override if one exists, otherwise apply to Default.
            bool hasOverride = !string.IsNullOrEmpty(_platform) && importer.ContainsSampleSettingsOverride(_platform);
            var settings = hasOverride ? importer.GetOverrideSampleSettings(_platform) : importer.defaultSampleSettings;
            if (settings.loadType == _target)
                return FixResult.Ok(L.Tr("Already in the target state; no change needed.", "已是目标状态，无需修改。"));

            settings.loadType = _target;
            if (hasOverride) importer.SetOverrideSampleSettings(_platform, settings);
            else importer.defaultSampleSettings = settings;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return FixResult.Ok(L.Tr($"Load Type set to {_target}: {_path}", $"已设置 Load Type = {_target}: {_path}"));
        }
    }

    internal sealed class AudioCompressionFix : IFix
    {
        private readonly string _path;
        private readonly AudioCompressionFormat _target;
        private readonly string _platform;

        public AudioCompressionFix(string path, AudioCompressionFormat target, string platform = null)
        {
            _path = path;
            _target = target;
            _platform = platform;
        }

        public string Description => L.Tr($"Set Compression Format to {_target} and reimport.", $"将 Compression Format 设为 {_target} 并重新导入。");
        public string Preview() => $"{_path}: Compression Format → {_target}";

        public FixResult Apply()
        {
            if (AssetImporter.GetAtPath(_path) is not AudioImporter importer)
                return FixResult.Fail(L.Tr($"Importer not found: {_path}", $"找不到导入器: {_path}"));

            // Fix whichever setting the issue was found in: apply to the platform override if one exists, otherwise apply to Default.
            bool hasOverride = !string.IsNullOrEmpty(_platform) && importer.ContainsSampleSettingsOverride(_platform);
            var settings = hasOverride ? importer.GetOverrideSampleSettings(_platform) : importer.defaultSampleSettings;
            if (settings.compressionFormat == _target)
                return FixResult.Ok(L.Tr("Already in the target state; no change needed.", "已是目标状态，无需修改。"));

            settings.compressionFormat = _target;
            if (hasOverride) importer.SetOverrideSampleSettings(_platform, settings);
            else importer.defaultSampleSettings = settings;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return FixResult.Ok(L.Tr($"Compression Format set to {_target}: {_path}", $"已设置 Compression Format = {_target}: {_path}"));
        }
    }
}
