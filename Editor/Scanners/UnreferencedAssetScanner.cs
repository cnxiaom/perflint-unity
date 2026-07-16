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
    ///   ASSET.UNREF001 — not referenced by any enabled Build scene or Resources asset.
    ///
    /// This rule is inherently prone to false positives (Addressables / AssetBundle /
    /// code string paths / reflection-based dynamic loading are all invisible to static analysis),
    /// so all findings are marked Info, carry a strong risk notice, and offer no auto-fix
    /// (deleting assets is high-risk). Editor/Resources/StreamingAssets/Plugins and
    /// script/config/scene-type files are excluded to further reduce false positives.
    /// </summary>
    public sealed class UnreferencedAssetScanner : IScanner
    {
        public string Name => "Unreferenced Assets (build)";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // Reference roots: enabled Build scenes + all assets under Resources (always reachable at runtime)
            // + assets referenced from ProjectSettings/ (preloaded assets, always-included shaders, SRP asset,
            // Input System project-wide actions, …). The last group is invisible to GetDependencies but ships in
            // the build, so omitting it produces false positives (e.g. InputSystem_Actions.inputactions).
            var roots = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled && !string.IsNullOrEmpty(s.path)) roots.Add(s.path);

            // hasScenes: whether the build has an actual scene graph to walk. ProjectSettings-referenced assets
            // are added regardless but must NOT, on their own, make us believe a scene graph exists — otherwise a
            // project with zero build scenes would silently run the (meaningless) unreferenced check.
            bool hasScenes = roots.Count > 0;

            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(p)) continue;
                if (p.Replace('\\', '/').Contains("/Resources/")) roots.Add(p);
            }

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

            // ProjectSettings-referenced assets (preloaded/graphics/quality/input) enter the build outside the
            // scene graph — add them only once we know there's a scene graph worth analyzing.
            roots.AddRange(GatherProjectSettingsRoots());

            context.ReportProgress(Name, 0.2f);
            var referenced = new HashSet<string>(
                AssetDatabase.GetDependencies(roots.ToArray(), true), StringComparer.Ordinal);

            var all = AssetDatabase.GetAllAssetPaths();
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
                    detail: L.Tr("Not referenced by any enabled build scene or Resources asset; it may be dead weight in the build. " +
                            "⚠️ Note: assets loaded via Addressables / AssetBundle / code string paths / reflection are reported as false positives, so confirm before deleting.",
                            "未被任何启用的 Build 场景或 Resources 资源引用，可能是死资源、白占包体。" +
                            "⚠️ 注意：经 Addressables / AssetBundle / 代码字符串路径 / 反射动态加载的资源会被误报，删除前务必确认。"),
                    targetPath: p,
                    ping: () => ScannerUtil.PingAsset(p));
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

        private static bool IsExcluded(string path)
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
