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
    /// Performance domain: GPU Instancing that overlaps Static Batching and therefore does nothing.
    ///   MAT004 — Materials with GPU Instancing enabled are used by Batching Static renderers in the loaded scene(s).
    ///
    /// Why this matters (the "blanket-enable GPU Instancing" trap): Static Batching takes priority over GPU Instancing —
    /// a BatchingStatic MeshRenderer is drawn from the build-time Combined Mesh, so the material's Instancing flag is never
    /// consulted for it. Enabling GPU Instancing across the whole project (a common "optimization" auto-applied by tools /
    /// AI assistants) is therefore inert on any scene that relies on static batching, and under an SRP it is worse than
    /// inert: it also keeps each of those materials out of the SRP Batcher for their NON-static users. This is the
    /// scene-grounded counterpart to MAT002 (which flags instancing at the material-asset level): MAT004 only fires when a
    /// scene actually places instanced materials on static-batched renderers, and it quantifies the overlap.
    ///
    /// Deliberately Info + no auto-fix: the same material may legitimately be instanced on non-static / runtime-spawned
    /// copies (grass, projectiles, pooled objects) that a scene walk can't see, so turning Instancing off blindly could
    /// regress those. This is a review prompt, not a safe blanket action — waste vs. trade-off.
    ///
    /// Only the currently LOADED scene(s) are inspected (scanning never opens/closes the user's scenes), and only when
    /// Static Batching is actually enabled for the active build target — if it's off, BatchingStatic renderers are NOT
    /// batched and instancing WOULD apply, so the premise doesn't hold. The batching state is read through the same
    /// internal API as PERF.SBATCH001; the rule silently skips when that API is unavailable in the running Unity version.
    /// </summary>
    public sealed class InstancingBatchingScanner : IScanner, ISceneScoped
    {
        public string Name => "GPU Instancing vs Static Batching";
        public Domain Domain => Domain.Performance;

        /// <summary>Minimum distinct static-batched renderers using instanced materials before we report (avoids small-scene noise). Internal-settable so tests can trip the rule with a single object.</summary>
        internal static int ReportThreshold = 16;

        /// <summary>Test hook: overrides the "is static batching enabled" answer so tests don't mutate real project settings. Null in production (reads the real setting).</summary>
        internal static System.Func<bool?> BatchingEnabledOverride;

        private const int TopContributors = 5;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            bool? enabled = BatchingEnabledOverride != null
                ? BatchingEnabledOverride()
                : StaticBatchingScanner.TryGetStaticBatching(EditorUserBuildSettings.activeBuildTarget);
            if (enabled != true) yield break; // batching off, or the internal API is gone in this version → the preemption premise doesn't hold

            // Aggregate: how many BatchingStatic renderers place a GPU-Instancing material, and which materials, across loaded scenes.
            var staticRenderersPerMat = new Dictionary<Material, int>();
            var examples = new List<GameObject>();
            var perRendererMats = new HashSet<Material>();       // reused per renderer to dedupe multi-submesh repeats
            var matBuf = new List<Material>(8);                  // reused to avoid per-renderer sharedMaterials array allocation
            int affectedRenderers = 0;
            var sceneNames = new List<string>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                sceneNames.Add(string.IsNullOrEmpty(scene.name) ? "(untitled)" : scene.name);

                foreach (var root in scene.GetRootGameObjects())
                {
                    // Only MeshRenderers participate in static batching; SkinnedMeshRenderers / particles never do, so
                    // instancing on them is a separate concern (not the static-batching overlap this rule is about).
                    foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
                    {
                        if ((GameObjectUtility.GetStaticEditorFlags(mr.gameObject) & StaticEditorFlags.BatchingStatic) == 0) continue;

                        perRendererMats.Clear();
                        mr.GetSharedMaterials(matBuf);
                        for (int i = 0; i < matBuf.Count; i++)
                        {
                            var m = matBuf[i];
                            if (m == null || !m.enableInstancing) continue;
                            perRendererMats.Add(m); // dedupe: one static renderer using a material on two submeshes counts once
                        }

                        if (perRendererMats.Count == 0) continue;
                        affectedRenderers++;
                        if (examples.Count < 12) examples.Add(mr.gameObject);
                        foreach (var m in perRendererMats)
                            staticRenderersPerMat[m] = (staticRenderersPerMat.TryGetValue(m, out var c) ? c : 0) + 1;
                    }
                }
            }

            if (affectedRenderers < ReportThreshold) yield break;

            bool isSrp = GraphicsSettings.currentRenderPipeline != null;
            int affectedMaterials = staticRenderersPerMat.Count;
            var target = EditorUserBuildSettings.activeBuildTarget;

            var top = staticRenderersPerMat
                .OrderByDescending(kv => kv.Value)
                .Take(TopContributors)
                .Select(kv => $"{kv.Value}× '{(string.IsNullOrEmpty(kv.Key.name) ? "(unnamed material)" : kv.Key.name)}'")
                .ToList();
            string topStr = string.Join("\n  ", top);

            // The extra SRP-Batcher cost only bites under an SRP; state it only then, so the wording stays accurate on Built-in.
            string srpClauseEn = isSrp
                ? " Under URP/HDRP it is also worse than inert: enabling Instancing keeps each of these materials out of the SRP Batcher for any NON-static users."
                : "";
            string srpClauseZh = isSrp
                ? "在 URP/HDRP 下还不止失效：开启 Instancing 会让这些材质对任何非静态使用者退出 SRP Batcher。"
                : "";

            yield return new Finding(
                ruleId: "MAT004",
                domain: Domain.Performance,
                severity: Severity.Info,
                title: L.Tr(
                    $"GPU Instancing overlaps Static Batching on {affectedRenderers} renderers (instancing is preempted there)",
                    $"GPU Instancing 与 Static Batching 在 {affectedRenderers} 个渲染器上重叠（该处 instancing 被抢占）"),
                groupTitle: L.Tr("GPU Instancing overlaps Static Batching", "GPU Instancing 与 Static Batching 重叠"),
                detail: L.Tr(
                    $"{affectedMaterials} material(s) with GPU Instancing enabled are used by {affectedRenderers} Batching Static " +
                    $"renderer(s) in the loaded scene(s) ({string.Join(", ", sceneNames)}). Static Batching is enabled for {target}, and it " +
                    "takes priority over GPU Instancing: those renderers are drawn from the build-time Combined Mesh, so the Instancing flag " +
                    "does nothing for them." + srpClauseEn + " This is the classic result of enabling GPU Instancing in bulk — on a scene that " +
                    "relies on static batching, most of those flags are inert. Top overlaps (static renderers × material):\n  " + topStr + "\n" +
                    "What to do: let static batching handle static objects (leave Instancing off there); keep Instancing only on materials whose " +
                    "meshes are genuinely duplicated on NON-static or runtime-spawned instances (grass, projectiles, pooled objects). No auto-fix on " +
                    "purpose — the same material may be instanced legitimately elsewhere, so turning it off blindly can regress those cases; review per material.",
                    $"{affectedMaterials} 个开启了 GPU Instancing 的材质，被当前已加载场景（{string.Join("、", sceneNames)}）中 {affectedRenderers} 个 " +
                    $"Batching Static 渲染器使用。{target} 平台启用了 Static Batching，且它优先级高于 GPU Instancing：这些渲染器由构建期的 Combined Mesh " +
                    "绘制，Instancing 标记对它们不起任何作用。" + srpClauseZh + "这正是「批量开 GPU Instancing」的典型结果——在依赖静态合批的场景里，多数标记是失效的。" +
                    "主要重叠（静态渲染器数 × 材质）：\n  " + topStr + "\n" +
                    "怎么办：静态物体交给静态合批（此处 Instancing 保持关闭）；仅在网格确实在**非静态/运行时生成**的实例上重复时（草、子弹、对象池）才保留 Instancing。" +
                    "刻意不提供自动修复——同一材质可能在别处被合法实例化，盲目关闭会让那些场景退化；请逐材质复查。"),
                targetPath: null,
                ping: examples.Count > 0 ? () => SelectExamples(examples) : (System.Action)null);
        }

        /// <summary>Selects the sample static GameObjects that place an instanced material (filtering out any destroyed since the scan). Wired to the finding's Locate.</summary>
        private static void SelectExamples(List<GameObject> examples)
        {
            var alive = new List<Object>();
            foreach (var go in examples) if (go != null) alive.Add(go);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }
    }
}
