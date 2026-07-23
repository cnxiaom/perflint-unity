using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
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

        /// <summary>A loaded graphics asset and its runtime memory (VRAM) size — used by RUN.MEM004 to name the biggest textures / render targets / meshes in memory.</summary>
        public readonly struct AssetMemStat
        {
            public readonly string Name;
            public readonly string Kind;   // "Texture" / "RenderTexture" / "Mesh"
            public readonly long Bytes;    // Profiler.GetRuntimeMemorySizeLong
            public readonly Object Asset;  // for Locate (project asset → pings import settings; runtime object → selects it); null-filtered at ping time
            public AssetMemStat(string name, string kind, long bytes, Object asset)
            {
                Name = name; Kind = kind; Bytes = bytes; Asset = asset;
            }
        }

        /// <summary>A group of identical runtime RenderTextures alive at once (same W×H + format, not project assets) — a strong "created repeatedly without Release()" leak signal. Used by RUN.MEM005.</summary>
        public sealed class RtLeakGroup
        {
            public readonly int Width, Height, Count;
            public readonly string Format;
            public readonly long TotalBytes;
            public readonly IReadOnlyList<Object> Examples; // for Locate (select the leaked RTs); null-filtered at ping time
            public RtLeakGroup(int width, int height, string format, int count, long totalBytes, IReadOnlyList<Object> examples)
            {
                Width = width; Height = height; Format = format; Count = count; TotalBytes = totalBytes; Examples = examples ?? new List<Object>();
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

        /// <summary>GameObjects using each of the top meshes, indexed parallel to <see cref="TopTriangleMeshes"/> (rank 0 = heaviest). For RUN.GPU002's per-mesh Locate — each rank selects its own group.
        /// Valid only within Play Mode; stale references filtered at Ping time.</summary>
        public IReadOnlyList<IReadOnlyList<GameObject>> TopMeshExamplesByRank { get; }

        /// <summary>Largest loaded graphics assets by runtime memory (textures / render targets / meshes), descending. For RUN.MEM004 VRAM localization.</summary>
        public IReadOnlyList<AssetMemStat> TopMemoryAssets { get; }

        /// <summary>The biggest group of identical runtime RenderTextures alive at once (leak-suspect), or null. For RUN.MEM005.</summary>
        public RtLeakGroup SuspectRtLeak { get; }

        private SceneBatchingSnapshot(
            bool hasData, bool isSrp, int rendererCount, int uniqueMaterialCount,
            int instancedRendererCount, IReadOnlyList<GameObject> instancedExamples,
            IReadOnlyList<MeshTriangleStat> topTriangleMeshes, long totalSceneTriangles,
            IReadOnlyList<IReadOnlyList<GameObject>> topMeshExamplesByRank,
            IReadOnlyList<AssetMemStat> topMemoryAssets,
            RtLeakGroup suspectRtLeak)
        {
            SuspectRtLeak = suspectRtLeak;
            HasData = hasData;
            IsSrp = isSrp;
            RendererCount = rendererCount;
            UniqueMaterialCount = uniqueMaterialCount;
            InstancedMaterialRendererCount = instancedRendererCount;
            InstancedExamples = instancedExamples ?? new List<GameObject>();
            TopTriangleMeshes = topTriangleMeshes ?? new List<MeshTriangleStat>();
            TotalSceneTriangles = totalSceneTriangles;
            TopMeshExamplesByRank = topMeshExamplesByRank ?? new List<IReadOnlyList<GameObject>>();
            TopMemoryAssets = topMemoryAssets ?? new List<AssetMemStat>();
        }

        public static readonly SceneBatchingSnapshot Empty =
            new SceneBatchingSnapshot(false, false, 0, 0, 0, null, null, 0, null, null, null);

        /// <summary>Test-only factory: the real constructor is private and <see cref="Capture"/> needs Play Mode, so this lets unit tests
        /// feed explicit topology (pipeline / renderer + material counts / instancing / top meshes) into RuntimeAnalyzer's logic headlessly.</summary>
        internal static SceneBatchingSnapshot ForTests(bool isSrp, int rendererCount, int uniqueMaterialCount, int instancedRendererCount,
            IReadOnlyList<MeshTriangleStat> topTriangleMeshes = null, long totalSceneTriangles = 0,
            IReadOnlyList<AssetMemStat> topMemoryAssets = null, RtLeakGroup suspectRtLeak = null,
            IReadOnlyList<IReadOnlyList<GameObject>> topMeshExamplesByRank = null) =>
            new SceneBatchingSnapshot(true, isSrp, rendererCount, uniqueMaterialCount, instancedRendererCount, null, topTriangleMeshes, totalSceneTriangles, topMeshExamplesByRank, topMemoryAssets, suspectRtLeak);

        /// <summary>True when rank <paramref name="rank"/> (parallel to <see cref="TopTriangleMeshes"/>) has at least one GameObject to Locate.</summary>
        public bool HasMeshExamples(int rank) =>
            TopMeshExamplesByRank != null && rank >= 0 && rank < TopMeshExamplesByRank.Count
            && TopMeshExamplesByRank[rank] != null && TopMeshExamplesByRank[rank].Count > 0;

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
                    // Locate should select *every* instance of the heaviest mesh, not just a handful. We don't know which mesh is heaviest
                    // until after the loop, so collect up to MaxTopMeshExamples per mesh; the top one then already has its full instance set.
                    // Total memory stays bounded by the renderer count (each renderer joins exactly one mesh's list, each list capped).
                    if (acc.Examples.Count < MaxTopMeshExamples) acc.Examples.Add(r.gameObject);
                }
            }

            if (rendererCount == 0) return Empty;

            // Top geometry contributors (by total authored triangles), descending. Keep the heaviest mesh's GameObjects for Locate.
            var ordered = new List<MeshAcc>(meshAcc.Values);
            ordered.Sort((a, b) => (b.PerInstanceTris * b.Count).CompareTo(a.PerInstanceTris * a.Count));
            var topMeshes = new List<MeshTriangleStat>();
            var topMeshExamplesByRank = new List<IReadOnlyList<GameObject>>(); // parallel to topMeshes — each rank keeps its own mesh's GameObjects for its own Locate
            for (int i = 0; i < ordered.Count && i < 5; i++)
            {
                var a = ordered[i];
                long total = a.PerInstanceTris * a.Count;
                if (total <= 0) break; // nothing meaningful past here
                topMeshes.Add(new MeshTriangleStat(string.IsNullOrEmpty(a.Name) ? "(unnamed mesh)" : a.Name, a.PerInstanceTris, a.Count, total));
                topMeshExamplesByRank.Add(a.Examples);
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
                topMeshExamplesByRank: topMeshExamplesByRank,
                topMemoryAssets: CaptureTopMemoryAssets(),
                suspectRtLeak: CaptureRtLeak());
        }

        /// <summary>
        /// Detect a likely RenderTexture leak: the biggest group of IDENTICAL runtime RenderTextures (same W×H + format, NOT project assets) alive at once.
        /// Engine/editor RTs (camera attachments, Game/Scene view) are unique or few, so the "≥8 identical, ≥8 MB total" threshold naturally excludes them.
        /// Signature: repeated new RenderTexture(...) without Release(). Returns null if no group crosses the threshold.
        /// </summary>
        private static SceneBatchingSnapshot.RtLeakGroup CaptureRtLeak()
        {
            try
            {
                var groups = new Dictionary<(int w, int h, int fmt), RtGroupAcc>();
                foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
                {
                    if (rt == null) continue;
                    if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(rt))) continue; // imported RT asset — not a runtime leak
                    if (IsEditorInternalGfxName(rt.name)) continue; // editor window / view / gizmo render targets — not a game leak
                    var key = (rt.width, rt.height, (int)rt.format);
                    if (!groups.TryGetValue(key, out var g))
                    {
                        g = new RtGroupAcc { Width = rt.width, Height = rt.height, Format = rt.format.ToString(), Examples = new List<Object>() };
                        groups[key] = g;
                    }
                    g.Count++;
                    g.TotalBytes += Profiler.GetRuntimeMemorySizeLong(rt);
                    if (g.Examples.Count < 12) g.Examples.Add(rt);
                }

                RtGroupAcc best = null;
                foreach (var g in groups.Values)
                    if (g.Count >= 8 && g.TotalBytes >= 8L * 1024 * 1024 && (best == null || g.TotalBytes > best.TotalBytes))
                        best = g;
                return best == null ? null
                    : new SceneBatchingSnapshot.RtLeakGroup(best.Width, best.Height, best.Format, best.Count, best.TotalBytes, best.Examples);
            }
            catch { return null; }
        }

        private sealed class RtGroupAcc { public int Width, Height, Count; public string Format; public long TotalBytes; public List<Object> Examples; }

        /// <summary>
        /// Largest loaded graphics assets by runtime memory (textures incl. RenderTextures, and meshes) — for RUN.MEM004 VRAM localization.
        /// FindObjectsOfTypeAll includes editor/hidden objects, but sorting by GetRuntimeMemorySizeLong surfaces the real big consumers (editor UI textures are tiny).
        /// O(loaded assets), a few ms; called once from Capture(). Any failure degrades to an empty list.
        /// </summary>
        private static List<AssetMemStat> CaptureTopMemoryAssets()
        {
            var list = new List<AssetMemStat>();
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
                {
                    if (t == null) continue;
                    if (IsEditorInternalGfxName(t.name)) continue; // editor window / view / gizmo render targets — editor-only, not the game's assets
                    long bytes = Profiler.GetRuntimeMemorySizeLong(t);
                    if (bytes <= 0) continue;
                    list.Add(new AssetMemStat(string.IsNullOrEmpty(t.name) ? "(unnamed)" : t.name, t is RenderTexture ? "RenderTexture" : "Texture", bytes, t));
                }
                foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
                {
                    if (m == null) continue;
                    long bytes = Profiler.GetRuntimeMemorySizeLong(m);
                    if (bytes <= 0) continue;
                    list.Add(new AssetMemStat(string.IsNullOrEmpty(m.name) ? "(unnamed)" : m.name, "Mesh", bytes, m));
                }
                list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                if (list.Count > 6) list.RemoveRange(6, list.Count - 6); // keep top 6 — matches the count RUN.MEM004 lists, so Locate selects exactly the shown assets
            }
            catch { list.Clear(); }
            return list;
        }

        private sealed class MeshAcc
        {
            public string Name;
            public long PerInstanceTris;
            public int Count;
            public List<GameObject> Examples;
        }

        /// <summary>Upper bound on how many GameObjects RUN.GPU002's Locate selects for the heaviest mesh. High enough to cover normal instance counts (e.g. a few hundred foliage), capped so a pathological scene (tens of thousands) can't stall the editor selecting them all.</summary>
        private const int MaxTopMeshExamples = 512;

        /// <summary>Whether a graphics object's name marks it as an editor-only render target/texture (Game/Scene view, editor windows, gizmos, handles) — these consume VRAM in the editor but are never shipped, so they shouldn't be attributed to the game.</summary>
        private static bool IsEditorInternalGfxName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("GUIView", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("GameView", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("SceneView", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Scene RenderTexture", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Gizmo", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Handles", System.StringComparison.OrdinalIgnoreCase) >= 0;
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

        /// <summary>Selects the GameObjects using the mesh at <paramref name="rank"/> (0 = heaviest), filtering out already-destroyed ones. Intended for RUN.GPU002's per-mesh Locate — each rank reveals its own group.</summary>
        public void SelectMeshExamples(int rank)
        {
            if (!HasMeshExamples(rank)) return;
            var alive = new List<Object>();
            foreach (var go in TopMeshExamplesByRank[rank])
                if (go != null) alive.Add(go);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }

        /// <summary>A memory asset is "locatable" only if it's a project asset with an AssetDatabase path — those reveal in the Project window / import settings.
        /// Runtime render targets (deferred G-buffer, temp pools, camera targets) have no asset path and aren't GameObjects, so putting them in Selection
        /// produces no visible effect — offering a Locate button for them is a dead button. Gate both the button and the selection on this.</summary>
        private static bool IsLocatable(Object asset) =>
            asset != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset));

        /// <summary>True when at least one biggest-memory asset is a locatable project asset (so Locate does something visible). Runtime RTs alone → false → no button.</summary>
        public bool HasTopMemoryAssets
        {
            get
            {
                foreach (var a in TopMemoryAssets) if (IsLocatable(a.Asset)) return true;
                return false;
            }
        }

        /// <summary>Selects the biggest-memory project assets (textures/meshes) so they reveal in the Project window / import settings. Runtime render targets are skipped — they have no location to reveal. Intended for RUN.MEM004's Locate.</summary>
        public void SelectTopMemoryAssets()
        {
            var alive = new List<Object>();
            foreach (var a in TopMemoryAssets)
                if (IsLocatable(a.Asset)) alive.Add(a.Asset);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }

        /// <summary>True when the suspected RT-leak group has at least one live RenderTexture to Locate.</summary>
        public bool HasRtLeakExamples
        {
            get
            {
                if (SuspectRtLeak == null) return false;
                foreach (var o in SuspectRtLeak.Examples) if (o != null) return true;
                return false;
            }
        }

        /// <summary>Selects the leaked RenderTextures (filtering out destroyed ones). Intended for RUN.MEM005's Locate.</summary>
        public void SelectRtLeakExamples()
        {
            if (SuspectRtLeak == null) return;
            var alive = new List<Object>();
            foreach (var o in SuspectRtLeak.Examples) if (o != null) alive.Add(o);
            if (alive.Count > 0) Selection.objects = alive.ToArray();
        }
    }
}
