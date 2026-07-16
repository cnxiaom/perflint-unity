using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfLint.Scanners
{
    /// <summary>
    /// MIG.MeshLodConflict — a mesh that carries built-in Mesh LOD levels (Unity 6.2+, Mesh.lodCount > 1) AND is driven
    /// by a LOD Group. Both systems reduce mesh detail by distance; running them together double-processes LOD selection
    /// — a negative optimization Unity explicitly warns against (灵界篇 番外1). Often slips in because a DCC-exported
    /// model already ships multi-level LOD meshes while the artist also adds a LOD Group.
    ///
    /// Gated by reflection on the real Mesh.lodCount property (verified public on 6000.5), so it stays silent on editors
    /// without Mesh LOD (&lt; 6.2) — existence check, never a version-number guess. Report-only, Warning: the fix is
    /// structural (strip the extra LOD levels in a DCC tool, or remove the LOD Group), so there is no one-click.
    ///
    /// Findings are DEDUPED BY THE CONFLICTING MESH (the actual fix unit): a grass prefab placed 17× in a scene is one
    /// finding "×17", not 17 identical rows, and Locate jumps to the mesh asset. Scope: loaded scenes (live objects) +
    /// prefab assets whose (text) YAML mentions a LOD Group (a cheap prune before loading). Closed scenes and
    /// binary-serialized prefabs are not covered — a documented limitation shared with other scene-walking scanners.
    /// </summary>
    public sealed class MeshLodConflictScanner : IScanner, ISceneScoped
    {
        public string Name => "Mesh LOD";
        public Domain Domain => Domain.Migration;

        private sealed class ConflictAgg
        {
            public string MeshName;
            public string MeshPath;   // asset path of the conflicting mesh, or null (procedural mesh)
            public int Count;         // how many LOD Groups drive this mesh
            public string FirstLabel; // a representative location (scene > object, or prefab path)
            public GameObject FirstGo;
            public string FirstPrefab;
        }

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            if (!MeshLodApiAvailable) yield break; // Mesh LOD is Unity 6.2+; on older editors there is nothing to check.

            // Keyed by the Mesh object itself (a shared asset instance is the same reference across every scene/prefab
            // that uses it → natural dedup). Deliberately NOT mesh.GetInstanceID(): that is error-level obsolete
            // (CS0619) on Unity 6.2+ and would break compilation there — the very API PerfLint's own MIG.GetInstanceID
            // flags. Reference-keying needs no id at all and compiles on every supported editor.
            var byMesh = new Dictionary<Mesh, ConflictAgg>();

            // ── Loaded scenes (live objects) ──
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    Collect(byMesh, root, sceneLabel: scene.path, prefabPath: null);
                }
            }

            // ── Prefab assets (prune by text mention of a LOD Group, then load) ──
            var guids = AssetDatabase.FindAssets("t:GameObject", new[] { "Assets" });
            int loads = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / Math.Max(1, guids.Length));

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (ScannerUtil.IsPerfLintOwnAsset(path)) continue;
                string yaml = SafeRead(path);
                if (yaml == null || !YamlMentionsLodGroup(yaml)) continue; // cheap prune: no LOD Group → cannot conflict

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) Collect(byMesh, go, sceneLabel: null, prefabPath: path);
                loads = ScannerUtil.ThrottleReclaim(loads);
            }

            // ── Emit one finding per unique conflicting mesh (the fix unit) ──
            foreach (var agg in byMesh.Values)
                yield return BuildFinding(agg);
        }

        /// <summary>Record every LOD Group whose actual LOD renderers use a Mesh-LOD mesh, aggregated by that mesh.</summary>
        private static void Collect(Dictionary<Mesh, ConflictAgg> byMesh, GameObject root, string sceneLabel, string prefabPath)
        {
            if (root == null) return;
            foreach (var lg in root.GetComponentsInChildren<LODGroup>(true))
            {
                if (lg == null) continue;
                Mesh mesh = FindMeshLodMesh(lg);
                if (mesh == null) continue;

                if (!byMesh.TryGetValue(mesh, out var agg))
                {
                    string label = prefabPath ?? (string.IsNullOrEmpty(sceneLabel) ? lg.gameObject.name : sceneLabel + " > " + lg.gameObject.name);
                    agg = new ConflictAgg
                    {
                        MeshName = mesh.name,
                        MeshPath = AssetDatabase.GetAssetPath(mesh),
                        FirstLabel = label,
                        FirstGo = prefabPath == null ? lg.gameObject : null,
                        FirstPrefab = prefabPath,
                    };
                    byMesh[mesh] = agg;
                }
                agg.Count++;
            }
        }

        private static Finding BuildFinding(ConflictAgg a)
        {
            string meshName = a.MeshName;
            string suffix = a.Count > 1 ? $" (×{a.Count})" : "";
            int count = a.Count;
            string firstLabel = a.FirstLabel;
            string meshPath = a.MeshPath;
            var go = a.FirstGo;
            var prefab = a.FirstPrefab;
            return new Finding(
                ruleId: "MIG.MeshLodConflict",
                domain: Domain.Migration,
                severity: Severity.Warning,
                title: L.Tr($"Mesh LOD mixed with LOD Group: {meshName}{suffix}", $"Mesh LOD 与 LOD Group 混用：{meshName}{suffix}"),
                groupTitle: L.Tr("Mesh LOD mixed with LOD Group", "Mesh LOD 与 LOD Group 混用"),
                detail: L.Tr(
                    $"The mesh '{meshName}' carries built-in Mesh LOD levels (Mesh.lodCount > 1, a Unity 6.2+ feature) AND is driven by a LOD Group in " +
                    $"{count} place(s) across your scenes/prefabs (e.g. {firstLabel}). Both reduce mesh detail by distance, so using them together " +
                    "double-processes LOD selection — a negative optimization Unity explicitly warns against. Fix the mesh once — strip its extra LOD levels " +
                    "in your DCC tool before import — to resolve every occurrence, OR remove the LOD Groups and rely on Mesh LOD (switch back only if Mesh LOD shows visual errors).",
                    $"网格「{meshName}」带内置 Mesh LOD 层级（Mesh.lodCount > 1，Unity 6.2+ 特性），同时又被 LOD Group 驱动，" +
                    $"在场景/预制体里共 {count} 处（例：{firstLabel}）。两者都按距离降网格细节，一起用会对 LOD 选择重复处理——Unity 明确警告这是负优化。" +
                    "改一次网格即可解决所有处——在 DCC 工具里把它多余的 LOD 层删掉再导入；或移除这些 LOD Group、改用 Mesh LOD" +
                    "（仅当 Mesh LOD 出现视觉错误时再退回）。"),
                targetPath: !string.IsNullOrEmpty(meshPath) ? meshPath : firstLabel,
                ping: () => PingConflict(meshPath, go, prefab));
        }

        /// <summary>Returns the first mesh among the LOD Group's actual LOD renderers that carries Mesh LOD levels; null if none.</summary>
        private static Mesh FindMeshLodMesh(LODGroup lg)
        {
            LOD[] lods;
            try { lods = lg.GetLODs(); } catch { return null; }
            if (lods == null) return null;
            foreach (var lod in lods)
            {
                if (lod.renderers == null) continue;
                foreach (var r in lod.renderers)
                {
                    if (r == null) continue;
                    var mesh = GetMesh(r);
                    if (mesh != null && ReadLodCount(mesh) > 1) return mesh;
                }
            }
            return null;
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        // Locate points at the conflicting mesh asset (the fix unit); falls back to the prefab, then the scene object.
        private static void PingConflict(string meshPath, GameObject go, string prefabPath)
        {
            if (!string.IsNullOrEmpty(meshPath)) { ScannerUtil.PingAsset(meshPath); return; }
            if (!string.IsNullOrEmpty(prefabPath)) { ScannerUtil.PingAsset(prefabPath); return; }
            if (go != null) { Selection.activeGameObject = go; EditorGUIUtility.PingObject(go); }
        }

        // ── Reflection: Mesh.lodCount (public int, Unity 6.2+). Cached; null on older editors → the rule is inert. ──
        private static PropertyInfo _lodCountProp;
        private static bool _lodCountResolved;

        /// <summary>Whether this editor exposes Mesh LOD (Mesh.lodCount). False on &lt; 6.2 → the scanner emits nothing.</summary>
        internal static bool MeshLodApiAvailable => LodCountProp() != null;

        private static PropertyInfo LodCountProp()
        {
            if (!_lodCountResolved)
            {
                _lodCountResolved = true;
                try { _lodCountProp = typeof(Mesh).GetProperty("lodCount", BindingFlags.Public | BindingFlags.Instance); }
                catch { _lodCountProp = null; }
            }
            return _lodCountProp;
        }

        /// <summary>Reads Mesh.lodCount via reflection; 0 when the API is absent or the read fails (never throws).</summary>
        internal static int ReadLodCount(Mesh mesh)
        {
            var p = LodCountProp();
            if (p == null || mesh == null) return 0;
            try { object v = p.GetValue(mesh); return v is int i ? i : 0; }
            catch { return 0; }
        }

        /// <summary>Pure logic: does the (text) prefab/scene YAML declare a LOD Group component (column-0 key)?</summary>
        internal static bool YamlMentionsLodGroup(string yaml)
            => !string.IsNullOrEmpty(yaml) && Regex.IsMatch(yaml, @"(?m)^LODGroup:");

        private static string SafeRead(string relPath)
        {
            try
            {
                string full = ScannerUtil.ToPhysicalFullPath(relPath);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch { return null; }
        }
    }
}
