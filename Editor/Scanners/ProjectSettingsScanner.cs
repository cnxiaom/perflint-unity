using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine.Rendering;

namespace PerfLint.Scanners
{
    /// <summary>
    /// P1 project settings diagnostics. This domain is mostly "situational" tuning items; intentionally limited to a few
    /// broadly-accepted, low-false-positive rules, all at Info severity with restrained wording:
    ///   PROJ001 — Too many Always Included Shaders (each one compiles all its variants into the build).
    ///   PROJ002 — Managed Stripping disabled under IL2CPP (increases build size).
    ///   PROJ003 — Incremental GC not enabled (bulk GC collections can cause stutter spikes). Includes an executable action "Enable".
    ///   PROJ005 — Prebake Collision Meshes not enabled (build cost on first collision/load at runtime). Includes an executable action "Enable".
    ///   PROJ008 — Multithreaded Rendering disabled on a mobile target (the render thread stays on the main thread, raising CPU frame time). Includes an executable action "Enable".
    ///   PROJ009 — Optimize Mesh Data disabled (unused vertex attributes such as Color/Tangent ship in every mesh, inflating memory and build size). Includes an executable action "Enable".
    ///   PROJ010 — WebGL Compression Format is Disabled (uncompressed .wasm/.data downloads; Brotli recommended). WebGL target only.
    ///             The one Warning in this domain: gated to the active WebGL target so it is near-zero-FP, and the impact (download size = load time on
    ///             web game platforms) is large and quantifiable. Includes an executable action "Set to Brotli".
    ///             Deliberately does NOT flag Gzip: choosing Gzip is a legitimate self-hosting tradeoff (faster JS decompression fallback on servers
    ///             without Content-Encoding config); only Disabled is a pure loss.
    ///   PROJ011 — Strip Engine Code off where it applies (IL2CPP backend or WebGL). Native-side complement of PROJ002's managed stripping. Includes an executable action "Enable".
    ///   PROJ012 — IL2CPP Code Generation set to OptimizeSpeed on a size-sensitive target (WebGL, or mobile with IL2CPP). Suggests OptimizeSize
    ///             ("Faster (smaller) builds"). Includes an executable action; confirm text states the runtime tradeoff.
    /// (Dynamic batching detection has been removed because there is no stable public API to read it.)
    /// Project-level findings have no specific asset path (targetPath is null). Rules with a FindingAction modify
    /// project settings (not undoable, Pro-gated).
    /// UI is displayed under the ProjectSettings group.
    /// </summary>
    public sealed class ProjectSettingsScanner : IScanner
    {
        public string Name => "Project Settings";
        public Domain Domain => Domain.ProjectSettings;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            // PROJ001 — Too many Always Included Shaders
            int alwaysCount = CountAlwaysIncludedShaders();
            if (alwaysCount > 8)
            {
                yield return new Finding(
                    ruleId: "PROJ001",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Many Always Included Shaders", "Always Included Shaders 较多"),
                    detail: L.Tr($"Graphics Settings has {alwaysCount} Always Included Shaders. Each one compiles all of its variants " +
                            "into the build, increasing build size and compile time. Confirm they are all necessary (move unneeded ones to on-demand/Addressables loading).",
                            $"Graphics Settings 的 Always Included Shaders 有 {alwaysCount} 个。每个都会把其所有变体" +
                            "编译进包，增大包体与编译时间。确认是否都必要（不必要的可移到按需/Addressables 加载）。"),
                    targetPath: null);
            }

            // PROJ002 — Managed Stripping disabled under IL2CPP
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            if (PlayerSettings.GetScriptingBackend(group) == ScriptingImplementation.IL2CPP
                && PlayerSettings.GetManagedStrippingLevel(group) == ManagedStrippingLevel.Disabled)
            {
                yield return new Finding(
                    ruleId: "PROJ002",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Managed Stripping disabled under IL2CPP", "IL2CPP 下 Managed Stripping 关闭"),
                    detail: L.Tr("The scripting backend is IL2CPP but Managed Stripping Level is Disabled, which significantly increases build size. " +
                            "Set it to at least Low (note: types used by reflection/serialization may get stripped, so preserve them explicitly via link.xml).",
                            "当前后端为 IL2CPP 但 Managed Stripping Level 为 Disabled，会显著增大包体。" +
                            "建议至少设为 Low（注意：反射 / 序列化用到的类型可能被裁掉，需用 link.xml 显式保留）。"),
                    targetPath: null);
            }

            // PROJ003 — Incremental GC not enabled (broadly recommended to enable; includes executable action)
            if (!PlayerSettings.gcIncremental)
            {
                yield return new Finding(
                    ruleId: "PROJ003",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Incremental GC is off", "增量式 GC 未开启"),
                    detail: L.Tr("Use Incremental GC is off in Player Settings. Incremental GC spreads garbage collection across multiple frames, " +
                            "significantly reducing the stutter spikes caused by a single bulk collection (runtime GC pressure can be confirmed by RUN.GC001). " +
                            "Recommended for the vast majority of games; verify the benefit with Profile Analyzer after enabling.",
                            "Player Settings 的 Use Incremental GC 未开启。增量式 GC 把垃圾回收分摊到多帧，" +
                            "能显著削减 GC 集中回收造成的卡顿尖峰（运行时可由 RUN.GC001 证实 GC 压力）。" +
                            "绝大多数游戏建议开启；开启后用 Profile Analyzer 验证收益。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Enable Incremental GC", "开启增量式 GC"),
                        confirmMessage: L.Tr("Set Use Incremental GC to on in Player Settings.\n" +
                                        "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                        "将 Player Settings 的 Use Incremental GC 设为开启。\n" +
                                        "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            PlayerSettings.gcIncremental = true;
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("Use Incremental GC enabled.", "已开启 Use Incremental GC。"));
                        }));
            }

            // PROJ005 — Prebake Collision Meshes not enabled (includes executable action)
            if (!PlayerSettings.bakeCollisionMeshes)
            {
                yield return new Finding(
                    ruleId: "PROJ005",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Prebake Collision Meshes is off", "Prebake Collision Meshes 未开启"),
                    detail: L.Tr("Prebake Collision Meshes is off in Player Settings. Enabling it bakes collision meshes at build time, " +
                            "reducing the mesh-construction cost and stutter on first collision/load at runtime, at the cost of a slightly larger build. Recommended for physics-heavy projects.",
                            "Player Settings 的 Prebake Collision Meshes 未开启。开启后碰撞网格在构建期烘焙，" +
                            "可减少运行时首次碰撞/加载时的网格构建开销与卡顿；代价是构建产物略增。物理量较多的项目建议开启。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Enable Prebake Collision Meshes", "开启 Prebake Collision Meshes"),
                        confirmMessage: L.Tr("Set Prebake Collision Meshes to on in Player Settings.\n" +
                                        "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                        "将 Player Settings 的 Prebake Collision Meshes 设为开启。\n" +
                                        "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            PlayerSettings.bakeCollisionMeshes = true;
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("Prebake Collision Meshes enabled.", "已开启 Prebake Collision Meshes。"));
                        }));
            }

            // PROJ008 — Multithreaded Rendering disabled on a mobile target (only meaningful for Android/iOS; includes executable action)
            var mobileGroup = ResolveMobileGroup(context);
            if (mobileGroup != BuildTargetGroup.Unknown && !PlayerSettings.GetMobileMTRendering(mobileGroup))
            {
                var mtGroup = mobileGroup; // capture for the action delegate
                yield return new Finding(
                    ruleId: "PROJ008",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Multithreaded Rendering is off (mobile)", "移动端 Multithreaded Rendering 未开启"),
                    detail: L.Tr($"Multithreaded Rendering is off for {mtGroup}. The render thread runs on the main thread, so all graphics-submission " +
                            "work piles onto CPU frame time. Enabling it moves submission to a separate thread and usually lowers main-thread cost on multi-core mobile devices. " +
                            "Recommended for the vast majority of mobile projects; verify the gain in the Profiler afterward.",
                            $"{mtGroup} 平台的 Multithreaded Rendering 未开启。渲染线程跑在主线程上，所有图形提交开销都压在 CPU 帧时间里。" +
                            "开启后提交工作移到独立线程，在多核移动设备上通常能降低主线程耗时。绝大多数移动项目建议开启；开启后用 Profiler 验证收益。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Enable Multithreaded Rendering", "开启 Multithreaded Rendering"),
                        confirmMessage: L.Tr($"Enable Mobile Multithreaded Rendering for {mtGroup} in Player Settings.\n" +
                                        "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                        $"为 {mtGroup} 平台开启 Mobile Multithreaded Rendering。\n" +
                                        "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            PlayerSettings.SetMobileMTRendering(mtGroup, true);
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("Multithreaded Rendering enabled.", "已开启 Multithreaded Rendering。"));
                        }));
            }

            // PROJ009 — Optimize Mesh Data disabled (project-wide; includes executable action)
            if (!PlayerSettings.stripUnusedMeshComponents)
            {
                yield return new Finding(
                    ruleId: "PROJ009",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Optimize Mesh Data is off", "Optimize Mesh Data 未开启"),
                    detail: L.Tr("Optimize Mesh Data is off in Player Settings. With it on, the build strips vertex attributes a mesh's shaders never use " +
                            "(Color, Tangent, extra UVs…), reducing mesh memory and build size. Caveat: if you swap a renderer's material at runtime to one needing more attributes, " +
                            "those attributes may have been stripped — assign such materials in the prefab before building so the importer keeps them.",
                            "Player Settings 的 Optimize Mesh Data 未开启。开启后构建会剔除网格 Shader 用不到的顶点属性" +
                            "（Color、Tangent、多余 UV 等），减少网格内存与包体。注意：若运行时把 Renderer 的材质换成需要更多属性的材质，" +
                            "这些属性可能已被剔除——请在 Prefab 上先挂好此类材质再构建，导入器才会保留。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Enable Optimize Mesh Data", "开启 Optimize Mesh Data"),
                        confirmMessage: L.Tr("Set Optimize Mesh Data to on in Player Settings.\n" +
                                        "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                        "将 Player Settings 的 Optimize Mesh Data 设为开启。\n" +
                                        "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            PlayerSettings.stripUnusedMeshComponents = true;
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("Optimize Mesh Data enabled.", "已开启 Optimize Mesh Data。"));
                        }));
            }

            // PROJ010 — WebGL build compression disabled (WebGL target only; includes executable action)
            var resolvedGroup = ResolveGroup(context);
            if (resolvedGroup == BuildTargetGroup.WebGL
                && PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Disabled)
            {
                yield return new Finding(
                    ruleId: "PROJ010",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Warning,
                    title: L.Tr("WebGL build compression is disabled", "WebGL 构建压缩未开启"),
                    detail: L.Tr("Publishing Settings > Compression Format is Disabled, so the .wasm and .data files ship uncompressed — downloads are commonly 2-4x larger " +
                            "than with Brotli, which directly lengthens first-load time (the metric web game platforms live by). Brotli also compresses ~20% smaller than Gzip; " +
                            "browsers accept it over HTTPS, which itch.io / Poki / CrazyGames and similar platforms already serve. If you self-host on a server without " +
                            "Content-Encoding headers, additionally enable Decompression Fallback in Publishing Settings (or use Gzip, whose fallback decompresses faster).",
                            "Publishing Settings 的 Compression Format 为 Disabled，.wasm 与 .data 文件未经压缩直接分发——下载体积通常比 Brotli 压缩后大 2-4 倍，" +
                            "直接拉长首次加载时长（web 游戏平台最看重的指标）。Brotli 还比 Gzip 小 ~20%；浏览器在 HTTPS 下即支持（itch.io / Poki / CrazyGames 等平台均为 HTTPS）。" +
                            "若自托管在不发 Content-Encoding 响应头的服务器上，请同时开启 Publishing Settings 的 Decompression Fallback（或改用 Gzip，其回退解压更快）。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Set Compression Format to Brotli", "压缩格式设为 Brotli"),
                        confirmMessage: L.Tr("Set WebGL Publishing Settings > Compression Format to Brotli.\n" +
                                        "Your web host must serve the files with a Content-Encoding: br header over HTTPS (major web game platforms do); " +
                                        "otherwise also enable Decompression Fallback in Publishing Settings.\n" +
                                        "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                        "将 WebGL Publishing Settings 的 Compression Format 设为 Brotli。\n" +
                                        "托管服务器需在 HTTPS 下以 Content-Encoding: br 响应头分发文件（主流 web 游戏平台均支持）；" +
                                        "否则请同时开启 Publishing Settings 的 Decompression Fallback。\n" +
                                        "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("WebGL Compression Format set to Brotli.", "WebGL Compression Format 已设为 Brotli。"));
                        }));
            }

            // PROJ011 — Strip Engine Code off where it applies (IL2CPP backend, or WebGL which always builds native via IL2CPP; includes executable action)
            bool engineStripApplies = resolvedGroup == BuildTargetGroup.WebGL
                || (resolvedGroup != BuildTargetGroup.Unknown
                    && PlayerSettings.GetScriptingBackend(resolvedGroup) == ScriptingImplementation.IL2CPP);
            if (engineStripApplies && !PlayerSettings.stripEngineCode)
            {
                yield return new Finding(
                    ruleId: "PROJ011",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("Strip Engine Code is off", "Strip Engine Code 未开启"),
                    detail: L.Tr("Strip Engine Code is off in Player Settings. On IL2CPP platforms (including WebGL) it removes unused native engine code from the binary, " +
                            "often saving several MB — separate from Managed Stripping Level, which only covers C# assemblies. Caveat: engine types referenced only by content " +
                            "loaded at runtime (AssetBundles/Addressables) can be stripped away — preserve them via link.xml and test such content in a real build.",
                            "Player Settings 的 Strip Engine Code 未开启。在 IL2CPP 平台（含 WebGL）上，开启后会从产物中剥离未使用的引擎原生代码，" +
                            "常见可省数 MB——它与只作用于 C# 程序集的 Managed Stripping Level 是两个独立开关。注意：仅被运行时加载内容" +
                            "（AssetBundle/Addressables）引用的引擎类型可能被剥掉——用 link.xml 显式保留，并在真机构建中验证这类内容。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Enable Strip Engine Code", "开启 Strip Engine Code"),
                        confirmMessage: L.Tr("Set Strip Engine Code to on in Player Settings.\n" +
                                        "If AssetBundle/Addressables content is the only user of some engine types, preserve them via link.xml and verify in a real build.\n" +
                                        "This modifies project settings (ProjectSettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                                        "将 Player Settings 的 Strip Engine Code 设为开启。\n" +
                                        "若某些引擎类型只被 AssetBundle/Addressables 内容使用，请用 link.xml 显式保留并在真机构建中验证。\n" +
                                        "此操作修改项目设置（ProjectSettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            PlayerSettings.stripEngineCode = true;
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("Strip Engine Code enabled.", "已开启 Strip Engine Code。"));
                        }));
            }

            // PROJ012 — IL2CPP Code Generation favors speed on a size-sensitive target (WebGL, or mobile with IL2CPP; includes executable action)
            bool sizeSensitiveIl2Cpp = resolvedGroup == BuildTargetGroup.WebGL
                || ((resolvedGroup == BuildTargetGroup.Android || resolvedGroup == BuildTargetGroup.iOS)
                    && PlayerSettings.GetScriptingBackend(resolvedGroup) == ScriptingImplementation.IL2CPP);
            if (sizeSensitiveIl2Cpp && IsIl2CppCodeGenOptimizeSpeed(resolvedGroup))
            {
                var cgGroup = resolvedGroup; // capture for the action delegate
                yield return new Finding(
                    ruleId: "PROJ012",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Info,
                    title: L.Tr("IL2CPP Code Generation favors speed on a size-sensitive target", "IL2CPP Code Generation 在体积敏感平台上偏向速度"),
                    detail: L.Tr($"IL2CPP Code Generation is set to Faster runtime (OptimizeSpeed) for {resolvedGroup}. Faster (smaller) builds (OptimizeSize) shares generic " +
                            "method bodies instead of expanding each instantiation, noticeably cutting binary size and build time; the runtime cost is small and rarely visible " +
                            "outside generics-heavy hot loops. On WebGL, where download size is load time, OptimizeSize is the usual choice; verify performance afterward.",
                            $"{resolvedGroup} 平台的 IL2CPP Code Generation 为 Faster runtime（OptimizeSpeed）。Faster (smaller) builds（OptimizeSize）会共享泛型方法体" +
                            "而非逐实例展开，可明显减小产物体积并缩短构建时间；运行时代价很小，通常只在泛型密集的热循环中可见。" +
                            "WebGL 上下载体积即加载时长，一般选 OptimizeSize；切换后请验证性能。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Set to Faster (smaller) builds", "设为 Faster (smaller) builds"),
                        confirmMessage: L.Tr($"Set IL2CPP Code Generation to Faster (smaller) builds (OptimizeSize) for {resolvedGroup}.\n" +
                                        "Tradeoff: generic-heavy hot paths may run slightly slower; profile if your game leans on generics in per-frame code.\n" +
                                        "This modifies project settings and cannot be reverted with Edit > Undo; commit to version control first.",
                                        $"将 {resolvedGroup} 平台的 IL2CPP Code Generation 设为 Faster (smaller) builds（OptimizeSize）。\n" +
                                        "代价：泛型密集的热路径可能略微变慢；若逐帧代码重度依赖泛型请自行 profile。\n" +
                                        "此操作修改项目设置，无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            SetIl2CppCodeGenOptimizeSize(cgGroup);
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("IL2CPP Code Generation set to Faster (smaller) builds.", "IL2CPP Code Generation 已设为 Faster (smaller) builds。"));
                        }));
            }
        }

        /// <summary>
        /// Resolves the build target group platform-gated rules evaluate against. Respects an injected
        /// ScanContext.TargetPlatform (importer-override names: "Android"/"iPhone"/"WebGL"/"Standalone") for tests and
        /// multi-platform diagnostics — an unrecognized injected name resolves to Unknown so gated rules stay silent
        /// rather than falling back to the active target. Otherwise uses the active build target.
        /// </summary>
        private static BuildTargetGroup ResolveGroup(ScanContext context)
        {
            if (!string.IsNullOrEmpty(context.TargetPlatform))
            {
                switch (context.TargetPlatform)
                {
                    case "Android": return BuildTargetGroup.Android;
                    case "iPhone": return BuildTargetGroup.iOS;
                    case "WebGL": return BuildTargetGroup.WebGL;
                    case "Standalone": return BuildTargetGroup.Standalone;
                    default: return BuildTargetGroup.Unknown;
                }
            }
            return BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>Mobile filter over ResolveGroup for PROJ008: Unknown for any non-mobile target (the rule is mobile-only).</summary>
        private static BuildTargetGroup ResolveMobileGroup(ScanContext context)
        {
            var g = ResolveGroup(context);
            return (g == BuildTargetGroup.Android || g == BuildTargetGroup.iOS) ? g : BuildTargetGroup.Unknown;
        }

        /// <summary>
        /// IL2CPP Code Generation accessors for PROJ012, bridging the 2021.3 global EditorUserBuildSettings property and
        /// the 2022.1+ per-target PlayerSettings API (the EditorUserBuildSettings property is obsolete there — do not
        /// reference it outside the version guard, see the CS0619 cross-version trap in the ledger).
        /// </summary>
        private static bool IsIl2CppCodeGenOptimizeSpeed(BuildTargetGroup group)
        {
#if UNITY_2022_1_OR_NEWER
            return PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.FromBuildTargetGroup(group))
                == Il2CppCodeGeneration.OptimizeSpeed;
#else
            return EditorUserBuildSettings.il2CppCodeGeneration == Il2CppCodeGeneration.OptimizeSpeed;
#endif
        }

        private static void SetIl2CppCodeGenOptimizeSize(BuildTargetGroup group)
        {
#if UNITY_2022_1_OR_NEWER
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.FromBuildTargetGroup(group), Il2CppCodeGeneration.OptimizeSize);
#else
            EditorUserBuildSettings.il2CppCodeGeneration = Il2CppCodeGeneration.OptimizeSize;
#endif
        }

        private static int CountAlwaysIncludedShaders()
        {
            try
            {
                var settings = GraphicsSettings.GetGraphicsSettings();
                var so = new SerializedObject(settings);
                var prop = so.FindProperty("m_AlwaysIncludedShaders");
                return prop != null ? prop.arraySize : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
