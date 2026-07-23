using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Performance domain: the Static Batching memory bill.
    ///   PERF.SBATCH001 — Static Batching is enabled for the active build target AND the loaded scene(s) carry enough
    ///     BatchingStatic mesh instances that the build-time Combined Mesh would add an estimated ≥32MB of mesh memory.
    ///
    /// Why this matters (course-verified, real project): the build step copies EVERY statically batched renderer's
    /// vertices (world-space transformed) plus indices into Combined Meshes — one copy PER INSTANCE, so 100 placements
    /// of a 10k-vert rock = 1M combined vertices, on top of the original shared mesh. A real-world scene measured
    /// 350MB of Combined Mesh (total 980MB) that dropped to 62MB by disabling the toggle, GPU time unchanged — an
    /// invisible bill nobody thinks to audit. This is a frame-time ↔ memory TRADE-OFF, not a defect: the finding is a
    /// quantified bill for an informed decision, with a one-click toggle-off action (Pro), not a blind recommendation.
    ///
    /// Estimate = Σ per static instance (vertexCount × vertexStride + indexCount × 2 bytes). Approximate by design
    /// (~16-bit indices since batches split at 64000 verts; build may strip attributes with Optimize Mesh Data) and
    /// labeled as such. Only the currently LOADED scene(s) are counted — scanning never opens/closes the user's scenes;
    /// the wording tells the user to open their heaviest scene for a full bill.
    ///
    /// The Static Batching toggle has NO stable public API (the old dynamic-batching rule was removed for this reason);
    /// we go through the internal PlayerSettings.GetBatchingForPlatform via reflection, and the rule silently skips when
    /// the API is absent. A dedicated unit test asserts the reflection resolves, so the release version matrix
    /// (2021.3/2022.3/2023.1/U6) catches any signature change per version instead of silently losing the rule.
    /// </summary>
    public sealed class StaticBatchingScanner : IScanner, ISceneScoped
    {
        public string Name => "Static Batching Memory";
        public Domain Domain => Domain.Performance;

        /// <summary>Report threshold. Internal-settable so tests can trip the rule with a tiny cube.</summary>
        internal static long ThresholdBytes = 32L * 1024 * 1024;

        /// <summary>Test hook: overrides the reflection-read "is static batching enabled" answer. Null in production.</summary>
        internal static Func<bool?> BatchingEnabledOverride;

        private const int TopContributors = 5;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            bool? enabled = BatchingEnabledOverride != null
                ? BatchingEnabledOverride()
                : TryGetStaticBatching(EditorUserBuildSettings.activeBuildTarget);
            if (enabled != true) yield break; // off, or the internal API is gone in this Unity version → silent

            // Aggregate static mesh instances across all loaded scenes, grouped by shared mesh (instances of the same
            // mesh each get their own combined copy — that multiplication is the whole story).
            var byMesh = new Dictionary<Mesh, (int instances, long bytesPerInstance)>();
            long totalBytes = 0;
            int totalInstances = 0;
            var sceneNames = new List<string>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                sceneNames.Add(string.IsNullOrEmpty(scene.name) ? "(untitled)" : scene.name);

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(includeInactive: true))
                    {
                        var mesh = mf.sharedMesh;
                        if (mesh == null) continue;
                        if (mf.GetComponent<MeshRenderer>() == null) continue; // only renderers participate in batching
                        if ((GameObjectUtility.GetStaticEditorFlags(mf.gameObject) & StaticEditorFlags.BatchingStatic) == 0) continue;

                        if (!byMesh.TryGetValue(mesh, out var entry))
                            entry = (0, EstimateInstanceBytes(mesh));
                        entry.instances++;
                        byMesh[mesh] = entry;
                        totalBytes += entry.bytesPerInstance;
                        totalInstances++;
                    }
                }
            }

            if (totalBytes < ThresholdBytes) yield break;

            string totalHuman = ScannerUtil.Human(totalBytes);
            var top = byMesh.OrderByDescending(kv => (long)kv.Value.instances * kv.Value.bytesPerInstance)
                .Take(TopContributors)
                .Select(kv => L.Tr(
                    $"{kv.Value.instances}× '{kv.Key.name}' ≈ {ScannerUtil.Human((long)kv.Value.instances * kv.Value.bytesPerInstance)}",
                    $"{kv.Value.instances}× '{kv.Key.name}' ≈ {ScannerUtil.Human((long)kv.Value.instances * kv.Value.bytesPerInstance)}"))
                .ToList();

            var target = EditorUserBuildSettings.activeBuildTarget;
            yield return new Finding(
                ruleId: "PERF.SBATCH001",
                domain: Domain.Performance,
                severity: Severity.Warning,
                title: L.Tr($"Static Batching would add an estimated ~{totalHuman} of Combined Mesh memory", $"Static Batching 预计额外产生约 {totalHuman} 的 Combined Mesh 内存"),
                groupTitle: L.Tr("Static Batching Combined Mesh memory bill", "Static Batching 的 Combined Mesh 内存账单"),
                detail: L.Tr($"Static Batching is enabled for {target}, and the loaded scene(s) ({string.Join(", ", sceneNames)}) contain " +
                             $"{totalInstances} BatchingStatic mesh instances. At build time each instance's vertices+indices are COPIED into " +
                             $"Combined Meshes (per instance, on top of the original mesh) — estimated ~{totalHuman} of extra runtime mesh memory. " +
                             "Top contributors:\n  " + string.Join("\n  ", top) + "\n" +
                             "This is a frame-time ↔ memory trade-off, not a defect: static batching cuts draw-call CPU cost by paying memory. " +
                             "If your project is memory-bound (or the Memory Profiler shows Mesh dominated by 'Combined Mesh'), disabling it can " +
                             "reclaim most of this estimate; a real-world case dropped Mesh memory 350MB → 62MB with GPU time unchanged. " +
                             "The Combined Meshes are baked into the player too, so this inflates build size as well — and Unity's Build Report hides it: " +
                             "its per-category breakdown counts user assets only, so the combined geometry shows under no category (not even Meshes), only in 'Complete build size'. " +
                             "If you are draw-call-bound instead, keep it and treat this as your bill. The estimate is approximate (~16-bit indices assumed; " +
                             "Optimize Mesh Data may strip attributes at build). Only loaded scenes are counted — open your heaviest scene for a full bill. " +
                             "Also note batches split at 64000 vertices, and marking huge unique meshes BatchingStatic buys nothing (no shared draws) while still paying the copy.",
                             $"{target} 平台启用了 Static Batching，且当前已加载场景（{string.Join("、", sceneNames)}）含 " +
                             $"{totalInstances} 个 BatchingStatic 网格实例。构建时每个实例的顶点+索引都会被复制进 Combined Mesh" +
                             $"（按实例、叠加在原网格之上）——预计额外占用约 {totalHuman} 运行时网格内存。主要来源：\n  " + string.Join("\n  ", top) + "\n" +
                             "这是帧时间 ↔ 内存的取舍而非缺陷：静态合批用内存换 Draw Call 的 CPU 开销。若项目受内存压制" +
                             "（或 Memory Profiler 里 Mesh 大头是「Combined Mesh」），关闭它能拿回本估算的大部分；实测案例网格内存 350MB → 62MB、GPU 耗时不变。" +
                             "这些 Combined Mesh 也会被打进 Player，故同样撑大包体——而 Unity 的 Build Report 看不见它：按类目统计只算用户资产，合并几何不落任何类目（连 Meshes 都不算），只体现在 Complete build size 总数里。" +
                             "若瓶颈在 Draw Call，则保留并把这当成一笔明账。估算为约值（按 16 位索引估；构建时 Optimize Mesh Data 可能剔除部分顶点属性）。" +
                             "只统计已加载场景——打开最重的场景可得完整账单。另注意合批按 64000 顶点拆批；巨型独占网格标 BatchingStatic 没有共享收益、白付复制代价。"),
                targetPath: null,
                action: new FindingAction(
                    label: L.Tr("Disable Static Batching", "关闭 Static Batching"),
                    confirmMessage: L.Tr($"Disable Static Batching for {target} in Player Settings (Dynamic Batching is left unchanged).\n" +
                                    "Trade-off: draw calls previously merged by static batching will issue separately — verify frame time in the Profiler afterward. " +
                                    "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                    $"在 Player Settings 中关闭 {target} 平台的 Static Batching（Dynamic Batching 保持不变）。\n" +
                                    "取舍提示：原本被静态合批合并的 Draw Call 将各自提交——关闭后请用 Profiler 验证帧时间。" +
                                    "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                    run: () =>
                    {
                        if (!TrySetStaticBatching(target, false))
                            return FixResult.Fail(L.Tr("Could not modify the setting (internal Unity API unavailable in this version) — toggle it in Player Settings > Other Settings.",
                                                       "无法修改该设置（此 Unity 版本内部 API 不可用）——请在 Player Settings > Other Settings 手动关闭。"));
                        AssetDatabase.SaveAssets();
                        return FixResult.Ok(L.Tr($"Static Batching disabled for {target}.", $"已关闭 {target} 平台的 Static Batching。"));
                    }),
                // The same estimate the title states — disabling reclaims most of the combined-mesh copies.
                // Filled for BOTH dimensions: the combined meshes live in runtime memory AND get baked into the
                // player (where the Build Report can't show them — engine-generated, no category). One scene's
                // CPU-side estimate is conservative for both: measured 2026-07 (3D Gamekit) memory reclaimed ~2×
                // this figure (CPU+GPU copies) and build size shrank ~600-660MB against a ~554MB estimate.
                estimatedMemorySavingsBytes: totalBytes,
                estimatedBuildSavingsBytes: totalBytes);
        }

        /// <summary>
        /// One instance's contribution to the Combined Mesh: full vertex buffer copy + re-emitted indices (~16-bit,
        /// since a static batch splits at 64000 vertices). Attribute stride is computed from the mesh's actual vertex
        /// layout rather than assuming a fixed size.
        /// </summary>
        internal static long EstimateInstanceBytes(Mesh mesh)
        {
            long stride = 0;
            foreach (var attr in mesh.GetVertexAttributes())
                stride += (long)attr.dimension * FormatBytes(attr.format);
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                indices += mesh.GetIndexCount(i);
            return mesh.vertexCount * stride + indices * 2;
        }

        internal static int FormatBytes(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                default: // UNorm8 / SNorm8 / UInt8 / SInt8
                    return 1;
            }
        }

        // ---- Static Batching toggle via the internal PlayerSettings.GetBatchingForPlatform / SetBatchingForPlatform ----
        // Signatures stable across 2019–Unity 6: Get(BuildTarget, out int staticBatching, out int dynamicBatching) /
        // Set(BuildTarget, int, int). No public equivalent exists. Every call is defensive: absent API → null/false.

        private static MethodInfo _get, _set;
        private static bool _resolved;

        private static void ResolveApi()
        {
            if (_resolved) return;
            _resolved = true;
            var t = typeof(PlayerSettings);
            _get = t.GetMethod("GetBatchingForPlatform", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _set = t.GetMethod("SetBatchingForPlatform", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }

        /// <summary>Whether the internal batching API resolves in this Unity version (asserted by a unit test per matrix version).</summary>
        internal static bool BatchingApiAvailable()
        {
            ResolveApi();
            return _get != null && _set != null;
        }

        internal static bool? TryGetStaticBatching(BuildTarget target)
        {
            ResolveApi();
            if (_get == null) return null;
            try
            {
                var args = new object[] { target, 0, 0 };
                _get.Invoke(null, args);
                return (int)args[1] != 0;
            }
            catch { return null; }
        }

        internal static bool TrySetStaticBatching(BuildTarget target, bool value)
        {
            ResolveApi();
            if (_get == null || _set == null) return false;
            try
            {
                var args = new object[] { target, 0, 0 };
                _get.Invoke(null, args); // read current dynamic value so we only change the static half
                _set.Invoke(null, new object[] { target, value ? 1 : 0, (int)args[2] });
                return true;
            }
            catch { return false; }
        }
    }
}
