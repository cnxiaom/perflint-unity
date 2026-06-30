using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Batching snapshot of the currently active scene — **this is a Play Mode exclusive capability**:
    /// static scanning cannot see "runtime material instantiation" (accessing renderer.material clones
    /// the material), nor the actual Renderer topology of what is loaded in the scene.
    ///
    /// Second landing point of the differentiation story: after runtime evidence confirms a problem, this class pins the
    /// root cause down to **specific GameObjects/meshes** rather than just throwing out a generic optimization checklist —
    /// "poor batching efficiency" (RUN.GPU003) → the instanced renderers; "high triangle count" (RUN.GPU002) → the heaviest meshes.
    /// </summary>
    public sealed class SceneBatchingSnapshot
    {
        /// <summary>Per-mesh geometry contribution in the loaded scene: authored triangle count, how many instances, and the total. Used by RUN.GPU002 to name the heaviest models.</summary>
        public readonly struct MeshTriangleStat
        {
            public readonly string MeshName;
            public readonly long TrianglesPerInstance;
            public readonly int InstanceCount;
            public readonly long TotalTriangles; // TrianglesPerInstance * InstanceCount
            public MeshTriangleStat(string meshName, long trianglesPerInstance, int instanceCount, long totalTriangles)
            {
                MeshName = meshName;
                TrianglesPerInstance = trianglesPerInstance;
                InstanceCount = instanceCount;
                TotalTriangles = totalTriangles;
            }
        }

        public bool HasData { get; }
        public bool IsSrp { get; }

        /// <summary>Number of enabled Renderers in the scene (those that produce draw calls).</summary>
        public int RendererCount { get; }

        /// <summary>Number of unique material objects actually used for rendering (by instanceID). Runtime instantiation inflates this count — which is exactly what we want to expose.</summary>
        public int UniqueMaterialCount { get; }

        /// <summary>Number of Renderers where runtime material instantiation was detected (material name ends with "(Instance)"). The #1 invisible killer of batching.</summary>
        public int InstancedMaterialRendererCount { get; }

        /// <summary>Sample GameObjects with instanced materials (for Locate selection, up to 12). Valid only within Play Mode; references become stale after exiting (nulls are filtered at Ping time).</summary>
        public IReadOnlyList<GameObject> InstancedExamples { get; }

        /// <summary>The heaviest meshes by total authored triangles (descending, top few), for RUN.GPU002 localization. Authored counts — not the post-cull per-frame counter, but the usual suspects.</summary>
        public IReadOnlyList<MeshTriangleStat> TopTriangleMeshes { get; }

        /// <summary>Sum of authored triangles across all enabled mesh Renderers in the scene (for sizing the top contributors against the whole).</summary>
        public long TotalSceneTriangles { get; }

        /// <summary>GameObjects using the single heaviest mesh (for Locate, up to 12). Valid only within Play Mode; stale references filtered at Ping time.</summary>
        public IReadOnlyList<GameObject> TopMeshExamples { get; }

        private SceneBatchingSnapshot(
            bool hasData, bool isSrp, int rendererCount, int uniqueMaterialCount,
            int instancedRendererCount, IReadOnlyList<GameObject> instancedExamples,
            IReadOnlyList<MeshTriangleStat> topTriangleMeshes, long totalSceneTriangles,
            IReadOnlyList<GameObject> topMeshExamples)
        {
            HasData = hasData;
            IsSrp = isSrp;
            RendererCount = rendererCount;
            UniqueMaterialCount = uniqueMaterialCount;
            InstancedMaterialRendererCount = instancedRendererCount;
            InstancedExamples = instancedExamples ?? new List<GameObject>();
            TopTriangleMeshes = topTriangleMeshes ?? new List<MeshTriangleStat>();
            TotalSceneTriangles = totalSceneTriangles;
            TopMeshExamples = topMeshExamples ?? new List<GameObject>();
        }

        public static readonly SceneBatchingSnapshot Empty =
            new SceneBatchingSnapshot(false, false, 0, 0, 0, null, null, 0, null);

        /// <summary>Test-only factory: the real constructor is private and <see cref="Capture"/> needs Play Mode, so this lets unit tests
        /// feed explicit topology (pipeline / renderer + material counts / instancing / top meshes) into RuntimeAnalyzer's logic headlessly.</summary>
        internal static SceneBatchingSnapshot ForTests(bool isSrp, int rendererCount, int uniqueMaterialCount, int instancedRendererCount,
            IReadOnlyList<MeshTriangleStat> topTriangleMeshes = null, long totalSceneTriangles = 0) =>
            new SceneBatchingSnapshot(true, isSrp, rendererCount, uniqueMaterialCount, instancedRendererCount, null, topTriangleMeshes, totalSceneTriangles, null);

        /// <summary>True when there's at least one GameObject to Locate for the heaviest mesh.</summary>
        public bool HasTopMeshExamples => TopMeshExamples != null && TopMeshExamples.Count > 0;

        /// <summary>Material reuse ratio: Renderer count / unique material count. The closer to 1 this is, the less materials are being shared (one material per object = hard to batch).</summary>
        public double MaterialReuseRatio =>
            UniqueMaterialCount > 0 ? (double)RendererCount / UniqueMaterialCount : 0;

        /// <summary>
        /// Iterates all Renderers in the currently loaded scene, collecting material topology and runtime
        /// instantiation statistics. Valid in Play Mode only.
        /// O(Renderer count), typically milliseconds; safe to call synchronously from Stop().
        /// </summary>
        public static SceneBatchingSnapshot Capture()
        {
            if (!Application.isPlaying) return Empty;

            Renderer[] renderers;
#pragma warning disable CS0618 // FindObjectsByType is 2023+ only; using the old API for 2021/2022 compatibility (internal tooling, not user-facing code)
            renderers = Object.FindObjectsOfType<Renderer>();
#pragma warning restore CS0618
            if (renderers == null || renderers.Length == 0) return Empty;

            bool isSrp = GraphicsSettings.currentRenderPipeline != null;

            // Dedup by material reference, not GetInstanceID(): Unity 6 marks Object.GetInstanceID() obsolete-as-error
            // (CS0619), and its replacement GetEntityId() doesn't exist on 2021.3/2022.3. HashSet<Material> dedups by
            // reference — same instance → same entry — which is exactly what the instance-id set did, and compiles on all versions.
            var uniqueMats = new HashSet<Material>();
            var instancedExamples = new List<GameObject>();
            int rendererCount = 0;
            int instancedCount = 0;
            var matBuf = new List<Material>(8); // reused to avoid per-Renderer sharedMaterials array allocation

            // Geometry attribution for RUN.GPU002: aggregate authored triangles by mesh (keyed by reference — GetInstanceID is
            // obsolete-as-error on Unity 6). Per-mesh triangle count is computed once and cached in the accumulator.
            var meshAcc = new Dictionary<Mesh, MeshAcc>();
            long totalSceneTris = 0;

            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                // Focus on mesh-based renderers only (particles/lines/trails do not go through the standard static/dynamic batching path; including them would add noise).
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;

                rendererCount++;

                bool thisInstanced = false;
                r.GetSharedMaterials(matBuf); // reuse the List — zero allocation
                for (int i = 0; i < matBuf.Count; i++)
                {
                    var m = matBuf[i];
                    if (m == null) continue;
                    uniqueMats.Add(m);
                    // Unity names cloned materials "Original (Instance)" — a high-confidence signal of runtime instantiation.
                    if (m.name.EndsWith("(Instance)", System.StringComparison.Ordinal)) thisInstanced = true;
                }

                if (thisInstanced)
                {
                    instancedCount++;
                    if (instancedExamples.Count < 12) instancedExamples.Add(r.gameObject);
                }

                var mesh = MeshOf(r);
                if (mesh != null)
                {
                    if (!meshAcc.TryGetValue(mesh, out var acc))
                    {
                        acc = new MeshAcc { Name = mesh.name, PerInstanceTris = TriangleCount(mesh), Examples = new List<GameObject>() };
                        meshAcc[mesh] = acc;
                    }
                    acc.Count++;
                    totalSceneTris += acc.PerInstanceTris;
                    if (acc.Examples.Count < 12) acc.Examples.Add(r.gameObject);
                }
            }

            if (rendererCount == 0) return Empty;

            // Top geometry contributors (by total authored triangles), descending. Keep the heaviest mesh's GameObjects for Locate.
            var ordered = new List<MeshAcc>(meshAcc.Values);
            ordered.Sort((a, b) => (b.PerInstanceTris * b.Count).CompareTo(a.PerInstanceTris * a.Count));
            var topMeshes = new List<MeshTriangleStat>();
            List<GameObject> topMeshExamples = null;
            for (int i = 0; i < ordered.Count && i < 5; i++)
            {
                var a = ordered[i];
                long total = a.PerInstanceTris * a.Count;
                if (total <= 0) break; // nothing meaningful past here
                topMeshes.Add(new MeshTriangleStat(string.IsNullOrEmpty(a.Name) ? "(unnamed mesh)" : a.Name, a.PerInstanceTris, a.Count, total));
                if (i == 0) topMeshExamples = a.Examples;
            }

            return new SceneBatchingSnapshot(
                hasData: true,
                isSrp: isSrp,
                rendererCount: rendererCount,
                uniqueMaterialCount: uniqueMats.Count,
                instancedRendererCount: instancedCount,
                instancedExamples: instancedExamples,
                topTriangleMeshes: topMeshes,
                totalSceneTriangles: totalSceneTris,
                topMeshExamples: topMeshExamples);
        }

        private sealed class MeshAcc
        {
            public string Name;
            public long PerInstanceTris;
            public int Count;
            public List<GameObject> Examples;
        }

        /// <summary>The shared mesh a renderer draws (MeshFilter for MeshRenderer, sharedMesh for SkinnedMeshRenderer). Null if none.</summary>
        private static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        /// <summary>Authored triangle count of a mesh, allocation-free (Mesh.triangles allocates; GetIndexCount doesn't). Sums index counts / 3 across submeshes.</summary>
        private static long TriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long indices = 0;
            int sub = mesh.subMeshCount;
            for (int s = 0; s < sub; s++)
            {
                // Only triangle topology contributes "triangles"; lines/points have no triangle cost worth attributing here.
                if (mesh.GetTopology(s) == MeshTopology.Triangles) indices += (long)mesh.GetIndexCount(s);
            }
            return indices / 3;
        }

        /// <summary>Selects the sample GameObjects with instanced materials (filtering out already-destroyed ones). Intended for use by finding.Ping.</summary>
        public void SelectInstancedExamples()
        {
            var alive = new List<Object>();
            foreach (var go in InstancedExamples)
                if (go != null) alive.Add(go);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }

        /// <summary>Selects the GameObjects using the heaviest mesh (filtering out already-destroyed ones). Intended for RUN.GPU002's Locate.</summary>
        public void SelectTopTriangleMeshExamples()
        {
            var alive = new List<Object>();
            foreach (var go in TopMeshExamples)
                if (go != null) alive.Add(go);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }
    }
}
