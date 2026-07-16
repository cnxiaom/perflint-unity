using System.Collections.Generic;
using System.IO;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 mesh/model import diagnostics. Rules:
    ///   PERF.MSH001 — Model has Read/Write enabled, doubling memory usage (not needed unless mesh data is modified at runtime).
    ///   PERF.MSH002 — Mesh compression is off (Info only, because compression can affect precision — advisory only).
    ///     Gated: when the model generates Lightmap UVs, compression quantizes the UV2 channel and can bake visible
    ///     lightmap seams, so we withhold the one-click fix (advisory only) — it is a trade-off, not a safe blanket win.
    /// Uses t:Mesh to look up model file paths and deduplicate them; only processes assets imported by ModelImporter.
    /// </summary>
    public sealed class MeshImportScanner : IScanner, IFileScanner
    {
        public string Name => "Mesh / Model Import Settings";
        public Domain Domain => Domain.Performance;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var guids = AssetDatabase.FindAssets("t:Mesh", new[] { "Assets" });
            var seen = new HashSet<string>();

            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!seen.Add(path)) continue; // a model can contain multiple Meshes; deduplicate by path
                if (AssetImporter.GetAtPath(path) is not ModelImporter importer) continue;
                foreach (var f in ScanModel(path, importer)) yield return f;
            }
        }

        /// <summary>Path-based (extension) gate for incremental re-scan — see the note on TextureImportScanner.Handles. ScanFile does the precise ModelImporter check.</summary>
        public bool Handles(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            switch (Path.GetExtension(assetPath).ToLowerInvariant())
            {
                case ".fbx": case ".obj": case ".dae": case ".3ds": case ".dxf": case ".blend":
                case ".ma": case ".mb": case ".max": case ".c4d": case ".lxo": case ".jas":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Incremental single-file scan: the same MSH00x rules as the full Scan, for one model. Empty for non-model assets.</summary>
        public IEnumerable<Finding> ScanFile(string assetPath, ScanContext context)
        {
            if (AssetImporter.GetAtPath(assetPath) is not ModelImporter importer) return System.Array.Empty<Finding>();
            return ScanModel(assetPath, importer);
        }

        /// <summary>Both MSH rules for one model, in order (MSH001 then MSH002). Shared by the full Scan loop and single-file ScanFile so the two are identical per asset (asserted by ScanFile_MatchesFullScan_PerModel). No asset load — importer metadata only.</summary>
        private List<Finding> ScanModel(string path, ModelImporter importer)
        {
            var findings = new List<Finding>();
            string file = Path.GetFileName(path);

            if (importer.isReadable)
            {
                findings.Add(new Finding(
                    ruleId: "PERF.MSH001",
                    domain: Domain.Performance,
                    severity: Severity.Warning,
                    title: L.Tr("Model has Read/Write enabled", "模型开启了 Read/Write"),
                    detail: L.Tr($"'{file}' has Read/Write Enabled, so the mesh keeps a CPU copy in memory (roughly doubling its footprint). " +
                            "Unless you read/modify mesh data at runtime (procedural meshes, some NavMesh/collision uses), turn it off.",
                            $"'{file}' 开启了 Read/Write Enabled，网格会在内存中保留 CPU 副本（约翻倍）。" +
                            "除非运行时需要读取/修改网格数据（如程序化网格、部分 NavMesh/碰撞用法），否则应关闭。"),
                    targetPath: path,
                    ping: () => ScannerUtil.PingAsset(path),
                    fix: new ModelReadWriteFix(path)));
            }

            if (importer.meshCompression == ModelImporterMeshCompression.Off)
            {
                // Mesh Compression quantizes EVERY vertex channel, including the lightmap UV (UV2). When a model
                // generates Lightmap UVs, that quantization can bake visible seams/cracks into lightmapped surfaces,
                // so compression here is a trade-off that needs a human visual check — not a safe blanket win. For
                // those models we still surface the finding (build-size cost is real) but withhold the one-click fix
                // and route the user to set it manually + inspect. Non-lightmapped models keep the one-click Low fix.
                // Read from the importer only (no mesh load): author-supplied UV2 carries the same risk, but generateSecondaryUV
                // is the reliable metadata-level signal for the common auto-generated case, and the detail names the caveat.
                bool lightmapUv = importer.generateSecondaryUV;
                findings.Add(new Finding(
                    ruleId: "PERF.MSH002",
                    domain: Domain.Performance,
                    severity: Severity.Info,
                    title: lightmapUv
                        ? L.Tr("Mesh compression is off (lightmapped — set manually)", "网格压缩关闭（有光照 UV，请手动设置）")
                        : L.Tr("Mesh compression is off", "网格压缩关闭"),
                    groupTitle: L.Tr("Mesh compression is off", "网格压缩关闭"),
                    detail: lightmapUv
                        ? L.Tr($"'{file}' has Mesh Compression set to Off, inflating build size. This model generates Lightmap UVs, and mesh " +
                                "compression quantizes the lightmap UV (UV2) channel — enabling it can bake visible seams/cracks into lightmapped surfaces. " +
                                "This is a trade-off, not a free win: set it to Low manually and inspect the lightmapped result, rather than applying in bulk. " +
                                "(No one-click here on purpose.)",
                                $"'{file}' 的 Mesh Compression 为 Off，包体偏大。该模型会生成 Lightmap UV，而网格压缩会量化光照贴图 UV（UV2）通道" +
                                "——开启后可能在烘焙表面产生可见接缝/裂缝。这是取舍而非免费收益：请手动设为 Low 并检查烘焙效果，不要批量套用。" +
                                "（此处刻意不提供一键。）")
                        : L.Tr($"'{file}' has Mesh Compression set to Off, inflating build size. Setting it to Low/Medium reduces disk usage " +
                                "(note: compression can introduce vertex precision loss, so geometry-sensitive models need a visual check).",
                                $"'{file}' 的 Mesh Compression 为 Off，包体偏大。开启 Low/Medium 可减小磁盘占用" +
                                "（注意：压缩可能引入顶点精度损失，几何敏感的模型需肉眼确认）。"),
                    targetPath: path,
                    ping: () => ScannerUtil.PingAsset(path),
                    fix: lightmapUv ? null : new MeshCompressionFix(path, ModelImporterMeshCompression.Low)));
            }

            return findings;
        }
    }

    internal sealed class ModelReadWriteFix : IFix
    {
        private readonly string _path;
        public ModelReadWriteFix(string path) => _path = path;

        public string Description => L.Tr("Turn off Read/Write and reimport.", "关闭 Read/Write 并重新导入。");
        public string Preview() => $"{_path}: Read/Write → false";

        public FixResult Apply()
        {
            if (AssetImporter.GetAtPath(_path) is not ModelImporter importer)
                return FixResult.Fail(L.Tr($"Importer not found: {_path}", $"找不到导入器: {_path}"));
            if (!importer.isReadable) return FixResult.Ok(L.Tr("Already turned off.", "已是关闭状态。"));

            importer.isReadable = false;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return FixResult.Ok(L.Tr($"Read/Write turned off: {_path}", $"已关闭 Read/Write: {_path}"));
        }
    }

    internal sealed class MeshCompressionFix : IFix
    {
        private readonly string _path;
        private readonly ModelImporterMeshCompression _target;

        public MeshCompressionFix(string path, ModelImporterMeshCompression target)
        {
            _path = path;
            _target = target;
        }

        public string Description => L.Tr($"Set Mesh Compression to {_target} and reimport.", $"将 Mesh Compression 设为 {_target} 并重新导入。");
        public string Preview() => $"{_path}: Mesh Compression → {_target}";

        public FixResult Apply()
        {
            if (AssetImporter.GetAtPath(_path) is not ModelImporter importer)
                return FixResult.Fail(L.Tr($"Importer not found: {_path}", $"找不到导入器: {_path}"));
            if (importer.meshCompression == _target) return FixResult.Ok(L.Tr("Already in the target state.", "已是目标状态。"));

            importer.meshCompression = _target;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return FixResult.Ok(L.Tr($"Mesh Compression set to {_target}: {_path}", $"已设置 Mesh Compression = {_target}: {_path}"));
        }
    }
}
