using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
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
    /// (Dynamic batching detection has been removed because there is no stable public API to read it.)
    /// Project-level findings have no specific asset path (targetPath is null). PROJ003/005 include a FindingAction
    /// (modifies project settings, not undoable, Pro-gated).
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
