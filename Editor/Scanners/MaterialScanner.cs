using System.Collections.Generic;
using System.IO;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 material diagnostics. Deliberately avoids reporting "GPU Instancing missing" —
    /// that would be bad advice under an SRP (enabling Instancing actually kicks the material
    /// out of the SRP Batcher). Instead, genuinely low-false-positive, high-value rules:
    ///   MAT001 — Pipeline/Shader mismatch (URP/HDRP using a Built-in shader or vice versa → pink, not batched).
    ///   MAT002 — Material has GPU Instancing enabled under an SRP (exits the SRP Batcher; slower in most scenes). Info, handle with care.
    ///   MAT003 — Material's shader reference is missing/None (deleted asset, uninstalled package) → renders magenta.
    /// </summary>
    public sealed class MaterialScanner : IScanner
    {
        public string Name => "Materials";
        public Domain Domain => Domain.Performance;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var rp = GraphicsSettings.currentRenderPipeline; // null = Built-in pipeline
            bool isSrp = rp != null;

            // Whether the active SRP is URP (as opposed to HDRP) — the "one-click upgrade to URP" Action
            // is only exposed for URP. We do not reference the URP package directly; instead we identify
            // it by the render pipeline asset's type name (URP uses UniversalRenderPipelineAsset).
            // HDRP's material upgrader is different and higher risk — out of scope for this release (see progress-ledger).
            bool isUrp = isSrp && rp.GetType().FullName?.Contains("Universal") == true;

            var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;

                string file = Path.GetFileName(path);

                // MAT003 — shader reference missing/None → magenta. Depending on the Unity version a dangling
                // reference reads back as null OR as the built-in error-shader placeholder; treat both as missing.
                // Distinct from MAT001 (shader exists, wrong pipeline family) and from a shader that exists but fails
                // to compile (mat.shader stays non-null there — that is a shader-level problem, not this material's).
                if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                {
                    string cap = path;
                    yield return new Finding(
                        ruleId: "MAT003",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr($"Material has a missing shader: '{file}'", $"材质缺少着色器：'{file}'"),
                        groupTitle: L.Tr("Materials with a missing shader (render magenta)", "材质缺少着色器（渲染为洋红）"),
                        detail: L.Tr(
                            $"'{file}' has no shader assigned — its shader reference is None or points to an asset that no longer exists " +
                            "(deleted, or its source package/asset isn't installed in this project). Everything using this material renders magenta. " +
                            "Reassign a shader in the material's Inspector; if the original shader came from a package or store asset, install/restore " +
                            "that source first. No automatic fix on purpose — which shader belongs here is a project decision.",
                            $"'{file}' 未指定着色器——其引用为 None 或指向已不存在的资产（被删除，或其来源包/资产未安装到本工程）。" +
                            "使用该材质的所有物体都会渲染为洋红。在材质的 Inspector 里重新指定着色器；若原着色器来自某个包或商店资产，" +
                            "先恢复其来源。刻意不提供自动修复——该用哪个着色器是项目决策。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(cap));
                    continue;
                }

                string sh = mat.shader.name;

                // MAT001 — Pipeline/Shader mismatch
                if (isSrp && IsBuiltinLitShader(sh))
                {
                    // Attach the "one-click upgrade to URP" Action only when the pipeline is URP and the shader
                    // falls within the reliable coverage of the official upgrader (delegate is null when the URP package is absent).
                    var action = isUrp ? MaterialUpgradeBridge.CreateUpgradeAction?.Invoke(path, sh) : null;
                    yield return new Finding(
                        ruleId: "MAT001",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Material uses a Built-in shader (project is on an SRP)", "材质用了 Built-in shader（当前为 SRP 管线）"),
                        detail: L.Tr($"'{file}' uses the Built-in pipeline shader \"{sh}\", but the project is currently on URP/HDRP. " +
                                "Such materials usually render incorrectly (pink) and are not batched by the SRP Batcher. " +
                                "Switch to the matching URP/HDRP shader (e.g. Universal Render Pipeline/Lit) and reconfigure its textures and parameters.",
                                $"'{file}' 使用 Built-in 管线 shader「{sh}」，但项目当前是 URP/HDRP。" +
                                "这类材质通常渲染异常（粉红）且不被 SRP Batcher 批处理。" +
                                "建议改用对应的 URP/HDRP shader（如 Universal Render Pipeline/Lit）并重新配置贴图与参数。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path),
                        action: action);
                }
                else if (!isSrp && IsSrpShader(sh))
                {
                    yield return new Finding(
                        ruleId: "MAT001",
                        domain: Domain.Performance,
                        severity: Severity.Warning,
                        title: L.Tr("Material uses an SRP shader (project is on Built-in)", "材质用了 SRP shader（当前为 Built-in 管线）"),
                        detail: L.Tr($"'{file}' uses the SRP pipeline shader \"{sh}\", but the project is currently on the Built-in pipeline, so it renders pink. " +
                                "Switch to a Built-in shader (e.g. Standard), or change the project's render pipeline.",
                                $"'{file}' 使用 SRP 管线 shader「{sh}」，但项目当前是 Built-in 管线，会渲染为粉红。" +
                                "建议改用 Built-in shader（如 Standard），或切换项目渲染管线。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path));
                }

                // MAT002 — GPU Instancing enabled under an SRP
                if (isSrp && mat.enableInstancing)
                {
                    yield return new Finding(
                        ruleId: "MAT002",
                        domain: Domain.Performance,
                        severity: Severity.Info,
                        title: L.Tr("Material has GPU Instancing enabled under an SRP", "SRP 下材质开启了 GPU Instancing"),
                        detail: L.Tr($"'{file}' has GPU Instancing enabled under URP/HDRP, which makes this material drop out of the SRP Batcher. " +
                                "In most cases the SRP Batcher is better; only keep Instancing when you genuinely have many instances of the same mesh (e.g. massive grass, bullets). Judge by your actual scene.",
                                $"'{file}' 在 URP/HDRP 下开启了 GPU Instancing，会使该材质退出 SRP Batcher。" +
                                "多数情况下 SRP Batcher 更优；仅当确有大量相同网格的实例（如海量草、子弹）时才保留 Instancing。请按实际场景判断。"),
                        targetPath: path,
                        ping: () => ScannerUtil.PingAsset(path));
                }
            }
        }

        // Lit shaders belonging to the Built-in pipeline (high-confidence set that causes problems under an SRP).
        internal static bool IsBuiltinLitShader(string name)
        {
            return name == "Standard"
                   || name == "Standard (Specular setup)"
                   || name == "Autodesk Interactive"
                   || name.StartsWith("Legacy Shaders/")
                   || name.StartsWith("Nature/");
        }

        internal static bool IsSrpShader(string name)
        {
            // Only recognise explicit URP/HDRP prefixes; ShaderGraph in newer versions can target Built-in,
            // so we do not use it as a signal here to avoid false positives.
            return name.StartsWith("Universal Render Pipeline/")
                   || name.StartsWith("HDRP/");
        }
    }
}
