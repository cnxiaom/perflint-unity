using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Performance domain: GPU Instancing on materials that only feed skinned meshes, where it can never take effect.
    ///   MAT005 — Materials with GPU Instancing enabled are used ONLY by SkinnedMeshRenderers in the loaded scene(s).
    ///
    /// Why this matters: skinned meshes are never drawn through the GPU Instancing path — each SkinnedMeshRenderer is
    /// deformed and drawn individually, no matter how many identical copies exist (a crowd of the same skinned enemy is
    /// still N separate draws). So enabling "GPU Instancing" on a material that only skinned meshes use is inert, and
    /// under an SRP it is a small net negative: the flag also drops that material out of the SRP Batcher. This is the
    /// second half of the "blanket-enable GPU Instancing" trap (see MAT004 for the Static Batching half): a tool or AI
    /// that ticks the box on every material lights up character/creature materials where it does nothing.
    ///
    /// Fires only when, across the loaded scene(s), a material has ≥1 SkinnedMeshRenderer user and ZERO MeshRenderer
    /// users — if any MeshRenderer uses it, instancing may legitimately batch those, so we don't flag it. Deliberately
    /// Info + no auto-fix: the same material could be placed on non-skinned meshes elsewhere (other scenes, prefabs,
    /// runtime-spawned) that a scene walk can't see, so turning Instancing off is a per-material review, not a blanket
    /// action. Pipeline-independent (the fact holds under Built-in too); the SRP-Batcher clause is added only under an SRP.
    /// </summary>
    public sealed class SkinnedInstancingScanner : IScanner, ISceneScoped
    {
        public string Name => "GPU Instancing on Skinned Meshes";
        public Domain Domain => Domain.Performance;

        /// <summary>Minimum skinned-only instanced materials before we report. Internal-settable so tests can trip the rule with one material.</summary>
        internal static int ReportThreshold = 1;

        private const int MaxListed = 8;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // Per instanced material: does any MeshRenderer use it, and does any SkinnedMeshRenderer use it, across loaded scenes.
            var usage = new Dictionary<Material, (bool hasMesh, bool hasSkinned)>();
            var matBuf = new List<Material>(8); // reused to avoid per-renderer sharedMaterials array allocation
            var sceneNames = new List<string>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                sceneNames.Add(string.IsNullOrEmpty(scene.name) ? "(untitled)" : scene.name);

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                    {
                        bool isSkinned = r is SkinnedMeshRenderer;
                        bool isMesh = r is MeshRenderer;
                        if (!isSkinned && !isMesh) continue; // particles/lines/trails don't instance the way we mean here

                        r.GetSharedMaterials(matBuf);
                        for (int i = 0; i < matBuf.Count; i++)
                        {
                            var m = matBuf[i];
                            if (m == null || !m.enableInstancing) continue;
                            usage.TryGetValue(m, out var u);
                            usage[m] = (u.hasMesh || isMesh, u.hasSkinned || isSkinned);
                        }
                    }
                }
            }

            // Skinned-only instanced materials: used by skinned renderers and by no mesh renderer anywhere in the loaded scenes.
            var skinnedOnly = usage.Where(kv => kv.Value.hasSkinned && !kv.Value.hasMesh).Select(kv => kv.Key).ToList();
            if (skinnedOnly.Count < ReportThreshold) yield break;

            bool isSrp = GraphicsSettings.currentRenderPipeline != null;
            var names = skinnedOnly
                .Select(m => string.IsNullOrEmpty(m.name) ? "(unnamed material)" : m.name)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToList();
            string listStr = string.Join("\n  ", names.Take(MaxListed));
            if (names.Count > MaxListed) listStr += L.Tr($"\n  …and {names.Count - MaxListed} more", $"\n  …还有 {names.Count - MaxListed} 个");

            string srpEn = isSrp
                ? " Under URP/HDRP it is also a small net negative: the flag drops these materials out of the SRP Batcher."
                : "";
            string srpZh = isSrp
                ? "在 URP/HDRP 下还是小幅净负：该标记会让这些材质退出 SRP Batcher。"
                : "";

            // Snapshot the materials for Locate (select the .mat assets — the fix, if any, is on the material).
            var selectable = skinnedOnly.Cast<Object>().ToList();

            yield return new Finding(
                ruleId: "MAT005",
                domain: Domain.Performance,
                severity: Severity.Info,
                title: L.Tr(
                    $"GPU Instancing enabled on {skinnedOnly.Count} material(s) used only by Skinned Mesh Renderers (no effect)",
                    $"{skinnedOnly.Count} 个仅被 Skinned Mesh Renderer 使用的材质开了 GPU Instancing（无效）"),
                groupTitle: L.Tr("GPU Instancing on skinned-only materials (no effect)", "仅用于蒙皮网格的材质开了 GPU Instancing（无效）"),
                detail: L.Tr(
                    $"{skinnedOnly.Count} material(s) with GPU Instancing enabled are used only by SkinnedMeshRenderers in the loaded scene(s) " +
                    $"({string.Join(", ", sceneNames)}). Skinned meshes never go through the GPU Instancing path — each is deformed and drawn " +
                    "individually, no matter how many identical copies you have — so the Instancing flag does nothing for them." + srpEn + " Materials:\n  " + listStr + "\n" +
                    "What to do: turn GPU Instancing off on these materials — it only helps identical NON-skinned meshes drawn by MeshRenderers " +
                    "(grass, props, projectiles). No auto-fix on purpose: if a material is also used on non-skinned meshes elsewhere (other scenes, " +
                    "prefabs, runtime-spawned), instancing may still help there, so review per material.",
                    $"{skinnedOnly.Count} 个开启了 GPU Instancing 的材质，在当前已加载场景（{string.Join("、", sceneNames)}）中仅被 SkinnedMeshRenderer 使用。" +
                    "蒙皮网格永远不走 GPU Instancing 路径——每个都逐帧单独蒙皮、单独绘制，无论有多少个相同副本——所以 Instancing 标记对它们不起任何作用。" + srpZh + "涉及材质：\n  " + listStr + "\n" +
                    "怎么办：关掉这些材质的 GPU Instancing——它只对由 MeshRenderer 绘制的**非蒙皮**相同网格有用（草、道具、子弹）。刻意不自动修复：" +
                    "若某材质在别处（其它场景、Prefab、运行时生成）也用在非蒙皮网格上，Instancing 仍可能有用，请逐材质复查。"),
                targetPath: null,
                // No Group: that field is for related asset *paths* ("Select all in Project"); some skinned-only materials
                // may be scene-embedded (no path). The Ping below selects the Material objects directly, which works for both.
                ping: selectable.Count > 0 ? () => SelectMaterials(selectable) : (System.Action)null);
        }

        /// <summary>Selects the offending material assets (filtering out any destroyed since the scan). Wired to the finding's Locate.</summary>
        private static void SelectMaterials(List<Object> materials)
        {
            var alive = new List<Object>();
            foreach (var m in materials) if (m != null) alive.Add(m);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }
    }
}
