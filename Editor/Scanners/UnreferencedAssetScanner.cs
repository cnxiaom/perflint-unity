using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P0 asset domain: assets that may not be referenced in the build.
    ///   ASSET.UNREF001 — not referenced by any enabled Build scene, Resources asset, ProjectSettings entry,
    ///   AssetBundle assignment or Addressables entry.
    ///
    /// The remaining false-positive sources (code string paths / reflection-based dynamic loading) are invisible
    /// to static analysis, so all findings are marked Info, carry a risk notice, and offer no auto-fix (deleting
    /// assets is high-risk). Editor/Resources/StreamingAssets/Plugins and script/config/scene-type files are
    /// excluded to further reduce false positives.
    /// </summary>
    public sealed class UnreferencedAssetScanner : IScanner
    {
        public string Name => "Unreferenced Assets (build)";
        public Domain Domain => Domain.Assets;

        /// <summary>
        /// Supplies the asset paths that are Addressables entries, injected by the Addressables bridge at domain
        /// load (null when the package isn't installed — in which case there are no entries to miss). Addressable
        /// assets ship in the build without appearing in any scene/Resources dependency graph, and so does
        /// everything they depend on, which is why they are ROOTS here and not merely an exclusion filter:
        /// a material used only by an addressable prefab is reachable in the build.
        /// </summary>
        public static Func<IEnumerable<string>> AddressableRootsHook;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // hasScenes: whether the build has an actual scene graph to walk. The other root groups must NOT, on
            // their own, make us believe a scene graph exists — otherwise a project with zero build scenes would
            // silently run the (meaningless) unreferenced check.
            var buildScenes = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled && !string.IsNullOrEmpty(s.path)) buildScenes.Add(s.path);
            bool hasScenes = buildScenes.Count > 0;

            if (!hasScenes)
            {
                yield return new Finding(
                    ruleId: "ASSET.UNREF001",
                    domain: Domain.Assets,
                    severity: Severity.Info,
                    title: L.Tr("No build scenes configured; unreferenced analysis skipped", "未配置 Build 场景，跳过未引用分析"),
                    detail: L.Tr("Scenes In Build in Build Settings is empty, so there's no way to tell which assets enter the build. The unreferenced check was skipped. Configure scenes and rescan to enable it.",
                            "Build Settings 的 Scenes In Build 为空，无法判定哪些资源进入构建，已跳过未引用检测。配置后重扫即可启用此项。"),
                    targetPath: null);
                yield break;
            }

            // Roots beyond the scene graph, gathered only once we know there's a scene graph worth analyzing:
            //   • everything under Resources/ (always reachable at runtime)
            //   • assets referenced from ProjectSettings/ (preloaded assets, always-included shaders, SRP asset,
            //     Input System project-wide actions, …) — invisible to GetDependencies but shipped
            //   • assets assigned to an AssetBundle, and everything under an Addressables entry
            var all = AssetDatabase.GetAllAssetPaths();
            var roots = BuildRoots(buildScenes, all, AssetDatabase.IsValidFolder,
                                   GatherProjectSettingsRoots(), GatherAssetBundleRoots(), GatherAddressableRoots());

            context.ReportProgress(Name, 0.2f);
            var referenced = new HashSet<string>(
                AssetDatabase.GetDependencies(roots.ToArray(), true), StringComparer.Ordinal);

            // Only claim we checked Addressables if we actually consulted it — a null hook means the package
            // isn't installed (so there are no entries), not that we skipped a channel we should have read.
            string addressableChecked = AddressableRootsHook != null
                ? L.Tr(", Addressables entry", "、Addressables 条目")
                : "";

            for (int i = 0; i < all.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, 0.2f + 0.8f * i / Math.Max(1, all.Length));

                string p = all[i];
                if (!p.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(p)) continue;
                if (referenced.Contains(p)) continue;
                if (IsExcluded(p)) continue;

                yield return new Finding(
                    ruleId: "ASSET.UNREF001",
                    domain: Domain.Assets,
                    severity: Severity.Info,
                    title: L.Tr("Asset may not be referenced by the build", "资源可能未被构建引用"),
                    detail: L.Tr($"Not referenced by any enabled build scene, Resources asset, ProjectSettings entry{addressableChecked} or AssetBundle assignment; it may be dead weight in the build. " +
                            "⚠ Note: assets loaded by a code string path or by reflection can't be seen by static analysis and are reported here as false positives, so confirm before deleting.",
                            $"未被任何启用的 Build 场景、Resources 资源、ProjectSettings 条目{addressableChecked}或 AssetBundle 指派引用，可能是死资源、白占包体。" +
                            "⚠ 注意：经代码字符串路径或反射动态加载的资源，静态分析看不见，会在这里被误报，删除前务必确认。"),
                    targetPath: p,
                    ping: () => ScannerUtil.PingAsset(p));
            }
        }

        /// <summary>
        /// Pure helper (unit-tested): merges every reference-root group into one deduplicated list — the build
        /// scenes, every non-folder asset under a Resources/ folder, and the caller-supplied extra groups.
        /// <paramref name="isFolder"/> is injected so the merge can be tested without an AssetDatabase.
        /// </summary>
        internal static List<string> BuildRoots(
            IEnumerable<string> buildScenes,
            IEnumerable<string> allAssetPaths,
            Func<string, bool> isFolder,
            params IEnumerable<string>[] extraRoots)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Action<string> add = p =>
            {
                if (!string.IsNullOrEmpty(p) && seen.Add(p)) roots.Add(p);
            };

            foreach (var s in buildScenes) add(s);

            foreach (var p in allAssetPaths)
            {
                if (p == null || !p.StartsWith("Assets/")) continue;
                if (isFolder != null && isFolder(p)) continue;
                if (p.Replace('\\', '/').Contains("/Resources/")) add(p);
            }

            if (extraRoots != null)
                foreach (var group in extraRoots)
                    if (group != null)
                        foreach (var p in group) add(p);

            return roots;
        }

        /// <summary>
        /// Asset paths assigned to an AssetBundle. Such an asset ships in that bundle whether or not any scene
        /// references it, so it — and its dependency closure — must not be reported as unreferenced.
        /// Pure built-in API: no optional package required.
        /// </summary>
        private static IEnumerable<string> GatherAssetBundleRoots()
        {
            foreach (var bundle in AssetDatabase.GetAllAssetBundleNames())
                foreach (var p in AssetDatabase.GetAssetPathsFromAssetBundle(bundle))
                    yield return p;
        }

        /// <summary>
        /// Asset paths covered by <see cref="AddressableRootsHook"/>. A folder entry makes everything inside it
        /// addressable, so folder paths are expanded — GetDependencies on a folder yields nothing.
        /// </summary>
        private static IEnumerable<string> GatherAddressableRoots()
        {
            var hook = AddressableRootsHook;
            if (hook == null) yield break;

            List<string> entries = null;
            try
            {
                var supplied = hook();
                if (supplied != null) entries = new List<string>(supplied);
            }
            catch { /* a broken probe must not take the whole scan down */ }
            if (entries == null) yield break;

            foreach (var p in entries)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!AssetDatabase.IsValidFolder(p)) { yield return p; continue; }

                foreach (var guid in AssetDatabase.FindAssets("", new[] { p }))
                {
                    string child = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(child) && !AssetDatabase.IsValidFolder(child))
                        yield return child;
                }
            }
        }

        /// <summary>
        /// Resolves the Assets/ asset paths referenced by any text file under the project's ProjectSettings/ folder
        /// (preloaded assets, GraphicsSettings always-included shaders, QualitySettings SRP assets, Input System
        /// project-wide actions, …). These enter the build without appearing in any scene/Resources dependency graph.
        /// Guids that don't resolve to an Assets/ path (built-in/package resources) are dropped.
        /// </summary>
        private static IEnumerable<string> GatherProjectSettingsRoots()
        {
            // Application.dataPath is "<project>/Assets"; ProjectSettings is its sibling.
            string projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot)) yield break;
            string dir = Path.Combine(projectRoot, "ProjectSettings");
            if (!Directory.Exists(dir)) yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(dir, "*.asset", SearchOption.TopDirectoryOnly))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }

                foreach (var guid in ExtractGuids(text))
                {
                    if (!seen.Add(guid)) continue;
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(p) && p.StartsWith("Assets/"))
                        yield return p;
                }
            }
        }

        private static readonly Regex GuidRegex = new Regex(
            @"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

        /// <summary>
        /// Pure helper (unit-tested): extracts every <c>guid: &lt;32-hex&gt;</c> token from a YAML settings blob.
        /// The all-zero guid (Unity's "no reference" sentinel) is filtered out.
        /// </summary>
        public static IEnumerable<string> ExtractGuids(string yaml)
        {
            if (string.IsNullOrEmpty(yaml)) yield break;
            foreach (Match m in GuidRegex.Matches(yaml))
            {
                string g = m.Groups[1].Value.ToLowerInvariant();
                if (g != "00000000000000000000000000000000")
                    yield return g;
            }
        }

        /// <summary>
        /// Pure helper (unit-tested): paths that must never be reported, either because they don't enter the
        /// build (Editor), always do (Resources/StreamingAssets), or have a false-positive rate too high to be
        /// worth reporting (scripts, configs, ScriptableObjects — mostly loaded from code).
        /// </summary>
        internal static bool IsExcluded(string path)
        {
            string norm = path.Replace('\\', '/');
            if (norm.Contains("/Editor/")) return true;          // not included in the build
            if (norm.Contains("/Resources/")) return true;        // always included in the build
            if (norm.Contains("/StreamingAssets/")) return true;  // packaged as-is
            if (norm.Contains("/Plugins/")) return true;

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cs":
                case ".asmdef":
                case ".asmref":
                case ".meta":
                case ".dll":
                case ".md":
                case ".txt":
                case ".json":
                case ".xml":
                case ".unity":   // scenes are a separate case
                case ".asset":   // ScriptableObjects are mostly referenced from code, high false-positive rate, excluded for now
                    return true;
                default:
                    return false;
            }
        }
    }
}
