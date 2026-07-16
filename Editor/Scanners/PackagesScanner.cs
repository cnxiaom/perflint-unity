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
    /// Packages hygiene diagnostics (early-optimization domain, from the Unity6/PC "灵界篇" L09 course match):
    ///   PKG001 — an installed built-in module (com.unity.modules.*) whose signature types are referenced NOWHERE in
    ///            the project (no script, no scene, no prefab). Unused built-in modules still cost runtime memory and
    ///            build size; disabling them (Package Manager ▸ Built-in, or removing from manifest.json) trims both.
    ///   PKG002 — XR built-in modules (vr/ar/xr) installed while the project has NO XR provider (no com.unity.xr.*
    ///            package) and no script references UnityEngine.XR. On modern Unity (2020+), the built-in XR modules do
    ///            nothing without a provider + enabled loader, so unless an XR/VR/AR build is planned they are dead
    ///            weight. Manifest+script based (needs no scene scan), so — unlike PKG001 — it runs even on binary/mixed
    ///            serialization.
    ///
    /// PKG001/PKG002/PKG003 all ship an executable one-click disable (Pro), gated on the same compile-verify +
    /// auto-revert net (<see cref="ModuleDisableService"/> / <see cref="ModuleDisableVerifier"/>): the removal is only
    /// manifest.json lines and fully reversible (re-enable in Package Manager ▸ Built-in / re-add from Unity Registry),
    /// so — unlike the irreversible asset-dedup killer — it's safe to automate once compile-verified. PKG002 removes
    /// its whole XR module group in one edit + one verification (vr/ar depend on xr, one-by-one would be gate-blocked);
    /// PKG003 removes a registry package — same manifest mechanics.
    ///
    /// False-positive discipline is the cardinal rule here (a wrong "remove this" is worse than a miss):
    ///   • Fail-closed on non-ForceText serialization — scenes/prefabs can't be text-scanned, so we can't prove "unused"
    ///     and therefore emit NOTHING (never risk a false positive on a binary/mixed project).
    ///   • Asymmetric tolerance — if a signature type name appears for an unrelated reason (e.g. a user class named
    ///     Terrain), the module is treated as USED and the finding is suppressed (under-report, never over-report).
    ///   • Dependency suppression — a module some installed package depends on is not reported at all (Unity would
    ///     keep it installed; "disable it" would be advice the user cannot execute — e.g. SRP Core depends on the
    ///     terrain module, so URP/HDRP projects can never disable Terrain). Disabling the dependent + rescan
    ///     surfaces the next layer, so multi-level cleanups proceed leaf-first with zero error dialogs.
    ///   • Blind spots stated honestly in the detail: references inside Addressables/AssetBundle-only content, or
    ///     components added at runtime via AddComponent, are not detected — so the wording is always "no references
    ///     found; if genuinely unused, disable to save…", never "this module is unused".
    ///
    /// Whitelist = Vehicles / Cloth / Physics2D / TileMap / Terrain / AI-NavMesh (matching the L09 list). The AI entry
    /// is the built-in com.unity.modules.ai (old NavMesh runtime), suppressed when com.unity.ai.navigation is installed
    /// (that registry package depends on it). terrainphysics and classID-based scene detection are deferred.
    /// Project-level scanner (targetPath = manifest.json); not incremental.
    /// </summary>
    public sealed class PackagesScanner : IScanner
    {
        public string Name => "Packages";
        public Domain Domain => Domain.ProjectSettings;

        /// <summary>Signature of one built-in module: how to recognize its usage in scripts and in scene/prefab YAML.</summary>
        internal sealed class ModuleSig
        {
            public string ModuleName;      // e.g. "com.unity.modules.vehicles"
            public Func<string> Label;     // localized display label (lazy: evaluated per finding, honoring language switch)
            public Regex ScriptPattern;    // matches the module's signature types in (comment/string-stripped) C#
            public Regex YamlPattern;      // matches the module's component keys at column 0 of scene/prefab YAML
            // Optional guard reading manifest.json: when it returns true the module is treated as "in use" and never
            // flagged. Used for a built-in module that a registry package depends on (e.g. com.unity.modules.ai is a
            // dependency of com.unity.ai.navigation) — flagging a still-depended-on module would be a false positive.
            public Func<string, bool> SuppressWhenManifest;
        }

        // Whitelist. Script patterns are word-boundary-anchored type names; YAML patterns are multiline, anchored to the
        // component key that Unity writes at column 0 (e.g. the line "WheelCollider:" under a "--- !u!146 &…" header).
        internal static readonly ModuleSig[] Whitelist =
        {
            new ModuleSig {
                ModuleName = "com.unity.modules.vehicles",
                Label = () => L.Tr("Vehicles (WheelCollider)", "Vehicles 载具（WheelCollider）"),
                ScriptPattern = new Regex(@"\bWheelCollider\b", RegexOptions.Compiled),
                YamlPattern = new Regex(@"^WheelCollider:", RegexOptions.Compiled | RegexOptions.Multiline),
            },
            new ModuleSig {
                ModuleName = "com.unity.modules.cloth",
                Label = () => L.Tr("Cloth", "Cloth 布料"),
                ScriptPattern = new Regex(@"\bCloth\b", RegexOptions.Compiled),
                YamlPattern = new Regex(@"^Cloth:", RegexOptions.Compiled | RegexOptions.Multiline),
            },
            new ModuleSig {
                ModuleName = "com.unity.modules.physics2d",
                Label = () => L.Tr("Physics 2D", "2D 物理（Physics 2D）"),
                ScriptPattern = new Regex(@"\b(Rigidbody2D|Collider2D|Physics2D|Collision2D|RaycastHit2D)\b", RegexOptions.Compiled),
                YamlPattern = new Regex(
                    @"^(Rigidbody2D|BoxCollider2D|CircleCollider2D|CapsuleCollider2D|PolygonCollider2D|EdgeCollider2D|" +
                    @"CompositeCollider2D|CustomCollider2D|TilemapCollider2D|SpringJoint2D|DistanceJoint2D|HingeJoint2D|" +
                    @"SliderJoint2D|WheelJoint2D|FixedJoint2D|FrictionJoint2D|RelativeJoint2D|TargetJoint2D|AreaEffector2D|" +
                    @"PointEffector2D|PlatformEffector2D|SurfaceEffector2D|BuoyancyEffector2D|ConstantForce2D):",
                    RegexOptions.Compiled | RegexOptions.Multiline),
            },
            new ModuleSig {
                ModuleName = "com.unity.modules.tilemap",
                Label = () => L.Tr("Tilemap", "Tilemap 瓦片地图"),
                ScriptPattern = new Regex(@"\b(Tilemap|TilemapRenderer|TilemapCollider2D|TileBase)\b", RegexOptions.Compiled),
                YamlPattern = new Regex(@"^(Tilemap|TilemapRenderer):", RegexOptions.Compiled | RegexOptions.Multiline),
            },
            new ModuleSig {
                ModuleName = "com.unity.modules.terrain",
                Label = () => L.Tr("Terrain", "Terrain 地形"),
                ScriptPattern = new Regex(@"\b(Terrain|TerrainData|TerrainCollider)\b", RegexOptions.Compiled),
                YamlPattern = new Regex(@"^(Terrain|TerrainCollider):", RegexOptions.Compiled | RegexOptions.Multiline),
            },
            new ModuleSig {
                // Terrain Physics is a SEPARATE built-in module from terrain (rendering): it provides TerrainCollider.
                // Both ship in the default manifest, so a project with no terrain at all carries both as dead weight.
                // No suppress guard: if both are unused, both are flagged; the developer removes this leaf module first
                // (the terrain module it depends on is unblocked only after — the shared PKG001 dependency caveat covers it).
                ModuleName = "com.unity.modules.terrainphysics",
                Label = () => L.Tr("Terrain Physics (TerrainCollider)", "Terrain Physics 地形碰撞（TerrainCollider）"),
                ScriptPattern = new Regex(@"\bTerrainCollider\b", RegexOptions.Compiled),
                YamlPattern = new Regex(@"^TerrainCollider:", RegexOptions.Compiled | RegexOptions.Multiline),
            },
            new ModuleSig {
                // The built-in AI module = the old NavMesh runtime (NavMeshAgent/Obstacle/OffMeshLink, NavMesh.* API),
                // shown as "AI" in Package Manager ▸ Built-in. NOT the same as the com.unity.ai.navigation registry
                // package (the modern NavMeshSurface/NavMeshModifier workflow), which DEPENDS ON this module — so when
                // that package is installed we suppress (the module is a required transitive dependency, not dead weight).
                ModuleName = "com.unity.modules.ai",
                Label = () => L.Tr("AI / NavMesh (com.unity.modules.ai)", "AI / NavMesh 寻路（com.unity.modules.ai）"),
                ScriptPattern = new Regex(@"\b(NavMeshAgent|NavMeshObstacle|OffMeshLink|NavMeshBuilder|NavMeshData|NavMeshPath|NavMeshHit|NavMeshQueryFilter)\b|\bNavMesh\.", RegexOptions.Compiled),
                YamlPattern = new Regex(@"^(NavMeshAgent|NavMeshObstacle|OffMeshLink):", RegexOptions.Compiled | RegexOptions.Multiline),
                SuppressWhenManifest = m => ModulePresent(m, "com.unity.ai.navigation"),
            },
        };

        // ── PKG002: XR built-in modules ──
        /// <summary>Built-in XR/VR/AR modules considered by PKG002.</summary>
        internal static readonly string[] XrModules =
        {
            "com.unity.modules.vr", "com.unity.modules.ar", "com.unity.modules.xr",
        };

        /// <summary>XR usage signature in C# — a script referencing these means XR is actually used, suppressing PKG002.</summary>
        internal static readonly ModuleSig XrSig = new ModuleSig
        {
            ModuleName = "xr",
            Label = () => L.Tr("XR / VR / AR", "XR / VR / AR"),
            ScriptPattern = new Regex(
                @"\b(XRSettings|XRDevice|XRNode|InputDevices|InputTracking|TrackedPoseDriver|XROrigin)\b|UnityEngine\.XR",
                RegexOptions.Compiled),
            YamlPattern = null, // XR is code/settings driven, not a scene component
        };

        // ── PKG003: unused AI Navigation registry package ──
        internal const string NavPackageName = "com.unity.ai.navigation";

        // The built-in AI/NavMesh module entry, reused to detect the underlying NavMesh runtime usage (NavMeshAgent /
        // NavMesh.* in scripts, NavMeshAgent:/Obstacle:/OffMeshLink: in scenes) — if the runtime is in use, the project
        // is doing navigation and we must not advise removing the package.
        private static readonly ModuleSig AiRuntimeSig = System.Array.Find(Whitelist, m => m.ModuleName == "com.unity.modules.ai");

        /// <summary>
        /// Script signature of the AI Navigation package's OWN types (Unity.AI.Navigation): NavMeshSurface/Modifier/Link.
        /// Distinct from the built-in runtime (AiRuntimeSig): these come from the registry package, not the module.
        /// </summary>
        internal static readonly ModuleSig NavPkgSig = new ModuleSig
        {
            ModuleName = NavPackageName,
            Label = () => L.Tr("AI Navigation", "AI Navigation"),
            ScriptPattern = new Regex(@"\b(NavMeshSurface|NavMeshModifier|NavMeshModifierVolume|NavMeshLink)\b|Unity\.AI\.Navigation", RegexOptions.Compiled),
            YamlPattern = null, // package components are MonoBehaviours (GUID-referenced) — detected by GUID, not a column-0 key
        };

        /// <summary>Component script filenames whose GUIDs mark the AI Navigation package as in use when found in a scene/prefab.</summary>
        internal static readonly string[] NavComponentScriptNames =
        {
            "NavMeshSurface", "NavMeshModifier", "NavMeshModifierVolume", "NavMeshLink",
        };

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            string manifest = SafeRead("Packages/manifest.json");
            if (manifest == null) yield break;

            // PKG001 candidates need ForceText (they lean on the scene/prefab YAML layer to prove "unused"); without it
            // we cannot prove unused, so we treat the PKG001 set as empty — fail-closed, never a false "remove this".
            bool isText = EditorSettings.serializationMode == SerializationMode.ForceText;
            var present = new List<ModuleSig>();
            if (isText)
                foreach (var sig in Whitelist)
                    if (ModulePresent(manifest, sig.ModuleName)
                        && (sig.SuppressWhenManifest == null || !sig.SuppressWhenManifest(manifest)))
                        present.Add(sig);

            // Scan-time dependency suppression (FP discipline: advice you cannot execute is noise). Unity keeps a
            // module installed while any installed package depends on it — e.g. SRP Core declares a dependency on the
            // terrain module, so on every URP/HDRP project "disable Terrain" is impossible; physics2d under an
            // installed tilemap can only leave after tilemap does. Modules with an installed dependent are therefore
            // not reported at all; disabling the dependent + rescan surfaces the next layer (leaf-first by
            // construction). Registry unavailable → fail open (no suppression; the disable-time gate still refuses
            // honestly at click time).
            var installedSnapshot = ModuleDisableService.SnapshotInstalledPackages();
            if (installedSnapshot != null)
                present.RemoveAll(sig => ModuleDisableService.FindDependentIn(installedSnapshot, sig.ModuleName) != null);

            // PKG002: XR built-in modules present, and no XR provider package to give them purpose. The batch keeps
            // only modules with no dependent OUTSIDE the batch itself (vr/ar depend on xr — they leave together).
            var xrPresent = new List<string>();
            foreach (var m in XrModules)
                if (ModulePresent(manifest, m)) xrPresent.Add(m);
            if (installedSnapshot != null && xrPresent.Count > 0)
                xrPresent = ModuleDisableService.FilterExternallyFree(installedSnapshot, xrPresent);
            bool xrCandidate = xrPresent.Count > 0 && !HasXrProvider(manifest);

            // PKG003: AI Navigation registry package present. Needs ForceText (scene GUID scan) AND resolvable component
            // GUIDs — if we can't resolve them we can't prove scene non-usage, so we don't emit (fail-closed).
            var navGuids = (isText && ModulePresent(manifest, NavPackageName)) ? ResolveNavComponentGuids() : null;
            bool navActive = navGuids != null && navGuids.Count > 0;

            if (present.Count == 0 && !xrCandidate && !navActive) yield break;

            var used = new HashSet<string>();
            bool xrUsed = false;
            bool navUsed = false;

            // ── Script layer ── (scripts are text regardless of serialization mode → serves PKG001/PKG002/PKG003)
            var scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            for (int i = 0; i < scriptGuids.Length; i++)
            {
                if (used.Count >= present.Count && (!xrCandidate || xrUsed) && (!navActive || navUsed)) break; // all resolved
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, 0.5f * i / Math.Max(1, scriptGuids.Length));

                string path = AssetDatabase.GUIDToAssetPath(scriptGuids[i]);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs")) continue;
                if (ScannerUtil.IsPerfLintOwnAsset(path)) continue;
                string src = SafeRead(path);
                if (src == null) continue;

                foreach (var sig in present)
                    if (!used.Contains(sig.ModuleName) && CodeUsesModule(src, sig))
                        used.Add(sig.ModuleName);
                if (xrCandidate && !xrUsed && CodeUsesModule(src, XrSig))
                    xrUsed = true;
                // PKG003 usage = the package's own types OR the underlying NavMesh runtime (either means "doing nav").
                if (navActive && !navUsed && (CodeUsesModule(src, NavPkgSig) || CodeUsesModule(src, AiRuntimeSig)))
                    navUsed = true;
            }

            // ── Scene + prefab layer ── (PKG001 built-in component keys; PKG003 nav component GUIDs / built-in nav keys)
            if (used.Count < present.Count || (navActive && !navUsed))
            {
                var assetGuids = CollectSceneAndPrefabGuids();
                for (int i = 0; i < assetGuids.Count; i++)
                {
                    if (used.Count >= present.Count && (!navActive || navUsed)) break;
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.ReportProgress(Name, 0.5f + 0.5f * i / Math.Max(1, assetGuids.Count));

                    string path = AssetDatabase.GUIDToAssetPath(assetGuids[i]);
                    if (string.IsNullOrEmpty(path)) continue;
                    string yaml = SafeRead(path);
                    if (yaml == null) continue;

                    foreach (var sig in present)
                        if (!used.Contains(sig.ModuleName) && AssetUsesModule(yaml, sig))
                            used.Add(sig.ModuleName);
                    // PKG003 scene usage: a NavMeshSurface/Modifier/Link component (by GUID) or a built-in nav component.
                    if (navActive && !navUsed && (YamlHasAnyGuid(yaml, navGuids) || AssetUsesModule(yaml, AiRuntimeSig)))
                        navUsed = true;
                }
            }

            // ── PKG002: XR modules with no provider and no script usage ──
            if (ShouldFlagXr(xrPresent.Count > 0, HasXrProvider(manifest), xrUsed))
            {
                string list = string.Join(", ", xrPresent);
                string xrLocate = xrPresent.Count > 0 ? xrPresent[0] : null; // navigate PM to a concrete XR module
                yield return new Finding(
                    ruleId: "PKG002",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("XR/VR/AR modules installed but no XR provider", "装了 XR/VR/AR 模块但无 XR 提供方"),
                    groupTitle: L.Tr("Unused XR/VR/AR module", "未使用的 XR/VR/AR 模块"),
                    detail: L.Tr(
                        $"The XR/VR/AR built-in modules ({list}) are installed, but the project has no XR provider package " +
                        "(no com.unity.xr.* — e.g. XR Plug-in Management + OpenXR/Oculus/AR Foundation) and no script references UnityEngine.XR. " +
                        "On modern Unity a built-in XR module does nothing without a provider and an enabled loader, so unless you are building an XR/VR/AR app " +
                        "these modules only add runtime memory and build size — disable them under Window ▸ Package Manager ▸ Built-in. " +
                        "If you DO plan XR, install a provider instead (which clears this finding). Not covered: legacy built-in VR (pre-2020) and runtime/Addressables-only usage.",
                        $"XR/VR/AR 内置模块（{list}）已安装，但项目没有任何 XR 提供方包" +
                        "（没有 com.unity.xr.*，如 XR Plug-in Management + OpenXR/Oculus/AR Foundation），脚本也未引用 UnityEngine.XR。" +
                        "现代 Unity 下内置 XR 模块没有提供方和启用的 loader 就不起作用，所以除非你在做 XR/VR/AR 应用，" +
                        "这些模块只会白占运行时内存与包体——可在 Window ▸ Package Manager ▸ Built-in 关闭。" +
                        "若确要做 XR，请改为安装提供方包（装上后本条自动消失）。未覆盖：Unity 2020 前的 legacy 内置 VR、以及运行时/Addressables-only 用法。"),
                    targetPath: "Packages/manifest.json",
                    ping: () => OpenPackageManagerBuiltIn(xrLocate),
                    action: new FindingAction(
                        label: L.Tr($"Disable XR modules ({xrPresent.Count})", $"禁用 XR 模块（{xrPresent.Count}）"),
                        confirmMessage: L.Tr(
                            $"Remove these built-in modules from Packages/manifest.json: {list}.\n\n" +
                            "They are removed together in one edit (vr/ar depend on xr, so one-by-one would be blocked), backed by one compile " +
                            "verification: if any script still references them and fails to compile, the manifest is automatically reverted. " +
                            "Re-enable any time under Window ▸ Package Manager ▸ Built-in. This edits manifest.json (not Edit > Undo territory), " +
                            "so commit to version control first.",
                            $"从 Packages/manifest.json 移除这些内置模块：{list}。\n\n" +
                            "它们会在一次编辑中一并移除（vr/ar 依赖 xr，逐个禁用会被依赖拦截），并由一次编译校验兜底：" +
                            "若仍有脚本引用并编译失败，manifest 会自动回滚。随时可在 Window ▸ Package Manager ▸ Built-in 重新启用。" +
                            "此操作修改 manifest.json（非 Edit > Undo 范畴），建议先提交版本控制。"),
                        run: () => ModuleDisableService.DisableMany(xrPresent.ToArray()),
                        allowRuleBatch: false));
            }

            // ── PKG001: every present module we never saw referenced ──
            foreach (var sig in present)
            {
                if (used.Contains(sig.ModuleName)) continue;
                string label = sig.Label();
                string moduleName = sig.ModuleName; // capture for the ping closure
                yield return new Finding(
                    ruleId: "PKG001",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr($"Unused built-in module: {label}", $"未使用的内置模块：{label}"),
                    groupTitle: L.Tr("Unused built-in module", "未使用的内置模块"),
                    detail: L.Tr(
                        $"The {label} module ({sig.ModuleName}) is installed, but PerfLint found no references to it in any " +
                        "script, scene, or prefab under Assets/. Unused built-in modules still add to runtime memory and build size. " +
                        "If it is genuinely unused, disable it under Window ▸ Package Manager ▸ Built-in (or remove it from Packages/manifest.json) to trim both. " +
                        "Caveats: references inside Addressables/AssetBundle-only content, or components added at runtime via AddComponent, are not detected; " +
                        "and Unity blocks disabling a module that another installed package depends on — verify before removing.",
                        $"内置模块 {label}（{sig.ModuleName}）已安装，但 PerfLint 在 Assets/ 下的所有脚本、场景、预制体中都未发现对它的引用。" +
                        "未使用的内置模块仍会占用运行时内存与包体。若确系未用，可在 Window ▸ Package Manager ▸ Built-in 关闭它" +
                        "（或从 Packages/manifest.json 移除），两者都能减。" +
                        "注意：Addressables/AssetBundle-only 内容里的引用、或运行时 AddComponent 生成的组件检测不到；" +
                        "且若有其他已安装包依赖该模块，Unity 会拦截关闭——移除前请先核对。"),
                    targetPath: "Packages/manifest.json",
                    ping: () => OpenPackageManagerBuiltIn(moduleName),
                    action: new FindingAction(
                        label: L.Tr($"Disable {label}", $"禁用 {label}"),
                        confirmMessage: L.Tr(
                            $"Remove {sig.ModuleName} from Packages/manifest.json to disable the {label} module.\n\n" +
                            "PerfLint will recompile to verify the removal: if any script still references this module and fails to compile, " +
                            "the manifest is automatically reverted (nothing is lost). Disabling is reversible either way — re-enable it any time under " +
                            "Window ▸ Package Manager ▸ Built-in. This edits manifest.json (not Edit > Undo territory), so commit to version control first.",
                            $"从 Packages/manifest.json 移除 {sig.ModuleName} 以禁用 {label} 模块。\n\n" +
                            "PerfLint 会重新编译校验此移除：若仍有脚本引用该模块并编译失败，manifest 会自动回滚（不会丢东西）。" +
                            "无论如何禁用都可逆——随时可在 Window ▸ Package Manager ▸ Built-in 重新启用。" +
                            "此操作修改 manifest.json（非 Edit > Undo 范畴），建议先提交版本控制。"),
                        run: () => ModuleDisableService.Disable(moduleName),
                        // No rule-level "disable all" button: each finding targets a DIFFERENT module (a shared
                        // "Disable X all" label would be wrong), and each disable triggers a package re-resolve +
                        // domain reload + one-at-a-time compile verification, so they can't run in a batch loop.
                        allowRuleBatch: false));
            }

            // ── PKG003: AI Navigation package installed but no NavMesh usage anywhere ──
            if (navActive && !navUsed)
            {
                yield return new Finding(
                    ruleId: "PKG003",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Unused AI Navigation package", "未使用的 AI Navigation 包"),
                    detail: L.Tr(
                        "com.unity.ai.navigation (AI Navigation) is installed, but PerfLint found no NavMesh usage — no NavMeshSurface/" +
                        "NavMeshModifier/NavMeshLink component (in any script, scene, or prefab) and no NavMesh runtime (NavMeshAgent / NavMesh.*). " +
                        "It is often auto-added to URP/HDRP templates and left unused. If you are not using AI Navigation, remove the package via " +
                        "Window ▸ Package Manager to trim runtime memory and build size — this also frees its com.unity.modules.ai dependency. " +
                        "Caveats: usage inside Addressables/AssetBundle-only content or components added at runtime is not detected — verify in the AI/Navigation window before removing.",
                        "com.unity.ai.navigation（AI Navigation）已安装，但 PerfLint 未发现任何 NavMesh 用法——脚本/场景/预制体里没有 " +
                        "NavMeshSurface/NavMeshModifier/NavMeshLink 组件，也没有 NavMesh 运行时（NavMeshAgent / NavMesh.*）。" +
                        "它常被 URP/HDRP 模板自动带上却一直没用。若你不用 AI Navigation，可在 Window ▸ Package Manager 移除该包，减运行时内存与包体" +
                        "——同时会释放它依赖的 com.unity.modules.ai。" +
                        "注意：Addressables/AssetBundle-only 内容里的用法、或运行时新增的组件检测不到——移除前请先在 AI/Navigation 窗口核对。"),
                    targetPath: "Packages/manifest.json",
                    ping: () => OpenPackageManagerBuiltIn(NavPackageName),
                    action: new FindingAction(
                        label: L.Tr("Remove AI Navigation package", "移除 AI Navigation 包"),
                        confirmMessage: L.Tr(
                            $"Remove {NavPackageName} from Packages/manifest.json.\n\n" +
                            "PerfLint will recompile to verify the removal: if any script still references the package and fails to compile, " +
                            "the manifest is automatically reverted (nothing is lost). Re-add it any time via Window ▸ Package Manager ▸ " +
                            "Unity Registry ▸ AI Navigation. This edits manifest.json (not Edit > Undo territory), so commit to version control first.",
                            $"从 Packages/manifest.json 移除 {NavPackageName}。\n\n" +
                            "PerfLint 会重新编译校验此移除：若仍有脚本引用该包并编译失败，manifest 会自动回滚（不会丢东西）。" +
                            "随时可在 Window ▸ Package Manager ▸ Unity Registry ▸ AI Navigation 重新安装。" +
                            "此操作修改 manifest.json（非 Edit > Undo 范畴），建议先提交版本控制。"),
                        run: () => ModuleDisableService.Disable(NavPackageName),
                        allowRuleBatch: false));
            }
        }

        /// <summary>
        /// Resolve the AssetDatabase GUIDs of the AI Navigation package's component scripts (NavMeshSurface, etc.). These
        /// are the GUIDs that appear as <c>m_Script: {guid: …}</c> in scene/prefab YAML, letting us detect the package's
        /// MonoBehaviour components (which have no column-0 type key). Filtered to the package path + exact filename so a
        /// user script coincidentally named NavMeshSurface doesn't leak in. Empty set → PKG003 is not emitted (fail-closed).
        /// </summary>
        private static HashSet<string> ResolveNavComponentGuids()
        {
            var set = new HashSet<string>();
            foreach (var name in NavComponentScriptNames)
            {
                string[] hits;
                try { hits = AssetDatabase.FindAssets(name + " t:MonoScript"); }
                catch { continue; }
                foreach (var guid in hits)
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(p)) continue;
                    if (p.IndexOf(NavPackageName, StringComparison.Ordinal) >= 0
                        && p.EndsWith("/" + name + ".cs", StringComparison.Ordinal))
                        set.Add(guid);
                }
            }
            return set;
        }

        /// <summary>Pure logic: does the scene/prefab YAML reference any of the given script GUIDs (as m_Script guid)?</summary>
        internal static bool YamlHasAnyGuid(string yaml, ICollection<string> guids)
        {
            if (string.IsNullOrEmpty(yaml) || guids == null || guids.Count == 0) return false;
            foreach (var g in guids)
                if (!string.IsNullOrEmpty(g) && yaml.IndexOf(g, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Locate action for package findings: open the Package Manager at its Built-in filter (where the user disables
        /// the module). manifest.json is not an AssetDatabase asset, so a normal ping/OpenAsset would no-op — this jumps
        /// to the actionable UI instead. Best-effort across versions: try to select the specific built-in package, then
        /// fall back to opening the window, and reveal manifest.json as a last resort.
        /// </summary>
        private static void OpenPackageManagerBuiltIn(string moduleName = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(moduleName))
                    UnityEditor.PackageManager.UI.Window.Open(moduleName);
                else
                    UnityEditor.PackageManager.UI.Window.Open(string.Empty);
                return;
            }
            catch { /* PM API shape varies across versions / built-in ids may not resolve; fall through */ }
            try { EditorUtility.RevealInFinder(ScannerUtil.ToPhysicalFullPath("Packages/manifest.json")); }
            catch { /* last resort; nothing else to do */ }
        }

        /// <summary>Pure logic: is the module declared as a dependency in manifest.json? Matches the quoted key followed by ':'.</summary>
        internal static bool ModulePresent(string manifestJson, string moduleName)
        {
            if (string.IsNullOrEmpty(manifestJson) || string.IsNullOrEmpty(moduleName)) return false;
            return Regex.IsMatch(manifestJson, "\"" + Regex.Escape(moduleName) + "\"\\s*:");
        }

        /// <summary>
        /// Pure logic: does the manifest declare any XR provider package (com.unity.xr.*, e.g. management/openxr/oculus/
        /// arfoundation/interaction.toolkit/core-utils)? Their presence means XR is intended → suppress PKG002. Errs
        /// toward "provider present" (any com.unity.xr.* match) so we never falsely flag an XR project.
        /// </summary>
        internal static bool HasXrProvider(string manifestJson)
        {
            if (string.IsNullOrEmpty(manifestJson)) return false;
            return manifestJson.IndexOf("\"com.unity.xr.", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Pure decision for PKG002: flag only when an XR module is present, no provider exists, and no script uses XR.</summary>
        internal static bool ShouldFlagXr(bool anyXrModulePresent, bool hasXrProvider, bool xrUsedInScript)
            => anyXrModulePresent && !hasXrProvider && !xrUsedInScript;

        /// <summary>Pure logic: does the C# source reference this module's signature types? Strips comments/strings per line (reuses MigrationScanner.StripNonCode) to avoid literal/comment false positives.</summary>
        internal static bool CodeUsesModule(string rawSource, ModuleSig sig)
        {
            if (string.IsNullOrEmpty(rawSource) || sig?.ScriptPattern == null) return false;
            foreach (var line in rawSource.Split('\n'))
            {
                string code = MigrationScanner.StripNonCode(line);
                if (sig.ScriptPattern.IsMatch(code)) return true;
            }
            return false;
        }

        /// <summary>Pure logic: does scene/prefab YAML contain one of this module's component keys at column 0?</summary>
        internal static bool AssetUsesModule(string yaml, ModuleSig sig)
            => !string.IsNullOrEmpty(yaml) && sig?.YamlPattern != null && sig.YamlPattern.IsMatch(yaml);

        /// <summary>Scenes (.unity via t:SceneAsset) and prefabs (.prefab via t:GameObject, filtered to skip binary model prefabs like .fbx).</summary>
        private static List<string> CollectSceneAndPrefabGuids()
        {
            var list = new List<string>();
            try { list.AddRange(AssetDatabase.FindAssets("t:SceneAsset", new[] { "Assets" })); } catch { }
            try
            {
                foreach (var g in AssetDatabase.FindAssets("t:GameObject", new[] { "Assets" }))
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        list.Add(g);
                }
            }
            catch { }
            return list;
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
    }
}
