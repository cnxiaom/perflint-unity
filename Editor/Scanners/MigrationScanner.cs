using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
// UnityEditor also has an old PackageInfo (Asset Store) that is ambiguous with PackageManager.PackageInfo; pin it with an alias.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Migration diagnostics (lightweight edition, triggered by Unity upgrade pulses):
    ///   MIG.* — deprecated/removed API usage (curated high-confidence list, pinpointed to line + replacement advice).
    ///   MIG.PreviewPackage / MIG.LegacyPackage — preview/experimental packages and deprecated/merged packages in manifest.json.
    ///   MIG.PackageUnityIncompat — an installed package declares a minimum Unity version higher than the current editor (authoritative, zero false positives).
    ///   MIG.InputBackendBoth — both old and new input backends enabled simultaneously (project-level, Info).
    ///   MIG.LegacyInputApi — old UnityEngine.Input still used when the backend is set to "New Input System only" (fails at runtime, Warning).
    ///   MIG.ApiCompatLevel — Player Settings' Api Compatibility Level is an obsolete (.NET 2.0) or .NET Framework level (project-level; Warning/Info, report-only).
    ///   MIG.AsmdefBrokenRef — an .asmdef under Assets/ references assemblies that don't resolve (likely renamed/removed during a migration; project-level, Warning, report-only).
    /// Render pipeline migration (Built-in→URP/HDRP material conversion) is heavier work; deferred to V2.
    /// </summary>
    public sealed class MigrationScanner : IScanner, IFileScanner
    {
        public string Name => "Migration";
        public Domain Domain => Domain.Migration;

        private sealed class ApiRule
        {
            public Regex Pattern;
            public string RuleId;
            // Lazy (Func, not string): ApiRules is a static readonly array initialized once at type load, so eagerly
            // calling L.Tr here would bake whatever language was current at that moment and never re-evaluate on a
            // language switch (the "Chinese leaks into the EN UI" bug). Evaluate per-finding instead — see ScanSource.
            public System.Func<string> Title;
            public System.Func<string> Detail;
            public bool RequiresUnity2023_1; // Only report when the current Unity is ≥2023.1/6 (the version where this API is truly deprecated)
            // Whether AI one-click migration is permitted. Only applies to "rename-style" cases (FindObjectOfType→FindAnyObjectByType, LoadLevel→LoadScene):
            // replacing the flagged fragment is sufficient. Structural rewrites (WWW→UnityWebRequest, GUIText→UGUI, Legacy particles) set this to false —
            // they change the entire usage block/scope; a local fragment replacement cannot reach all downstream usages of the method, so AI would corrupt the code. Just report + locate, leave to the developer.
            public bool AllowAiFix = true;
        }

        private static readonly ApiRule[] ApiRules =
        {
            new ApiRule {
                Pattern = new Regex(@"\bFindObjectsOfType\b", RegexOptions.Compiled),
                RuleId = "MIG.FindObjectsOfType", Title = () => L.Tr("Deprecated API: FindObjectsOfType", "废弃 API：FindObjectsOfType"),
                Detail = () => L.Tr("FindObjectsOfType is deprecated in Unity 2023.1+/6. Use FindObjectsByType(FindObjectsSortMode.None) (unsorted by default, and faster).",
                              "FindObjectsOfType 在 Unity 2023.1+/6 已弃用。改用 FindObjectsByType(FindObjectsSortMode.None)（默认不排序，更快）。"),
                RequiresUnity2023_1 = true
            },
            new ApiRule {
                Pattern = new Regex(@"\bFindObjectOfType\b", RegexOptions.Compiled),
                RuleId = "MIG.FindObjectOfType", Title = () => L.Tr("Deprecated API: FindObjectOfType", "废弃 API：FindObjectOfType"),
                Detail = () => L.Tr("FindObjectOfType is deprecated in Unity 2023.1+/6. Use FindAnyObjectByType (faster, order not guaranteed) or FindFirstObjectByType (when you need determinism).",
                              "FindObjectOfType 在 Unity 2023.1+/6 已弃用。改用 FindAnyObjectByType（更快、不保证顺序）或 FindFirstObjectByType（需要确定性时）。"),
                RequiresUnity2023_1 = true
            },
            new ApiRule {
                Pattern = new Regex(@"\bnew\s+WWW\b", RegexOptions.Compiled),
                RuleId = "MIG.WWW", Title = () => L.Tr("Removed API: WWW", "已移除 API：WWW"),
                Detail = () => L.Tr("WWW is deprecated/removed. Use UnityWebRequest (requires using UnityEngine.Networking). This is a structural migration, not a rename: "
                         + "the async model, DownloadHandler, result checks, and variable scoping all differ, so the whole request flow must be rewritten by hand; no one-click fix.",
                         "WWW 已废弃/移除。改用 UnityWebRequest（需 using UnityEngine.Networking）。这是结构性迁移而非改名："
                         + "异步模型、DownloadHandler、result 检查、变量作用域都不同，需人工整体改写该请求流程，不提供一键修复。"),
                AllowAiFix = false
            },
            new ApiRule {
                Pattern = new Regex(@"\bApplication\.LoadLevel", RegexOptions.Compiled),
                RuleId = "MIG.LoadLevel", Title = () => L.Tr("Removed API: Application.LoadLevel", "已移除 API：Application.LoadLevel"),
                Detail = () => L.Tr("Application.LoadLevel/LoadLevelAsync has been removed. Use SceneManager.LoadScene (UnityEngine.SceneManagement).",
                              "Application.LoadLevel/LoadLevelAsync 已移除。改用 SceneManager.LoadScene（UnityEngine.SceneManagement）。")
            },
            new ApiRule {
                Pattern = new Regex(@"\bGUIText\b|\bGUITexture\b", RegexOptions.Compiled),
                RuleId = "MIG.GUIText", Title = () => L.Tr("Removed components: GUIText/GUITexture", "已移除组件：GUIText/GUITexture"),
                Detail = () => L.Tr("GUIText/GUITexture have been removed. Use UGUI (Text/Image) or TextMeshPro. This is a structural replacement (components/prefabs/references all change), so it must be migrated by hand; no one-click fix.",
                              "GUIText/GUITexture 已移除。改用 UGUI（Text/Image）或 TextMeshPro。这是结构性替换（组件/预制体/引用都要换），需人工迁移，不提供一键修复。"),
                AllowAiFix = false
            },
            new ApiRule {
                Pattern = new Regex(@"\bParticleEmitter\b|\bParticleRenderer\b|\bParticleAnimator\b", RegexOptions.Compiled),
                RuleId = "MIG.LegacyParticles", Title = () => L.Tr("Removed: legacy particle components", "已移除：Legacy 粒子组件"),
                Detail = () => L.Tr("Legacy particles (ParticleEmitter/Renderer/Animator) have been removed. Use the Shuriken Particle System. This is a structural replacement, so it must be migrated by hand; no one-click fix.",
                              "Legacy 粒子（ParticleEmitter/Renderer/Animator）已移除。改用 Shuriken Particle System。这是结构性替换，需人工迁移，不提供一键修复。"),
                AllowAiFix = false
            },
        };

        // High-confidence static members of the old UnityEngine.Input — scoped to these members to avoid false positives on user-defined types or fields named Input.
        private static readonly Regex LegacyInputApi = new Regex(
            @"\bInput\.(GetAxisRaw|GetAxis|GetButtonDown|GetButtonUp|GetButton|GetKeyDown|GetKeyUp|GetKey|" +
            @"GetMouseButtonDown|GetMouseButtonUp|GetMouseButton|mousePosition|mouseScrollDelta|" +
            @"touchCount|GetTouch|touches|acceleration|anyKeyDown|anyKey)\b",
            RegexOptions.Compiled);

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // Version-aware: FindObjectOfType and similar are only reported on Unity ≥2023.1/6 (where they are truly deprecated);
            // otherwise they are pure noise for users still on 2021/2022 who are not upgrading.
            bool unity2023_1Plus = IsAtLeast2023_1(Application.unityVersion);

            // Input backend: 0=old Input Manager, 1=new Input System only, 2=Both enabled.
            // The old UnityEngine.Input only truly stops working under "new system only"; that is when MIG.LegacyInputApi is reported.
            int inputBackend = ReadActiveInputHandler();
            bool newInputOnly = inputBackend == 1;

            // ── Deprecated APIs / legacy input APIs in scripts ── (per-file, reusing ScanScript so line-by-line analysis also covers migration rules)
            var guids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, 0.5f * i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!Handles(path)) continue;
                foreach (var f in ScanScript(path, unity2023_1Plus, newInputOnly))
                    yield return f;
            }

            // ── manifest.json package checks ──
            foreach (var f in ScanManifest())
                yield return f;

            // ── Input backend: both enabled (project-level, Info) ──
            if (inputBackend == 2)
            {
                yield return new Finding(
                    ruleId: "MIG.InputBackendBoth",
                    domain: Domain.Migration,
                    severity: Severity.Info,
                    title: L.Tr("Both input backends enabled", "同时启用新旧输入后端"),
                    detail: L.Tr("Active Input Handling is set to \"Both\": the old and new input backends run at the same time, adding memory/initialization overhead " +
                            "and making it ambiguous for the team which one to use. Once migration is done, converge on one (usually the Input System Package).",
                            "Active Input Handling 设为「Both」：新旧两套输入后端同时运行，有额外内存/初始化开销，" +
                            "也容易让团队对该用哪套产生歧义。迁移完成后建议收敛为其一（通常是 Input System Package）。"),
                    targetPath: "ProjectSettings/ProjectSettings.asset");
            }

            // ── API Compatibility Level (project-level migration check) ──
            foreach (var f in CheckApiCompatLevel())
                yield return f;

            // ── asmdef broken references (project-level migration check) ──
            foreach (var f in ScanAsmdefs())
                yield return f;
        }

        /// <summary>The migration script rules cover all .cs files (including the Editor directory: Editor scripts can equally be broken at compile time by removed APIs).</summary>
        public bool Handles(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".cs");

        /// <summary>
        /// Single-file incremental: only recompute this script's deprecated-API / legacy-input-API findings (project-level rules such as manifest and input backend are excluded).
        /// The version and input-backend checks are read inline here — single-file calls are infrequent, so the cost is negligible.
        /// </summary>
        public IEnumerable<Finding> ScanFile(string assetPath, ScanContext context)
        {
            if (!Handles(assetPath)) yield break;
            bool unity2023_1Plus = IsAtLeast2023_1(Application.unityVersion);
            bool newInputOnly = ReadActiveInputHandler() == 1;
            foreach (var f in ScanScript(assetPath, unity2023_1Plus, newInputOnly))
                yield return f;
        }

        /// <summary>Match deprecated APIs and legacy input APIs line by line in a single script. Shared by the full Scan and the single-file ScanFile to guarantee both paths produce consistent results.</summary>
        private IEnumerable<Finding> ScanScript(string path, bool unity2023_1Plus, bool newInputOnly)
        {
            var lines = ReadLines(path);
            if (lines == null) yield break;
            foreach (var f in ScanSource(lines, path, unity2023_1Plus, newInputOnly)) yield return f;
        }

        /// <summary>Pure logic: match deprecated APIs / legacy input APIs against already-loaded source lines (no file I/O, making it easy to verify false positives/negatives in end-to-end unit tests).</summary>
        internal static IEnumerable<Finding> ScanSource(string[] lines, string path, bool unity2023_1Plus, bool newInputOnly)
        {
            for (int ln = 0; ln < lines.Length; ln++)
            {
                string code = StripNonCode(lines[ln]);
                if (code.Trim().Length == 0) continue;
                int line = ln + 1;
                string cap = path;

                foreach (var rule in ApiRules)
                {
                    if (rule.RequiresUnity2023_1 && !unity2023_1Plus) continue;
                    if (!rule.Pattern.IsMatch(code)) continue;
                    yield return new Finding(
                        ruleId: rule.RuleId,
                        domain: Domain.Migration,
                        severity: Severity.Warning,
                        title: rule.Title(),
                        detail: rule.Detail(),
                        targetPath: $"{path}:{line}",
                        ping: () => OpenAt(cap, line),
                        // Rename-style cases carry a code location → eligible for AI Fix; structural migrations carry no codeFile → report + locate only (Locate still works).
                        codeFile: rule.AllowAiFix ? cap : null,
                        codeLine: rule.AllowAiFix ? line : 0);
                }

                // Legacy input APIs break at runtime under "new system only". Report-only (this migration is not a rename, so no AI Fix attached).
                if (newInputOnly && LegacyInputApi.IsMatch(code))
                {
                    yield return new Finding(
                        ruleId: "MIG.LegacyInputApi",
                        domain: Domain.Migration,
                        severity: Severity.Warning,
                        title: L.Tr("Legacy Input API broken under \"Input System (New) only\"", "旧 Input API 在「仅新 Input System」下失效"),
                        detail: L.Tr("The project's Active Input Handling is set to \"Input System Package (New)\", but this code still uses the old " +
                                "UnityEngine.Input (GetAxis/GetKey/mousePosition, etc.). At runtime these calls stop working / throw. " +
                                "Switch to the new system (InputAction / Keyboard.current / Mouse.current, etc.), or set Active Input Handling to \"Both\" as a transition.",
                                "项目的 Active Input Handling 设为「Input System Package (New)」，但此处仍用旧 " +
                                "UnityEngine.Input（GetAxis/GetKey/mousePosition 等）。运行时这些调用会失效/抛异常。" +
                                "改用新系统（InputAction / Keyboard.current / Mouse.current 等），或把 Active Input Handling 改为「Both」过渡。"),
                        targetPath: $"{path}:{line}",
                        ping: () => OpenAt(cap, line)); // Deliberately no codeFile: input migration is not a rename, so AI Fix would get it wrong
                }
            }
        }

        private static IEnumerable<Finding> ScanManifest()
        {
            string text = SafeRead("Packages/manifest.json");
            if (text == null) yield break;

            bool isUnity6 = (Application.unityVersion ?? "").StartsWith("6000");

            foreach (Match m in Regex.Matches(text, "\"(com\\.[a-z0-9_\\-\\.]+)\"\\s*:\\s*\"([^\"]+)\""))
            {
                string pkg = m.Groups[1].Value;
                string ver = m.Groups[2].Value;

                // Package version vs target Unity compatibility (plan Migration P0): use Unity's already-resolved PackageInfo
                // to read the package's declared minimum unity version; higher than the current editor means incompatible. Checked for all sources (including git/local).
                var incompat = CheckPackageUnityCompat(pkg);
                if (incompat != null) yield return incompat;

                if (ver.StartsWith("file:") || ver.StartsWith("http") || ver.StartsWith("git")) continue;

                string lver = ver.ToLowerInvariant();
                if (lver.Contains("preview") || lver.Contains("-exp") || lver.Contains("-pre"))
                {
                    yield return new Finding(
                        ruleId: "MIG.PreviewPackage",
                        domain: Domain.Migration,
                        severity: Severity.Info,
                        title: L.Tr("Preview/experimental package", "预览/实验性包"),
                        detail: L.Tr($"{pkg}@{ver} is a preview/experimental version whose API is volatile and prone to breaking when you upgrade Unity. Move to a release (Verified) version where possible.",
                                     $"{pkg}@{ver} 为预览/实验性版本，升级 Unity 时 API 易变、易破坏。尽量改用正式版（Verified）。"),
                        targetPath: "Packages/manifest.json");
                }

                if (pkg == "com.unity.postprocessing")
                {
                    yield return new Finding(
                        ruleId: "MIG.LegacyPackage",
                        domain: Domain.Migration,
                        severity: Severity.Info,
                        title: L.Tr("Post Processing v2 package", "Post Processing v2 包"),
                        detail: L.Tr($"{pkg}@{ver}: URP/HDRP have built-in post-processing, so PPv2 is redundant and conflicts with SRP post-processing. For SRP projects, migrate to the pipeline's own post-processing and remove this package.",
                                     $"{pkg}@{ver}：URP/HDRP 已内置后处理，PPv2 多余且与 SRP 后处理冲突。SRP 项目建议迁移到管线自带后处理后移除此包。"),
                        targetPath: "Packages/manifest.json");
                }
                else if (isUnity6 && pkg == "com.unity.textmeshpro")
                {
                    yield return new Finding(
                        ruleId: "MIG.LegacyPackage",
                        domain: Domain.Migration,
                        severity: Severity.Info,
                        title: L.Tr("TextMeshPro standalone package (merged in Unity 6)", "TextMeshPro 独立包（Unity 6 已合并）"),
                        detail: L.Tr($"{pkg}@{ver}: Unity 6 merged TextMeshPro into com.unity.ugui, so the standalone package can be removed (mind the namespace/reference adjustments).",
                                     $"{pkg}@{ver}：Unity 6 已把 TextMeshPro 合并进 com.unity.ugui，独立包可移除（注意命名空间/引用调整）。"),
                        targetPath: "Packages/manifest.json");
                }
            }
        }

        /// <summary>
        /// If the minimum Unity version declared by <paramref name="pkg"/> is higher than the current editor, return an incompatibility finding; otherwise null.
        /// Uses Unity's already-resolved PackageInfo (authoritative: the unity field of the package's own package.json), synchronous and false-positive-free;
        /// reports nothing when it cannot be resolved / the package declares no unity field / it is compatible.
        /// </summary>
        private static Finding CheckPackageUnityCompat(string pkg)
        {
            PackageInfo info;
            try { info = PackageInfo.FindForAssetPath("Packages/" + pkg); }
            catch { return null; }
            if (info == null || string.IsNullOrEmpty(info.resolvedPath)) return null;

            // PackageInfo does not expose the minimum Unity version — read unity/unityRelease from the package's own package.json (under resolvedPath).
            string pj = SafeRead(Path.Combine(info.resolvedPath, "package.json"));
            if (pj == null) return null;
            var mu = Regex.Match(pj, "\"unity\"\\s*:\\s*\"([^\"]+)\"");
            if (!mu.Success) return null;
            string minUnity = mu.Groups[1].Value;
            var mr = Regex.Match(pj, "\"unityRelease\"\\s*:\\s*\"([^\"]+)\"");
            string minRelease = mr.Success ? mr.Groups[1].Value : "";

            var req = ParseUnityVer(minUnity);
            var cur = ParseUnityVer(Application.unityVersion);
            if (req.major == 0 || cur.major == 0) return null;

            bool incompatible = req.major > cur.major
                                || (req.major == cur.major && req.minor > cur.minor);
            if (!incompatible) return null;

            string reqStr = minUnity + (string.IsNullOrEmpty(minRelease) ? "" : "." + minRelease);
            return new Finding(
                ruleId: "MIG.PackageUnityIncompat",
                domain: Domain.Migration,
                severity: Severity.Warning,
                title: L.Tr("Package requires a newer Unity version", "包要求更高的 Unity 版本"),
                detail: L.Tr($"{pkg}@{info.version} declares a minimum supported Unity {reqStr}, but the current editor is {Application.unityVersion}. " +
                        "The package may not compile or run correctly on the current version - upgrade Unity, or downgrade the package to a version compatible with the current editor.",
                        $"{pkg}@{info.version} 声明最低支持 Unity {reqStr}，但当前编辑器为 {Application.unityVersion}。" +
                        "该包在当前版本可能无法正确编译或运行——升级 Unity，或把该包降到兼容当前版本的版本号。"),
                targetPath: "Packages/manifest.json");
        }

        // ── API Compatibility Level (project-level, report-only) ─────────────────────────────────
        /// <summary>
        /// Reports the project's Api Compatibility Level when it is obsolete (legacy .NET 2.0, removed from modern Unity) or .NET Framework 4.x
        /// (larger builds, not the cross-platform default). The normal .NET Standard family is silent. Report-only — switching the level can break
        /// code that depends on Framework-only APIs, so we never offer a one-click change.
        /// </summary>
        private static IEnumerable<Finding> CheckApiCompatLevel()
        {
            string levelName = null;
            try
            {
                var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
                levelName = PlayerSettings.GetApiCompatibilityLevel(group).ToString();
            }
            catch { levelName = null; }

            var verdict = EvaluateApiCompat(levelName);
            if (verdict == null) yield break;

            bool legacy = verdict.Value.severity == Severity.Warning; // NET_2_0 / Subset branch
            yield return new Finding(
                ruleId: "MIG.ApiCompatLevel",
                domain: Domain.Migration,
                severity: verdict.Value.severity,
                title: legacy
                    ? L.Tr("Obsolete API Compatibility Level", "过时的 API 兼容级别")
                    : L.Tr(".NET Framework API Compatibility Level", ".NET Framework API 兼容级别"),
                detail: legacy
                    ? L.Tr($"Player Settings' Api Compatibility Level is {levelName}, a level removed from modern Unity (a leftover from a much older project). " +
                           "Set it to .NET Standard 2.1 under Edit ▸ Project Settings ▸ Player ▸ Other Settings ▸ Api Compatibility Level; pick .NET Framework only if you rely on Framework-only APIs.",
                           $"Player Settings 的 Api Compatibility Level 为 {levelName}，是现代 Unity 已移除的级别（老项目升级残留）。" +
                           "在 Edit ▸ Project Settings ▸ Player ▸ Other Settings ▸ Api Compatibility Level 设为 .NET Standard 2.1；仅在依赖 Framework-only API 时才选 .NET Framework。")
                    : L.Tr($"Api Compatibility Level is set to .NET Framework ({levelName}): larger builds, and not the cross-platform default. " +
                           "If you don't depend on Framework-only APIs (System.Drawing, some System.Net/serialization surfaces), switching to .NET Standard 2.1 trims the build — verify your code and dependencies still compile first.",
                           $"Api Compatibility Level 设为 .NET Framework（{levelName}）：包体更大、且非跨平台首选。" +
                           "若不依赖 Framework-only API（System.Drawing、部分 System.Net/序列化等），切到 .NET Standard 2.1 可减小包体——切换前请先确认代码与依赖仍能编译。"),
                targetPath: "ProjectSettings/ProjectSettings.asset");
        }

        /// <summary>
        /// Pure decision: map an ApiCompatibilityLevel.ToString() value to (ruleId, severity), or null when it should not be reported.
        /// Compares by STRING (never by enum member) on purpose: NET_2_0 / NET_2_0_Subset were removed from the enum in Unity 6,
        /// so referencing those members by name would fail to compile there. The .NET Standard family and any unknown/future value yield null (no noise).
        /// </summary>
        internal static (string ruleId, Severity severity)? EvaluateApiCompat(string levelName)
        {
            if (string.IsNullOrEmpty(levelName)) return null;
            switch (levelName)
            {
                case "NET_2_0":
                case "NET_2_0_Subset":
                    return ("MIG.ApiCompatLevel", Severity.Warning);
                case "NET_4_6":
                case "NET_Unity_4_8":
                    return ("MIG.ApiCompatLevel", Severity.Info);
                default:
                    return null;
            }
        }

        // ── asmdef broken references (project-level, report-only) ─────────────────────────────────
        /// <summary>
        /// Reports .asmdef files under Assets/ whose "references" point to assemblies that don't resolve (broken after a package/Unity migration,
        /// e.g. com.unity.textmeshpro merging into UGUI in Unity 6). Conservative: only Assets/, only the references field, object-form conditional
        /// references are skipped, and dormant assemblies (not currently compiled — e.g. defineConstraints gating an optional package like
        /// Addressables/URP) are skipped entirely — a healthy project must report zero. Report-only (editing .asmdef JSON is not a single-fragment rewrite).
        /// </summary>
        private static IEnumerable<Finding> ScanAsmdefs()
        {
            string[] guids = null;
            try { guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", new[] { "Assets" }); }
            catch { guids = null; }
            if (guids == null || guids.Length == 0) yield break;

            var asmNames = BuildAsmNameSet();
            System.Func<string, bool> nameResolves = name =>
            {
                try { if (!string.IsNullOrEmpty(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(name))) return true; }
                catch { return true; } // resolution error → do not flag
                return asmNames.Contains(name);
            };
            System.Func<string, bool> guidResolves = guid =>
            {
                try
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    return !string.IsNullOrEmpty(p) && p.EndsWith(".asmdef") && File.Exists(Path.GetFullPath(p));
                }
                catch { return true; }
            };

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".asmdef")) continue;
                string json = SafeRead(path);
                if (json == null) continue;

                // Skip dormant assemblies: an .asmdef excluded from compilation (e.g. unsatisfied
                // defineConstraints gating an optional package such as Addressables/URP) is never compiled,
                // so unresolved references in it cannot fail a build — flagging them is a false positive
                // (PerfLint's own optional-package scanners are exactly this shape). "Not compiled" = its
                // declared name is absent from the live assembly set.
                if (!ShouldCheckAsmdefRefs(json, n => asmNames.Contains(n))) continue;

                var broken = FindBrokenReferences(json, nameResolves, guidResolves);
                if (broken.Count == 0) continue;

                string cap = path;
                string list = string.Join(", ", broken);
                yield return new Finding(
                    ruleId: "MIG.AsmdefBrokenRef",
                    domain: Domain.Migration,
                    severity: Severity.Warning,
                    title: L.Tr("Assembly Definition has unresolved references", "程序集定义存在无法解析的引用"),
                    detail: L.Tr($"{path} references assemblies that cannot be resolved: {list}. " +
                            "These were likely renamed or removed during a package/Unity migration (e.g. com.unity.textmeshpro merged into UGUI in Unity 6). " +
                            "Open the .asmdef and remove or repoint the broken references, otherwise this assembly will fail to compile.",
                            $"{path} 引用了无法解析的程序集：{list}。" +
                            "这通常是包/Unity 迁移时改名或移除导致（如 Unity 6 把 com.unity.textmeshpro 合并进 UGUI）。" +
                            "打开该 .asmdef 移除或重新指向这些引用，否则该程序集会编译失败。"),
                    targetPath: cap,
                    ping: () => PingAsset(cap));
            }
        }

        private static HashSet<string> BuildAsmNameSet()
        {
            var set = new HashSet<string>();
            try { foreach (var a in CompilationPipeline.GetAssemblies(AssembliesType.Editor)) set.Add(a.name); } catch { }
            try { foreach (var a in CompilationPipeline.GetAssemblies(AssembliesType.Player)) set.Add(a.name); } catch { }
            return set;
        }

        /// <summary>
        /// Pure logic: from asmdef JSON text, return the reference tokens that fail to resolve. Object-form (versionDefines-conditional)
        /// references are stripped and never flagged; precompiledReferences and every other field are ignored. A healthy or conditional-only
        /// project returns an empty list. Resolver delegates are injected so this is unit-testable without real assemblies.
        /// </summary>
        internal static IReadOnlyList<string> FindBrokenReferences(
            string asmdefJson, System.Func<string, bool> nameResolves, System.Func<string, bool> guidResolves)
        {
            var broken = new List<string>();
            if (string.IsNullOrEmpty(asmdefJson)) return broken;

            // Isolate the "references" array body. Absent → nothing to check.
            var arr = Regex.Match(asmdefJson, "\"references\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!arr.Success) return broken;
            string body = arr.Groups[1].Value;

            // Drop {...} object blocks (conditional references) entirely — never resolve or flag them (zero false positives on optional packages).
            body = Regex.Replace(body, "\\{[^}]*\\}", " ", RegexOptions.Singleline);

            // Remaining quoted entries are plain name refs or "GUID:xxxx" refs.
            foreach (Match m in Regex.Matches(body, "\"([^\"]+)\""))
            {
                string token = m.Groups[1].Value.Trim();
                if (token.Length == 0) continue;
                bool resolved = token.StartsWith("GUID:")
                    ? guidResolves(token.Substring(5))
                    : nameResolves(token);
                if (!resolved) broken.Add(token);
            }
            return broken;
        }

        /// <summary>
        /// Pure logic: should this .asmdef be checked for broken references at all? Returns false when the assembly
        /// is dormant — it declares a name that is NOT among the currently-compiled assemblies (excluded from
        /// compilation, typically by unsatisfied defineConstraints gating an optional package such as Addressables /
        /// URP). Such an assembly is never compiled, so unresolved references in it cannot fail a build; flagging them
        /// is a false positive. An asmdef with no parseable name is checked (fail-open). Resolver injected for testing.
        /// </summary>
        internal static bool ShouldCheckAsmdefRefs(string asmdefJson, System.Func<string, bool> assemblyIsCompiled)
        {
            string name = ExtractAsmdefName(asmdefJson);
            if (string.IsNullOrEmpty(name)) return true;
            return assemblyIsCompiled(name);
        }

        /// <summary>Pure logic: extract the <c>"name"</c> field from .asmdef JSON, or null if absent/unparseable.</summary>
        internal static string ExtractAsmdefName(string asmdefJson)
        {
            if (string.IsNullOrEmpty(asmdefJson)) return null;
            var m = Regex.Match(asmdefJson, "\"name\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static void PingAsset(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        /// <summary>Parse "2021.3" / "2021.3.16f1" / "6000.0" into (major, minor); returns 0 for any field that fails to parse.</summary>
        internal static (int major, int minor) ParseUnityVer(string v)
        {
            if (string.IsNullOrEmpty(v)) return (0, 0);
            var parts = v.Split('.');
            int.TryParse(parts[0], out int major);
            int minor = 0;
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            return (major, minor);
        }

        /// <summary>Whether the current Unity is ≥ 2023.1 (including Unity 6 = 6000.x), i.e. the version line where FindObjectOfType and similar are deprecated.</summary>
        internal static bool IsAtLeast2023_1(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            var parts = version.Split('.');
            if (!int.TryParse(parts[0], out int major)) return false;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;

            if (major >= 6000) return true;          // Unity 6+ (6000.x)
            if (major > 2023 && major < 6000) return true; // 2024 / 2025…
            if (major == 2023) return minor >= 1;    // 2023.1+
            return false;                            // 2022 and earlier
        }

        /// <summary>Read Active Input Handling: 0=Input Manager (old), 1=Input System (new), 2=Both; returns -1 if it cannot be read.</summary>
        private static int ReadActiveInputHandler()
        {
            string text = SafeRead("ProjectSettings/ProjectSettings.asset");
            if (text == null) return -1;
            var m = Regex.Match(text, @"activeInputHandler:\s*(\d)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : -1;
        }

        /// <summary>
        /// Strip out [string literals / char literals / comments] from a line, leaving only real code, then do API matching.
        /// Otherwise the scanner would hit API names inside strings — e.g. this scanner's own Title/Detail text ("…Application.LoadLevel…"),
        /// or a user's Debug.Log("don't use GUIText") — producing self-referential / literal false positives.
        /// Single-line handling: covers "…", @"…", '…', // line comments, and inline /* … */; for multi-line strings/block comments only the current line's fragment is handled (rare, acceptable).
        /// </summary>
        internal static string StripNonCode(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            int i = 0, n = raw.Length;
            while (i < n)
            {
                char c = raw[i];
                if (c == '/' && i + 1 < n && raw[i + 1] == '/') break;            // line comment → drop the rest of the line
                if (c == '/' && i + 1 < n && raw[i + 1] == '*')                    // block comment
                {
                    int end = raw.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    if (end < 0) break;                                           // multi-line block comment → drop the rest of the line
                    sb.Append(' '); i = end + 2; continue;
                }
                if (c == '"')                                                      // string (including verbatim @"…")
                {
                    bool verbatim = i > 0 && raw[i - 1] == '@';
                    i++;
                    while (i < n)
                    {
                        if (!verbatim && raw[i] == '\\' && i + 1 < n) { i += 2; continue; } // escape
                        if (raw[i] == '"')
                        {
                            if (verbatim && i + 1 < n && raw[i + 1] == '"') { i += 2; continue; } // @"" escape
                            i++; break;
                        }
                        i++;
                    }
                    sb.Append(' '); continue;                                     // placeholder, to avoid gluing the tokens on either side together
                }
                if (c == '\'')                                                     // char literal
                {
                    i++;
                    while (i < n)
                    {
                        if (raw[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (raw[i] == '\'') { i++; break; }
                        i++;
                    }
                    sb.Append(' '); continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        private static string[] ReadLines(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? File.ReadAllLines(full) : null;
            }
            catch { return null; }
        }

        private static string SafeRead(string relPath)
        {
            try
            {
                string full = Path.GetFullPath(relPath);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch { return null; }
        }

        private static void OpenAt(string path, int line)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            EditorGUIUtility.PingObject(obj);
            AssetDatabase.OpenAsset(obj, line);
        }
    }
}
