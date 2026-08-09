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
    ///   MIG.SerializeFieldOnNonField — [SerializeField] attached to an enum/class/method: silently accepted on older editors,
    ///     CS0592 on Unity 6. A delayed-action upgrade blocker that costs nothing until the day you move.
    ///   MIG.RenderGraphMissing — a ScriptableRenderPass that only implements the Compatibility-Mode Execute() while the project renders through
    ///     Render Graph (the Unity 6 default): it compiles cleanly and silently contributes nothing. The one rule here that compile-error
    ///     ingestion can never reach — there is no error to ingest.
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
            public bool RequiresUnity2022_1; // Only report when the current Unity is ≥2022.1 (e.g. URP 13 marking RenderTargetHandle obsolete); on 2021 LTS it is still the current API
            // Optional exact gate: report only when this returns true. Sharper than version thresholds for APIs whose
            // deprecation point is hard to pin (e.g. GetInstanceID somewhere in the 6000.x line): reflect over the
            // CURRENT engine and ask whether the member actually carries [Obsolete] — zero guessing, zero noise on
            // editors where the API is still current. Null = always active (subject to the version flags above).
            public System.Func<bool> ActiveWhen;
            // Severity policy: Critical = blocks compilation on the current editor (removed API / error-level
            // obsolete), Warning = deprecation warning only. Receives unity2023_1Plus for rules whose "breaking
            // from" line is 2023.1 (e.g. RenderTargetHandle). Null = Warning.
            public System.Func<bool, Severity> SeverityFn;
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
                // Fact check (Tim, 2026-07-04): WWW is warning-level obsolete since 2018.3 and STILL COMPILES on
                // Unity 6 — deprecated, unmaintained, but not removed. Title/severity must not claim otherwise.
                RuleId = "MIG.WWW", Title = () => L.Tr("Deprecated API: WWW", "废弃 API：WWW"),
                Detail = () => L.Tr("WWW has been deprecated since Unity 2018.3 — it still compiles (with a warning) even on Unity 6, but is unmaintained and slated for removal. "
                         + "Migrate to UnityWebRequest (requires using UnityEngine.Networking). This is a structural migration, not a rename: "
                         + "the async model, DownloadHandler, result checks, and variable scoping all differ, so the whole request flow must be rewritten by hand; no one-click fix.",
                         "WWW 自 Unity 2018.3 起废弃——在 Unity 6 上仍能编译（警告级），但已停止维护、列入移除计划。"
                         + "建议迁移到 UnityWebRequest（需 using UnityEngine.Networking）。这是结构性迁移而非改名："
                         + "异步模型、DownloadHandler、result 检查、变量作用域都不同，需人工整体改写该请求流程，不提供一键修复。"),
                AllowAiFix = false
            },
            new ApiRule {
                Pattern = new Regex(@"\bApplication\.LoadLevel", RegexOptions.Compiled),
                RuleId = "MIG.LoadLevel", Title = () => L.Tr("Removed API: Application.LoadLevel", "已移除 API：Application.LoadLevel"),
                Detail = () => L.Tr("Application.LoadLevel/LoadLevelAsync has been removed. Use SceneManager.LoadScene (UnityEngine.SceneManagement).",
                              "Application.LoadLevel/LoadLevelAsync 已移除。改用 SceneManager.LoadScene（UnityEngine.SceneManagement）。"),
                SeverityFn = _ => Severity.Critical // removed on every supported editor → guaranteed compile error
            },
            new ApiRule {
                Pattern = new Regex(@"\bGUIText\b|\bGUITexture\b", RegexOptions.Compiled),
                RuleId = "MIG.GUIText", Title = () => L.Tr("Removed components: GUIText/GUITexture", "已移除组件：GUIText/GUITexture"),
                Detail = () => L.Tr("GUIText/GUITexture have been removed. Use UGUI (Text/Image) or TextMeshPro. This is a structural replacement (components/prefabs/references all change), so it must be migrated by hand; no one-click fix.",
                              "GUIText/GUITexture 已移除。改用 UGUI（Text/Image）或 TextMeshPro。这是结构性替换（组件/预制体/引用都要换），需人工迁移，不提供一键修复。"),
                AllowAiFix = false,
                SeverityFn = _ => Severity.Critical // removed on every supported editor → guaranteed compile error
            },
            new ApiRule {
                Pattern = new Regex(@"\bGetInstanceID\b", RegexOptions.Compiled),
                RuleId = "MIG.GetInstanceID", Title = () => L.Tr("Deprecated API: GetInstanceID", "废弃 API：GetInstanceID"),
                Detail = () => L.Tr("Object.GetInstanceID() is marked obsolete on this Unity version (newer Unity 6 releases; compile error where obsolete-as-error). " +
                              "Migrate to GetEntityId() — but note this is NOT a plain rename: the EntityId→int implicit conversion is also obsolete there, " +
                              "so every int variable/field/dictionary key that receives the id must change type to EntityId as well (EntityId is comparable " +
                              "and works as a dictionary key). PerfLint's Migration Assistant can rewrite the file for you (AI Migrate; it decides per " +
                              "call site between a local unique-key counter and the full EntityId migration), or rewrite the id's flow by hand.",
                              "Object.GetInstanceID() 在当前 Unity 版本已标记废弃（较新的 Unity 6 版本；error 级废弃时直接编译失败）。" +
                              "迁移到 GetEntityId()——但注意这不是单纯改名：EntityId→int 的隐式转换在该版本同样废弃，" +
                              "接收该 id 的所有 int 变量/字段/字典 key 都要连带改为 EntityId 类型（EntityId 可比较、可作字典 key）。" +
                              "可用迁移助手整体重写此文件（AI Migrate，按调用点在「本地唯一键计数器」与「完整 EntityId 迁移」间取舍），或手动改写该 id 的整条流转。"),
                // Reflect over the CURRENT engine: report only where GetInstanceID actually carries [Obsolete]
                // (real case: CS0619 on 6000.5 while 2022.3 is perfectly fine — a version threshold would be a guess).
                ActiveWhen = () => GetInstanceIdIsObsolete,
                // Error-level obsolete ([Obsolete(msg, true)] → CS0619) blocks compilation → Critical; warning-level → Warning.
                SeverityFn = _ => GetInstanceIdObsoleteIsError ? Severity.Critical : Severity.Warning,
                // Structural after all: on 6000.5 the EntityId→int implicit operator is error-level obsolete too, so a
                // call-site swap breaks every int receiver (real rollback). The id's type must migrate through the file.
                AllowAiFix = false
            },
            new ApiRule {
                // Word boundaries keep variable names (renderTargetHandle / m_RenderTargetHandle) out; only the type name itself matches.
                Pattern = new Regex(@"\bRenderTargetHandle\b", RegexOptions.Compiled),
                RuleId = "MIG.RenderTargetHandle", Title = () => L.Tr("Deprecated API: RenderTargetHandle (URP)", "废弃 API：RenderTargetHandle（URP）"),
                Detail = () => L.Tr("URP's RenderTargetHandle is deprecated since Unity 2022.1 (URP 13) and removed in Unity 6 — custom render passes that use it no longer compile there "
                         + "(a top blocker when upgrading older URP assets). Migrate to RTHandle: allocate in OnCameraSetup via RTHandles.Alloc / RenderingUtils.ReAllocateIfNeeded, "
                         + "pass the handle itself instead of .Identifier()/.id, and release it explicitly (Dispose). This changes the pass's whole resource lifecycle; "
                         + "PerfLint's Migration Assistant can rewrite the file for you (AI Migrate, probing the pass shape your URP actually exposes), or migrate it by hand.",
                         "URP 的 RenderTargetHandle 自 Unity 2022.1（URP 13）废弃、Unity 6 已移除——使用它的自定义 render pass 在 Unity 6 无法编译"
                         + "（老 URP 资产升级的头号阻塞点）。迁移到 RTHandle：在 OnCameraSetup 用 RTHandles.Alloc / RenderingUtils.ReAllocateIfNeeded 分配，"
                         + "直接传句柄（不再用 .Identifier()/.id），并需显式释放（Dispose）。整个 pass 的资源生命周期都要改；"
                         + "可用迁移助手整体重写此文件（AI Migrate，按你的 URP 实际提供的 pass 形态迁移），或人工整体改写。"),
                RequiresUnity2022_1 = true,
                AllowAiFix = false,
                // #breakingFrom(2023.1): error-level (blocks compilation) on 2023.1+/6 → Critical there; warning-level obsolete on 2022 → Warning.
                SeverityFn = unity2023_1Plus => unity2023_1Plus ? Severity.Critical : Severity.Warning
            },
            new ApiRule {
                // Member access only (".cameraColorTarget"), and \b keeps the *Handle successors out:
                // in "cameraColorTargetHandle" the character after the match is a word char, so \b fails there.
                Pattern = new Regex(@"\.(cameraColorTarget|cameraDepthTarget)\b", RegexOptions.Compiled),
                RuleId = "MIG.CameraColorTarget", Title = () => L.Tr("Removed API: cameraColorTarget / cameraDepthTarget (URP)", "已移除 API：cameraColorTarget / cameraDepthTarget（URP）"),
                Detail = () => L.Tr("URP's ScriptableRenderer.cameraColorTarget / cameraDepthTarget are deprecated since Unity 2022.1 and error-level from 2023.2 — "
                         + "on Unity 6 they no longer compile, and the property body itself throws. Use cameraColorTargetHandle / cameraDepthTargetHandle. "
                         + "This is not a plain rename: the type changes from RenderTargetIdentifier to RTHandle, so the variables/fields receiving it and everything "
                         + "downstream (Blit targets, ConfigureTarget, SetComputeTextureParam) must move to RTHandle as well. "
                         + "PerfLint's Migration Assistant can rewrite the file for you (AI Migrate), or migrate the pass by hand.",
                         "URP 的 ScriptableRenderer.cameraColorTarget / cameraDepthTarget 自 Unity 2022.1 废弃、2023.2 起为 error 级——"
                         + "在 Unity 6 上无法编译，且属性本身实现就是抛异常。改用 cameraColorTargetHandle / cameraDepthTargetHandle。"
                         + "这不是单纯改名：类型从 RenderTargetIdentifier 变成 RTHandle，接收它的变量/字段以及下游用法"
                         + "（Blit 目标、ConfigureTarget、SetComputeTextureParam）都要一并改为 RTHandle。"
                         + "可用迁移助手整体重写此文件（AI Migrate），或人工迁移该 pass。"),
                RequiresUnity2022_1 = true,
                AllowAiFix = false,
                // #breakingFrom(2023.2): error-level there. IsAtLeast2023_2 is read from the live engine rather than
                // from the caller's flag — the caller only carries a 2023.1 threshold, and 2023.1 is still warning-level.
                SeverityFn = _ => IsAtLeast2023_2(Application.unityVersion) ? Severity.Critical : Severity.Warning
            },
            new ApiRule {
                Pattern = new Regex(@"\bVolumeComponentMenuForRenderPipeline\b", RegexOptions.Compiled),
                RuleId = "MIG.VolumeComponentMenuForRenderPipeline", Title = () => L.Tr("Removed API: VolumeComponentMenuForRenderPipeline", "已移除 API：VolumeComponentMenuForRenderPipeline"),
                Detail = () => L.Tr("VolumeComponentMenuForRenderPipelineAttribute is error-level obsolete from Unity 2023.1 — custom Volume components using it no longer compile on Unity 6. "
                         + "Split it into two attributes: [VolumeComponentMenu(\"Post-processing/Your Effect\")] for the menu path, and "
                         + "[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))] for the pipeline filter. "
                         + "Mind the type: SupportedOnRenderPipeline takes the pipeline *Asset* type (UniversalRenderPipelineAsset / HDRenderPipelineAsset), "
                         + "not the pipeline type (UniversalRenderPipeline) the old attribute took.",
                         "VolumeComponentMenuForRenderPipelineAttribute 自 Unity 2023.1 起为 error 级废弃——用到它的自定义 Volume 组件在 Unity 6 无法编译。"
                         + "拆成两个特性：菜单路径用 [VolumeComponentMenu(\"Post-processing/Your Effect\")]，"
                         + "管线过滤用 [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]。"
                         + "注意类型不同：SupportedOnRenderPipeline 接收管线 **Asset** 类型（UniversalRenderPipelineAsset / HDRenderPipelineAsset），"
                         + "而不是旧特性接收的管线类型（UniversalRenderPipeline）。"),
                RequiresUnity2023_1 = true,
                SeverityFn = _ => Severity.Critical // error-level obsolete from 2023.1 → blocks compilation wherever this rule is active
            },
            new ApiRule {
                // Static class → only ever appears as a member access; requiring the dot keeps user-defined
                // identifiers that merely contain the word out of the results.
                Pattern = new Regex(@"\bXRGraphics\s*\.", RegexOptions.Compiled),
                RuleId = "MIG.XRGraphics", Title = () => L.Tr("Removed API: XRGraphics (URP)", "已移除 API：XRGraphics（URP）"),
                Detail = () => L.Tr("URP's XRGraphics helper was removed in the Unity 6 URP line, so code referencing it fails with CS0103 (the name does not exist). "
                         + "It only ever forwarded to the XR module, so the replacement is a rename: XRGraphics.enabled → UnityEngine.XR.XRSettings.enabled, "
                         + "XRGraphics.eyeTextureResolutionScale → XRSettings.eyeTextureResolutionScale, XRGraphics.renderViewportScale → XRSettings.renderViewportScale.",
                         "URP 的 XRGraphics 辅助类在 Unity 6 的 URP 线上已移除，引用它的代码报 CS0103（名称不存在）。"
                         + "它本来就只是转发给 XR 模块，所以替换是改名级：XRGraphics.enabled → UnityEngine.XR.XRSettings.enabled，"
                         + "XRGraphics.eyeTextureResolutionScale → XRSettings.eyeTextureResolutionScale，XRGraphics.renderViewportScale → XRSettings.renderViewportScale。"),
                // Reflect over THIS project's URP rather than pin a version: the release that dropped XRGraphics is
                // hard to pin (still present in URP 14 / 2022.3, gone on 6000.3), and asking the engine costs nothing.
                ActiveWhen = () => XRGraphicsRemoved,
                SeverityFn = _ => Severity.Critical // the type is gone → CS0103, guaranteed compile error wherever this is active
            },
            new ApiRule {
                Pattern = new Regex(@"\bParticleEmitter\b|\bParticleRenderer\b|\bParticleAnimator\b", RegexOptions.Compiled),
                RuleId = "MIG.LegacyParticles", Title = () => L.Tr("Removed: legacy particle components", "已移除：Legacy 粒子组件"),
                Detail = () => L.Tr("Legacy particles (ParticleEmitter/Renderer/Animator) have been removed. Use the Shuriken Particle System. This is a structural replacement, so it must be migrated by hand; no one-click fix.",
                              "Legacy 粒子（ParticleEmitter/Renderer/Animator）已移除。改用 Shuriken Particle System。这是结构性替换，需人工迁移，不提供一键修复。"),
                AllowAiFix = false,
                SeverityFn = _ => Severity.Critical // removed on every supported editor → guaranteed compile error
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
            bool unity2022_1Plus = IsAtLeast2022_1(Application.unityVersion);

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
                foreach (var f in ScanScript(path, unity2022_1Plus, unity2023_1Plus, newInputOnly))
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

        /// <summary>
        /// The migration script rules cover all .cs files (including the Editor directory: Editor scripts can equally be broken at compile time by removed APIs).
        /// PerfLint's own shipped scripts are excluded — in the Asset Store install form they live under Assets/, so otherwise we'd diagnose ourselves
        /// (e.g. SceneBatchingAnalyzer's deliberate FindObjectsOfType 2021.3-compat call), which is noise the user can't act on.
        /// </summary>
        public bool Handles(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".cs")
            && !ScannerUtil.IsPerfLintOwnAsset(assetPath);

        /// <summary>
        /// Single-file incremental: only recompute this script's deprecated-API / legacy-input-API findings (project-level rules such as manifest and input backend are excluded).
        /// The version and input-backend checks are read inline here — single-file calls are infrequent, so the cost is negligible.
        /// </summary>
        public IEnumerable<Finding> ScanFile(string assetPath, ScanContext context)
        {
            if (!Handles(assetPath)) yield break;
            bool unity2023_1Plus = IsAtLeast2023_1(Application.unityVersion);
            bool unity2022_1Plus = IsAtLeast2022_1(Application.unityVersion);
            bool newInputOnly = ReadActiveInputHandler() == 1;
            foreach (var f in ScanScript(assetPath, unity2022_1Plus, unity2023_1Plus, newInputOnly))
                yield return f;
        }

        /// <summary>Match deprecated APIs and legacy input APIs line by line in a single script. Shared by the full Scan and the single-file ScanFile to guarantee both paths produce consistent results.</summary>
        private IEnumerable<Finding> ScanScript(string path, bool unity2022_1Plus, bool unity2023_1Plus, bool newInputOnly)
        {
            var lines = ReadLines(path);
            if (lines == null) yield break;
            foreach (var f in ScanSource(lines, path, unity2022_1Plus, unity2023_1Plus, newInputOnly)) yield return f;

            // Both of these need more than one line to decide, so they sit outside the line-by-line ScanSource:
            // one looks at the declaration an attribute is attached to, the other at the whole file's entry points.
            foreach (var f in ScanSerializeFieldTargets(lines, path, SerializeFieldIsFieldOnly)) yield return f;

            var rg = ScanRenderGraphPass(lines, path, RenderGraphActive);
            if (rg != null) yield return rg;
        }

        // ── [SerializeField] on a declaration that isn't a field ──────────────────────────────────
        // Matches the attribute anywhere in a bracket group (so "[SerializeField, Tooltip(…)]" counts), but never
        // "[field: SerializeField]" — the targeted form on an auto-property is legal and compiles everywhere.
        private static readonly Regex SerializeFieldAttr = new Regex(@"\[[^\]]*\bSerializeField\b[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex FieldTargetedSerializeField = new Regex(@"\bfield\s*:\s*SerializeField\b", RegexOptions.Compiled);
        // Only shapes that CANNOT be a field. Everything else — including anything ambiguous — stays silent.
        private static readonly Regex NonFieldTypeDecl = new Regex(@"\b(enum|class|struct|interface|delegate|event)\s+\w", RegexOptions.Compiled);
        private static readonly Regex VoidMethodDecl = new Regex(@"\bvoid\s+\w+\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// Pure logic: classify the declaration an attribute is attached to. Returns "type" or "method" when it
        /// definitely is NOT a field, and null for fields and for anything we cannot be certain about — a field
        /// declaration wrongly reported here would send the user to delete an attribute that is doing real work,
        /// so silence is the default.
        /// </summary>
        internal static string ClassifyNonFieldDeclaration(string decl)
        {
            if (string.IsNullOrEmpty(decl)) return null;
            if (NonFieldTypeDecl.IsMatch(decl)) return "type";
            if (VoidMethodDecl.IsMatch(decl)) return "method";
            return null;
        }

        /// <summary>
        /// Pure logic: find <c>[SerializeField]</c> sitting on an enum/class/method rather than a field.
        /// Measured, not assumed: on Unity 2022.3 the compiler accepts this silently (a clean project, zero errors),
        /// and on Unity 6 the same code fails with CS0592 — so a project that upgrades finds a batch of these at once,
        /// and they hide behind whichever assembly failed first. The attribute never did anything on a non-field, so
        /// the fix is to delete it, on any version.
        /// <paramref name="serializeFieldIsFieldOnly"/> comes from reflecting over THIS engine's
        /// <c>AttributeUsage</c>, which decides whether this already blocks compilation (Critical) or is still just
        /// dead weight waiting for the upgrade (Warning).
        /// </summary>
        internal static IEnumerable<Finding> ScanSerializeFieldTargets(string[] lines, string path, bool serializeFieldIsFieldOnly)
        {
            if (lines == null) yield break;

            for (int ln = 0; ln < lines.Length; ln++)
            {
                string code = StripNonCode(lines[ln]);
                if (!SerializeFieldAttr.IsMatch(code)) continue;
                if (FieldTargetedSerializeField.IsMatch(code)) continue;

                // The declaration is whatever follows the last ']' on this line; if nothing does, it is the next
                // line that is neither blank nor another attribute. Reading the same line FIRST is what keeps the
                // common "[SerializeField] private Foo m_foo;" from being judged by the line below it.
                string decl = null;
                int close = code.LastIndexOf(']');
                if (close >= 0 && close + 1 < code.Length)
                {
                    string rest = code.Substring(close + 1).Trim();
                    if (rest.Length > 0) decl = rest;
                }
                for (int j = ln + 1; decl == null && j < lines.Length; j++)
                {
                    string c = StripNonCode(lines[j]).Trim();
                    if (c.Length == 0 || c.StartsWith("[")) continue;
                    decl = c;
                }

                string kind = ClassifyNonFieldDeclaration(decl);
                if (kind == null) continue;

                string cap = path;
                int line = ln + 1;
                bool isType = kind == "type";
                yield return new Finding(
                    ruleId: "MIG.SerializeFieldOnNonField",
                    domain: Domain.Migration,
                    severity: serializeFieldIsFieldOnly ? Severity.Critical : Severity.Warning,
                    title: L.Tr("[SerializeField] on something that isn't a field", "[SerializeField] 贴在了非字段上"),
                    detail: L.Tr($"This [SerializeField] is attached to a {(isType ? "type declaration" : "method")}, not a field. "
                            + "SerializeField only ever applied to fields, so it has never done anything here — but older editors accept it silently "
                            + "(measured: a project full of these compiles clean on 2022.3), while Unity 6 rejects it with CS0592 and the assembly stops building. "
                            + "That makes it a delayed-action upgrade blocker: it costs nothing today and breaks the build the day you move to Unity 6, "
                            + "usually as a batch, and usually hidden behind whichever assembly failed first. Delete the attribute — on any version, "
                            + "removing it changes no behaviour. (The targeted form [field: SerializeField] on an auto-property is a different thing and is legal.)",
                            $"这个 [SerializeField] 贴在{(isType ? "类型声明" : "方法")}上，不是字段。"
                            + "SerializeField 从来就只对字段生效，所以它在这里从没起过作用——但老版本编辑器会静默接受"
                            + "（实测：满是这种写法的工程在 2022.3 上编译全绿），而 Unity 6 直接报 CS0592、该程序集停止构建。"
                            + "这是一个延迟生效的升级阻塞点：今天零代价，等你升到 Unity 6 那天集中爆发，而且通常被最先失败的那个程序集挡在后面看不见。"
                            + "删掉这个特性即可——在任何版本上删它都不改变行为。（自动属性上的定向写法 [field: SerializeField] 是另一回事，合法。）"),
                    targetPath: $"{path}:{line}",
                    ping: () => OpenAt(cap, line),
                    // Deleting one attribute is the textbook fragment-level edit.
                    codeFile: cap,
                    codeLine: line);
            }
        }

        /// <summary>
        /// Whether <c>SerializeField</c> carries <c>AttributeUsage(AttributeTargets.Field)</c> on THIS engine — i.e.
        /// whether misplacing it is already a compile error (CS0592) rather than silently-accepted dead weight.
        /// Reflected rather than pinned to a version: measured false on 2022.3 and true on 6000.3, with the exact
        /// changeover in between unknown. Internal and writable so tests can drive both paths.
        /// </summary>
        internal static bool SerializeFieldIsFieldOnly = ComputeSerializeFieldIsFieldOnly();

        private static bool ComputeSerializeFieldIsFieldOnly()
        {
            try
            {
                var usage = (System.AttributeUsageAttribute)System.Attribute.GetCustomAttribute(
                    typeof(SerializeField), typeof(System.AttributeUsageAttribute));
                return usage != null && usage.ValidOn == System.AttributeTargets.Field;
            }
            catch { return false; }
        }

        // ── Render Graph silent-no-op detection ───────────────────────────────────────────────────
        // Inheritance list only ("class X : ScriptableRenderPass" / ", ScriptableRenderPass"), so a bare mention
        // elsewhere in the file does not qualify it.
        private static readonly Regex DerivesFromRenderPass = new Regex(@"[:,]\s*ScriptableRenderPass\b", RegexOptions.Compiled);
        // The compatibility-mode entry point. Matching the parameter type (not just the method name) keeps
        // unrelated Execute() methods out.
        private static readonly Regex CompatibilityExecute = new Regex(@"\boverride\s+void\s+Execute\s*\(\s*ScriptableRenderContext\b", RegexOptions.Compiled);
        private static readonly Regex RecordRenderGraphMember = new Regex(@"\bRecordRenderGraph\b", RegexOptions.Compiled);

        /// <summary>
        /// Pure logic: does this file define a ScriptableRenderPass that implements ONLY the Compatibility-Mode
        /// <c>Execute(ScriptableRenderContext, …)</c> and never <c>RecordRenderGraph</c>? Under Render Graph — the
        /// Unity 6 default — URP's base RecordRenderGraph only logs a warning and skips the pass: the effect is
        /// silently gone while the file still compiles cleanly. No compile error, no inspector warning, nothing in
        /// the editor points at it — which is exactly why this needs a rule (compile-error ingestion can never see it).
        /// Returns null when it does not apply: not a pass, already implements RecordRenderGraph, or Render Graph
        /// is not the active path (Compatibility Mode on / pre-Unity-6 / URP absent).
        /// </summary>
        internal static Finding ScanRenderGraphPass(string[] lines, string path, bool renderGraphActive)
        {
            return ScanRenderGraphPass(lines, path, renderGraphActive, CompatibilityModeReachable);
        }

        /// <summary>
        /// Overload taking the "can this project still fall back to Compatibility Mode?" answer explicitly, so the
        /// advice never points at a switch that isn't there. From Unity 6.3 URP hides the setting and strips the
        /// code unless URP_COMPATIBILITY_MODE is in Player Settings' Scripting Define Symbols — advice that says
        /// "turn it back on under Project Settings ▸ Graphics" sends those users hunting for a menu that is gone.
        /// </summary>
        internal static Finding ScanRenderGraphPass(string[] lines, string path, bool renderGraphActive, bool compatibilityModeReachable)
        {
            if (!renderGraphActive || lines == null || lines.Length == 0) return null;

            bool derivesFromPass = false, hasRecordRenderGraph = false;
            int executeLine = 0;
            for (int ln = 0; ln < lines.Length; ln++)
            {
                string code = StripNonCode(lines[ln]);
                if (code.Trim().Length == 0) continue;
                if (!derivesFromPass && DerivesFromRenderPass.IsMatch(code)) derivesFromPass = true;
                if (!hasRecordRenderGraph && RecordRenderGraphMember.IsMatch(code)) hasRecordRenderGraph = true;
                if (executeLine == 0 && CompatibilityExecute.IsMatch(code)) executeLine = ln + 1;
            }
            // A dual-shape pass (both entry points present) is what a correct migration looks like → stay silent.
            if (!derivesFromPass || hasRecordRenderGraph || executeLine == 0) return null;

            string cap = path;
            int capLine = executeLine;
            return new Finding(
                ruleId: "MIG.RenderGraphMissing",
                domain: Domain.Migration,
                // Critical on observed behaviour, not on a guess: URP's ScriptableRenderPass.RecordRenderGraph base
                // implementation logs "does not have an implementation of the RecordRenderGraph method … the render
                // pass will have no effect" and returns. The pass contributes nothing to the frame.
                severity: Severity.Critical,
                title: L.Tr("Render pass does nothing under Render Graph", "Render Graph 下此 render pass 完全不执行"),
                detail: L.Tr("This ScriptableRenderPass only implements the Compatibility Mode entry point (Execute(ScriptableRenderContext, …)), "
                        + "but this project renders through Render Graph — the Unity 6 default. URP's base RecordRenderGraph implementation just logs a "
                        + "warning and skips the pass, so this effect contributes nothing to the frame. It still compiles cleanly, which is why nothing "
                        + "else in the editor flags it: the only symptom is that the effect is gone.\n"
                        + "Two ways out. (1) Migrate the pass: implement RecordRenderGraph(RenderGraph, ContextContainer) — for a direct port, "
                        + "renderGraph.AddUnsafePass<PassData> plus builder.SetRenderFunc lets you keep the existing command logic, and the camera targets "
                        + "come from frameData.Get<UniversalResourceData>().activeColorTexture. PerfLint's Migration Assistant can rewrite the file for you (AI Migrate). "
                        + (compatibilityModeReachable
                            ? "(2) As a stopgap, turn Compatibility Mode back on under Edit ▸ Project Settings ▸ Graphics ▸ Render Graph — the old path runs again, "
                              + "but it is deprecated and you lose the Render Graph optimizations."
                            : "(2) There is no quick fallback left on this Unity version: URP hides the Compatibility Mode setting from 6.3 and strips its code. "
                              + "Reaching it means adding URP_COMPATIBILITY_MODE to Edit ▸ Project Settings ▸ Player ▸ Scripting Define Symbols, which Unity itself labels "
                              + "\"not recommended or supported\" and is slated for removal — so treat it as a few days' breathing room at most, not a destination. Migrating the pass is the real fix."),
                        "此 ScriptableRenderPass 只实现了 Compatibility Mode 的入口（Execute(ScriptableRenderContext, …)），"
                        + "而本工程走的是 Render Graph——Unity 6 的默认路径。URP 基类的 RecordRenderGraph 实现只会打一条警告然后跳过该 pass，"
                        + "所以这个效果对画面毫无贡献。它照样编译通过，所以编辑器里没有任何别的东西会提示你：唯一的症状就是效果没了。\n"
                        + "两条出路。(1) 迁移该 pass：实现 RecordRenderGraph(RenderGraph, ContextContainer)——要直迁的话，"
                        + "renderGraph.AddUnsafePass<PassData> 配 builder.SetRenderFunc 可以保留原有的命令逻辑，相机目标从 "
                        + "frameData.Get<UniversalResourceData>().activeColorTexture 取。可用迁移助手整体重写此文件（AI Migrate）。"
                        + (compatibilityModeReachable
                            ? "(2) 过渡方案：在 Edit ▸ Project Settings ▸ Graphics ▸ Render Graph 里重新打开 Compatibility Mode——旧路径会重新执行，"
                              + "但它已被废弃，且会失去 Render Graph 的优化。"
                            : "(2) 当前 Unity 版本已经没有快速退路：URP 从 6.3 起隐藏了 Compatibility Mode 设置并剥离其代码。"
                              + "要够到它得在 Edit ▸ Project Settings ▸ Player ▸ Scripting Define Symbols 里加 URP_COMPATIBILITY_MODE，"
                              + "而 Unity 自己把这条标注为「不推荐、不支持」且已列入移除计划——所以最多把它当几天喘息时间，不是归宿。迁移该 pass 才是真正的修法。")),
                targetPath: $"{path}:{executeLine}",
                // Deliberately no codeFile: porting a pass to Render Graph rewrites its whole structure — beyond
                // what a fragment-level AI Fix can do (same call as WWW / RenderTargetHandle).
                ping: () => OpenAt(cap, capLine));
        }

        /// <summary>Pure logic: match deprecated APIs / legacy input APIs against already-loaded source lines (no file I/O, making it easy to verify false positives/negatives in end-to-end unit tests).</summary>
        internal static IEnumerable<Finding> ScanSource(string[] lines, string path, bool unity2022_1Plus, bool unity2023_1Plus, bool newInputOnly)
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
                    if (rule.RequiresUnity2022_1 && !unity2022_1Plus) continue;
                    if (rule.ActiveWhen != null && !rule.ActiveWhen()) continue;
                    if (!rule.Pattern.IsMatch(code)) continue;
                    yield return new Finding(
                        ruleId: rule.RuleId,
                        domain: Domain.Migration,
                        severity: rule.SeverityFn?.Invoke(unity2023_1Plus) ?? Severity.Warning,
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
                    // Warning, NOT Critical: Unity silently SKIPS unresolved asmdef references and compiles the
                    // assembly anyway (the Inspector greys them out: "missing and will not be referenced during
                    // compilation"). Real-world proof: Viking Village's WaterSystem.Runtime.asmdef carries a
                    // dangling GUID reference while the project compiles and runs fine. The actual risk is
                    // conditional — code that USES types from the missing assembly fails with CS0246, and the
                    // feature the reference existed for may be silently absent.
                    severity: Severity.Warning,
                    title: L.Tr("Assembly Definition has unresolved references", "程序集定义存在无法解析的引用"),
                    detail: L.Tr($"{path} references assemblies that cannot be resolved: {list}. " +
                            "These were likely renamed or removed during a package/Unity migration, or left behind when the asset was extracted from a larger project. " +
                            "Unity skips unresolved references and still compiles this assembly (the Inspector shows them greyed out) — so if your project compiles and the feature works, this is leftover clutter you can remove safely. " +
                            "It becomes a real problem only when code in this assembly needs types from the missing assembly (compile errors), or when the reference pointed at a package that should be installed — then reinstall it or repoint the reference instead.",
                            $"{path} 引用了无法解析的程序集：{list}。" +
                            "这通常是包/Unity 迁移时改名或移除导致，或资产从更大的工程里摘出来时的残留。" +
                            "Unity 会跳过无法解析的引用、照常编译该程序集（Inspector 里显示为灰条）——所以如果工程编译正常、功能正常，这就是可以放心清理的历史残留。" +
                            "只有两种情况它才是真问题：该程序集的代码用到了缺失程序集里的类型（会编译报错），或这条引用本该指向一个需要安装的包——那就重装该包或重新指向，而不是删除。"),
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

        /// <summary>
        /// Whether Object.GetInstanceID carries [Obsolete] on THIS engine (newer Unity 6 deprecates it in favor of
        /// GetEntityId). Evaluated once per domain load; internal and writable so tests can exercise both rule paths
        /// on an editor where the real value is false (2022.3).
        /// </summary>
        internal static bool GetInstanceIdIsObsolete = MemberIsObsolete(typeof(UnityEngine.Object), "GetInstanceID");

        /// <summary>Whether that [Obsolete] is error-level ([Obsolete(msg, true)] → CS0619, blocks compilation) — decides Critical vs Warning.</summary>
        internal static bool GetInstanceIdObsoleteIsError = MemberObsoleteIsError(typeof(UnityEngine.Object), "GetInstanceID");

        /// <summary>
        /// Whether URP is installed in THIS project but no longer exposes XRGraphics (dropped in the Unity 6 URP
        /// line). Reflected over the loaded assemblies instead of pinned to a version number — the exact URP release
        /// that removed it is hard to pin (present in URP 14, gone in URP 17), and asking the engine is zero-guess.
        /// False when URP is absent (the rule is moot there). Internal and writable so tests can drive both paths.
        /// </summary>
        internal static bool XRGraphicsRemoved = ComputeXRGraphicsRemoved();

        private static bool ComputeXRGraphicsRemoved()
        {
            try
            {
                System.Reflection.Assembly urp = null;
                foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Unity.RenderPipelines.Universal.Runtime") { urp = a; break; }
                if (urp == null) return false; // URP not installed → no XRGraphics call site can exist
                return urp.GetType("UnityEngine.Rendering.Universal.XRGraphics") == null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Find a type by full name across the loaded assemblies. Type.GetType needs an assembly-qualified name,
        /// and guessing which package assembly owns an SRP type is exactly how this went wrong once already
        /// (RenderGraphSettings was assumed to live in Core, and actually lives in URP) — so ask, don't guess.
        /// </summary>
        private static System.Type FindLoadedType(string fullName)
        {
            try
            {
                foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = a.GetType(fullName);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Whether this project actually renders through URP's Render Graph — Unity 6+, URP installed, and
        /// Compatibility Mode (Render Graph disabled) turned OFF. That is the exact condition under which a
        /// ScriptableRenderPass implementing only the compatibility Execute() silently does nothing.
        /// Anything we cannot determine → false, i.e. report nothing: a wrong "your custom rendering is dead"
        /// costs far more than staying silent. Internal and writable so tests can drive both paths.
        /// </summary>
        internal static bool RenderGraphActive = ComputeRenderGraphActive();

        /// <summary>
        /// Whether this project can still fall back to Compatibility Mode at all. URP compiles the real
        /// getter/setter only under the URP_COMPATIBILITY_MODE define; without it the property degrades to
        /// <c>get =&gt; false</c> plus an error-level obsolete setter, and the Graphics settings UI hides the toggle
        /// (Unity 6.3). Detected by asking whether that setter is error-obsolete — no version guessing, and it
        /// keeps the finding's advice pointing at something that actually exists. Unknown → false (advise migration,
        /// which is correct either way). Internal and writable so tests can drive both wordings.
        /// </summary>
        internal static bool CompatibilityModeReachable = ComputeCompatibilityModeReachable();

        private static bool ComputeCompatibilityModeReachable()
        {
            try
            {
                var settingsType = FindLoadedType("UnityEngine.Rendering.Universal.RenderGraphSettings")
                                   ?? FindLoadedType("UnityEngine.Rendering.RenderGraphSettings");
                if (settingsType == null) return false;

                var setter = settingsType.GetProperty("enableRenderCompatibilityMode")?.GetSetMethod();
                if (setter == null) return false; // no setter at all → not reachable

                var obsolete = (System.ObsoleteAttribute)System.Attribute.GetCustomAttribute(
                    setter, typeof(System.ObsoleteAttribute), inherit: true);
                // Error-level obsolete on the setter is exactly the stripped "define is missing" shape.
                return obsolete == null || !obsolete.IsError;
            }
            catch { return false; }
        }

        private static bool ComputeRenderGraphActive()
        {
            try
            {
                // RenderGraph became the default in Unity 6; on older editors the compatibility path IS the path.
                if (!(Application.unityVersion ?? "").StartsWith("6000")) return false;

                // Verified on a live 6000.3.20f1 editor: this type is in the URP package's
                // Unity.RenderPipelines.Universal.Runtime assembly and the UnityEngine.Rendering.Universal
                // namespace — NOT in Core, which is where an assembly-qualified guess first sent it and made this
                // whole check silently return false. The Core name is kept as a fallback in case it ever moves.
                var settingsType = FindLoadedType("UnityEngine.Rendering.Universal.RenderGraphSettings")
                                   ?? FindLoadedType("UnityEngine.Rendering.RenderGraphSettings");
                if (settingsType == null) return false; // no URP RenderGraph settings → rule is moot

                var getter = typeof(UnityEngine.Rendering.GraphicsSettings).GetMethod(
                    "GetRenderPipelineSettings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (getter == null || !getter.IsGenericMethodDefinition) return false;

                // Throws when no SRP asset is active — caught below and read as "cannot determine".
                object settings = getter.MakeGenericMethod(settingsType).Invoke(null, null);
                if (settings == null) return false;

                var prop = settingsType.GetProperty("enableRenderCompatibilityMode");
                if (prop == null) return false;
                return !(bool)prop.GetValue(settings);
            }
            catch { return false; }
        }

        /// <summary>Reflection: does the public parameterless instance method carry [Obsolete] on the current engine?</summary>
        internal static bool MemberIsObsolete(System.Type type, string methodName)
        {
            try
            {
                var m = type?.GetMethod(methodName, System.Type.EmptyTypes);
                return m != null && m.IsDefined(typeof(System.ObsoleteAttribute), inherit: true);
            }
            catch { return false; }
        }

        /// <summary>Reflection: is the member's [Obsolete] error-level (IsError=true) on the current engine?</summary>
        internal static bool MemberObsoleteIsError(System.Type type, string methodName)
        {
            try
            {
                var m = type?.GetMethod(methodName, System.Type.EmptyTypes);
                var attr = m == null ? null : (System.ObsoleteAttribute)System.Attribute.GetCustomAttribute(m, typeof(System.ObsoleteAttribute), inherit: true);
                return attr != null && attr.IsError;
            }
            catch { return false; }
        }

        /// <summary>Whether the current Unity is ≥ 2022.1 (including Unity 6 = 6000.x), i.e. the version line where URP 13 marks RenderTargetHandle obsolete.</summary>
        internal static bool IsAtLeast2022_1(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            var parts = version.Split('.');
            if (!int.TryParse(parts[0], out int major)) return false;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;

            if (major >= 6000) return true;                // Unity 6+ (6000.x)
            if (major > 2022 && major < 6000) return true; // 2023 / 2024…
            if (major == 2022) return minor >= 1;          // 2022.1+
            return false;                                  // 2021 and earlier
        }

        /// <summary>Whether the current Unity is ≥ 2023.2 (including Unity 6 = 6000.x), i.e. the version line where URP's cameraColorTarget/cameraDepthTarget turn error-level.</summary>
        internal static bool IsAtLeast2023_2(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            var parts = version.Split('.');
            if (!int.TryParse(parts[0], out int major)) return false;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;

            if (major >= 6000) return true;                // Unity 6+ (6000.x)
            if (major > 2023 && major < 6000) return true; // 2024 / 2025…
            if (major == 2023) return minor >= 2;          // 2023.2+
            return false;                                  // 2022 and earlier
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
