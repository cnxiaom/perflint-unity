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
    /// Second landing point of the differentiation story: after runtime evidence confirms "poor batching
    /// efficiency" (RUN.GPU003), this class pins the root cause down to **specific GameObjects** rather
    /// than just throwing out a generic optimization checklist.
    /// </summary>
    public sealed class SceneBatchingSnapshot
    {
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

        private SceneBatchingSnapshot(
            bool hasData, bool isSrp, int rendererCount, int uniqueMaterialCount,
            int instancedRendererCount, IReadOnlyList<GameObject> instancedExamples)
        {
            HasData = hasData;
            IsSrp = isSrp;
            RendererCount = rendererCount;
            UniqueMaterialCount = uniqueMaterialCount;
            InstancedMaterialRendererCount = instancedRendererCount;
            InstancedExamples = instancedExamples ?? new List<GameObject>();
        }

        public static readonly SceneBatchingSnapshot Empty =
            new SceneBatchingSnapshot(false, false, 0, 0, 0, null);

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
            }

            if (rendererCount == 0) return Empty;

            return new SceneBatchingSnapshot(
                hasData: true,
                isSrp: isSrp,
                rendererCount: rendererCount,
                uniqueMaterialCount: uniqueMats.Count,
                instancedRendererCount: instancedCount,
                instancedExamples: instancedExamples);
        }

        /// <summary>Selects the sample GameObjects with instanced materials (filtering out already-destroyed ones). Intended for use by finding.Ping.</summary>
        public void SelectInstancedExamples()
        {
            var alive = new List<Object>();
            foreach (var go in InstancedExamples)
                if (go != null) alive.Add(go);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }
    }
}
