using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PerfLint.L10n;
using UnityEngine;

namespace PerfLint.Llm
{
    /// <summary>
    /// A rule-bound WHOLE-FILE migration playbook. Where AI Fix replaces one marked snippet, AI Migrate rewrites the
    /// entire file following a curated recipe — for structural migrations (RenderTargetHandle→RTHandle) whose changes
    /// span declarations, overrides, allocation and release, so a fragment replacement can never be complete.
    /// The deterministic rule engine decides WHICH migration applies (the finding's RuleId); the recipe tells the
    /// model exactly HOW, so the model applies a known pattern instead of improvising.
    /// </summary>
    public sealed class MigrateRecipe
    {
        public string RuleId;
        /// <summary>One-line, human-readable "what this migration does" for the UI panel (lazy for language switching).</summary>
        public Func<string> Summary;
        /// <summary>The full migration playbook, injected as the system prompt.</summary>
        public string SystemPrompt;
        /// <summary>Tokens that must no longer appear in CODE (comments/strings ignored) after migration — the "did it actually migrate" check.</summary>
        public string[] MustDisappear;
        /// <summary>MVP file-size cap: larger files risk output truncation (the whole file must come back in one completion).</summary>
        public int MaxLines = 500;
        public int MaxTokens = 16384;
        /// <summary>
        /// Optional deterministic environment probe, run at Propose time and injected into the user message as
        /// authoritative facts. Models can't be trusted to know which API surface THIS project's package versions
        /// expose (first smoke test: the model emitted the URP compatibility-mode overrides, but Unity 6000.5's URP
        /// had removed them — CS0115 ×3, rollback). Reflection over the loaded assemblies knows; the model obeys.
        /// Returns null when there is nothing to report.
        /// </summary>
        public Func<string> ProbeEnvironment;

        /// <summary>What the recipe rewrites — decides the guard set (Parse) and the Apply/verify path (UI).</summary>
        public MigrateKind Kind = MigrateKind.CSharp;

        /// <summary>
        /// Optional per-finding target resolution. Null → the default .cs extraction from the finding's TargetPath.
        /// Shader recipes need this: the file to migrate is the one the COMPILER ERROR points at (often an included
        /// .hlsl, suffix-matched from the truncated ShaderMessage.file), not the finding's .shader asset — and the
        /// per-finding facts (the full error list) ride along. Returns null when no migratable target exists.
        /// </summary>
        public Func<string, MigrateTarget> ResolveTarget;
    }

    public enum MigrateKind
    {
        /// <summary>C# rewrite: verified by the compile-scheduler pipeline (domain reload, rollback via Verifier).</summary>
        CSharp,
        /// <summary>Shader/HLSL rewrite: verified synchronously (re-import + active per-pass compilation), no domain reload.</summary>
        Shader
    }

    /// <summary>A resolved migration target: the file to rewrite plus everything Apply-time verification needs.</summary>
    public sealed class MigrateTarget
    {
        /// <summary>The file the model rewrites (.cs, .shader or an included .hlsl/.cginc).</summary>
        public string FilePath;
        /// <summary>Per-finding authoritative facts appended to the probe facts in the user message (e.g. the shader's full compiler-error list). Optional.</summary>
        public string Facts;
        /// <summary>The asset whose compilation proves success (the erroring .shader). Null for C# targets (the whole-project compile is the verifier there).</summary>
        public string VerifyAssetPath;
        /// <summary>Optional user-facing note shown in the panel (e.g. "a specialized recipe covers this file — prefer it"). Not sent to the model.</summary>
        public string UserNotice;
    }

    public sealed class MigrateProposal
    {
        public bool Ok;
        public string Error;
        public string FilePath;
        public string Original;   // whole original file (normalized to \n)
        public string Migrated;   // whole migrated file (normalized to \n)
        public bool NoChange;     // model returned an equivalent file → nothing to apply
        public MigrateRecipe Recipe; // the recipe that produced this proposal (needed by the auto-retry loop)
        public int Attempt;       // 0 = the user-approved apply; 1..MaxRetries = automatic compile-error-driven retries
        public string VerifyAssetPath; // shader targets: the erroring .shader whose compilation proves success (null for C#)
    }

    /// <summary>Registry of whole-file migration recipes, keyed by the rule that detects the need for them.</summary>
    public static class MigrateRecipes
    {
        public static MigrateRecipe ForRule(string ruleId)
        {
            if (ruleId == RtHandleRecipe.RuleId) return RtHandleRecipe;
            if (ruleId == GetInstanceIdRecipe.RuleId) return GetInstanceIdRecipe;
            if (ruleId == ShaderRecipes.UrpShaderRecipe.RuleId) return ShaderRecipes.UrpShaderRecipe;
            if (ruleId == GenericCompileErrorRecipe.RuleId) return GenericCompileErrorRecipe;
            return null;
        }

        /// <summary>
        /// Resolve what a recipe would migrate for this finding: the recipe's own resolver when present (shader
        /// recipes locate the error's file), otherwise the default .cs extraction. Null → no migratable target,
        /// the UI shows no AI Migrate button.
        /// </summary>
        public static MigrateTarget Resolve(MigrateRecipe recipe, string findingTargetPath)
        {
            if (recipe == null) return null;
            if (recipe.ResolveTarget != null)
            {
                try { return recipe.ResolveTarget(findingTargetPath); }
                catch { return null; }
            }
            string file = TargetFileOf(findingTargetPath);
            return file == null ? null : new MigrateTarget { FilePath = file };
        }

        /// <summary>
        /// Extract the .cs file behind a finding's TargetPath ("Assets/X.cs:19" or "Assets/X.cs"); null when the
        /// target isn't a C# file. Structural findings deliberately carry no CodeFile (that would light up the
        /// fragment-level AI Fix), so the file is recovered from the location string instead.
        /// </summary>
        public static string TargetFileOf(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath)) return null;
            var m = Regex.Match(targetPath, @"^(.+\.cs)(?::\d+)?$");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ── MIG.RenderTargetHandle → RTHandle (the #1 URP blocker when upgrading to Unity 6) ─────────────
        private static readonly MigrateRecipe RtHandleRecipe = new MigrateRecipe
        {
            RuleId = "MIG.RenderTargetHandle",
            Summary = () => L.Tr(
                "Rewrites this file from the removed RenderTargetHandle API to the RTHandle system (allocation, explicit release, and the pass overrides your URP version actually exposes).",
                "把此文件从已移除的 RenderTargetHandle API 整体迁移到 RTHandle 体系（分配、显式释放、以及你的 URP 版本实际提供的 pass override 形态）。"),
            MustDisappear = new[] { "RenderTargetHandle" },
            ProbeEnvironment = ProbeUrpRenderPassApi,
            SystemPrompt =
                "你是资深 Unity 图形工程师。用户给你一个完整的 C# 源文件：一个使用了已废弃 RenderTargetHandle 的 URP 自定义 " +
                "ScriptableRenderPass / ScriptableRendererFeature。请把它整体迁移到 RTHandle 体系。\n" +
                "用户消息中会给出【当前工程 URP 实测 API】——那是对本工程已加载程序集做反射得到的权威事实，" +
                "决定你必须使用哪种 pass 形态；它优先于你对任何 Unity/URP 版本的记忆。\n" +
                "【通用迁移——两种形态都要做】\n" +
                "1) 字段/变量声明：RenderTargetHandle x → RTHandle x。\n" +
                "2) x.Identifier() → 直接写 x（RTHandle 可隐式转换为 RenderTargetIdentifier）；" +
                "cmd.SetGlobalTexture(x.id, …) → 用 Shader.PropertyToID(\"_Name\") 或纹理句柄。\n" +
                "3) cmd.GetTemporaryRT / cmd.ReleaseTemporaryRT(x.id) → 删除，按所选形态的资源管理替代（见下）。\n" +
                "4) RenderTargetHandle.CameraTarget → 相机目标按所选形态获取（见下）。\n" +
                "5) 缺少的 using（UnityEngine.Rendering / UnityEngine.Rendering.Universal / UnityEngine.Rendering.RenderGraphModule）请补上。\n" +
                "【形态 A：compatibility 形态——仅当实测 API 表明 OnCameraSetup/Execute(ScriptableRenderContext, ref RenderingData) 可覆写时】\n" +
                "- 分配：在 OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) 中 " +
                "var desc = renderingData.cameraData.cameraTargetDescriptor;（临时颜色目标通常 desc.depthBufferBits = 0;）" +
                "RenderingUtils.ReAllocateIfNeeded(ref x, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: \"_Name\");" +
                "（该方法在较新 URP 改名 ReAllocateHandleIfNeeded，按实测 API/版本选用。）\n" +
                "- ConfigureTarget(x)；旧 Configure(CommandBuffer, RenderTextureDescriptor) 的逻辑并入 OnCameraSetup。\n" +
                "- 释放：Feature 覆写 protected override void Dispose(bool disposing) 调 x?.Release()。\n" +
                "- 相机目标：renderer.cameraColorTargetHandle / cameraDepthTargetHandle（在 SetupRenderPasses 或 OnCameraSetup 阶段取）。\n" +
                "【形态 B：RenderGraph 形态——当实测 API 表明 OnCameraSetup/Execute 已不可覆写、存在 RecordRenderGraph 时【必须】用此形态】\n" +
                "- pass 的全部逻辑改写进 public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)。\n" +
                "- 【PassData 必须声明为 class，绝不能是 struct】——AddUnsafePass<PassData>/SetRenderFunc 的泛型约束是 " +
                "where PassData : class，struct 直接产生 CS0452 编译错误。\n" +
                "- 直迁旧 Execute 里的命令逻辑用 Unsafe Pass 最稳：\n" +
                "  using (var builder = renderGraph.AddUnsafePass<PassData>(\"名字\", out var passData)) {\n" +
                "      passData.xxx = …;（把 Execute 需要的数据放进自定义 PassData class）\n" +
                "      builder.AllowPassCulling(false);\n" +
                "      builder.UseTexture(tex, AccessFlags.Write);（声明每个读/写的 TextureHandle）\n" +
                "      builder.SetRenderFunc((PassData d, UnsafeGraphContext ctx) => { 原 Execute 逻辑；需要 CommandBuffer 时用 " +
                "CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd); });\n" +
                "  }\n" +
                "- 临时 RT：TextureHandle t = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, \"_Name\", clear: true);" +
                "该方法【没有 clearColor 参数】（第 5 个可选参数是 FilterMode）——需要特定清屏色时在 pass 内用 " +
                "cmd.ClearRenderTarget，绝不要把 Color 塞进参数列表。" +
                "（不再需要 RTHandle 字段与 Dispose——RenderGraph 管生命周期；跨帧持久纹理才保留 RTHandle + renderGraph.ImportTexture。）\n" +
                "- 相机目标：var resourceData = frameData.Get<UniversalResourceData>(); resourceData.activeColorTexture / activeDepthTexture。\n" +
                "- 全局纹理：builder.SetGlobalTextureAfterPass(t, Shader.PropertyToID(\"_Name\"));\n" +
                "- 【原 Execute 里的 context.DrawRenderers 必须改 RendererList API——UnsafeGraphContext 里没有 ScriptableRenderContext】：\n" +
                "  在 RecordRenderGraph 中：var urd = frameData.Get<UniversalRenderingData>(); var cam = frameData.Get<UniversalCameraData>(); " +
                "var lights = frameData.Get<UniversalLightData>();\n" +
                "  var drawSettings = RenderingUtils.CreateDrawingSettings(shaderTagId, urd, cam, lights, cam.defaultOpaqueSortFlags);\n" +
                "  var listParams = new RendererListParams(urd.cullResults, drawSettings, filteringSettings);\n" +
                "  RendererListHandle list = renderGraph.CreateRendererList(listParams); builder.UseRendererList(list);（把 list 存进 PassData）\n" +
                "  SetRenderFunc 内：ctx.cmd.DrawRendererList(d.list);\n" +
                "- 【UnsafeGraphContext 可用的成员只有 cmd】——它【没有】renderContext / ScriptableRenderContext；" +
                "原 Execute 里的 context.ExecuteCommandBuffer(...) / CommandBufferPool.Get/Release / context.Submit 全部删除" +
                "（RenderGraph 自行调度与提交，你只需要往 ctx.cmd 里录命令）。\n" +
                "- RenderGraph 类型的 using：UnityEngine.Rendering.RenderGraphModule。\n" +
                "- 【调用工具方法的通用纪律】只用你确定存在的最少参数重载；要传可选参数时必须用具名参数（如 clear: true、" +
                "filterMode: FilterMode.Bilinear），绝不按位置猜——参数位错配（如把 Color 传进 FilterMode 位）是编译错误的高发源。\n" +
                "- 【反幻觉纪律】绝不调用本对照表之外、你不能确定存在的成员（此前失败案例：编造 RenderGraph.GetBuiltinRenderTextureSystem、" +
                "UnsafeGraphContext.renderContext——都不存在）。某段旧逻辑在新 API 下确实找不到对应物时，" +
                "宁可省略该次要功能并留一行 // TODO(PerfLint AI Migrate): <说明> 注释，也不要编造 API 或访问 protected/internal 成员。\n" +
                "- 【builder 作用域】builder 变量只在它自己的 using (var builder = renderGraph.AddUnsafePass…) 块内有效：" +
                "所有 builder.* 调用（UseTexture/UseRendererList/SetRenderFunc/AllowPassCulling）必须写在该块内；" +
                "多个 pass 就各开各的 using 块、各用各的 builder；SetRenderFunc 的 lambda 体内绝不引用 builder（此前失败案例：块外引用 builder → CS0103）。\n" +
                "【硬性要求】\n" +
                "- 输出必须是完整、可直接编译的整个源文件：从第一行到最后一行逐行给出，不得省略、不得用「// ... 其余不变」占位、不得截断。\n" +
                "- 迁移后文件中不得再出现 RenderTargetHandle。\n" +
                "- 绝不 override 实测 API 里标记为「已不存在」的方法——那会直接产生 CS0115 编译错误。\n" +
                "- 只做与本迁移相关的改动：类名、命名空间、公共 API、无关方法、注释与空行布局保持原样。\n" +
                "- 不虚构不存在的 API；不确定的边缘写法选择最保守的等价实现。\n" +
                "严格按如下格式回复，不要有任何其他文字或解释：\n" +
                "<<<FILE>>>\n（完整迁移后的源文件）\n<<<END>>>"
        };

        // ── MIG.GetInstanceID → unique-key counter / GetEntityId with the type ripple (newer Unity 6) ─────
        // History: this rule went through both other repair channels and each failed for a knowable reason —
        // the plain LLM snippet fix invented APIs newer than its training data, and the deterministic rename
        // was evicted because on 6000.5 even the EntityId→int implicit operator is error-level obsolete (the
        // receivers must migrate WITH the call). The whole-file recipe is the correct shape: the split below
        // (unique key vs real identity) is exactly the generic playbook's rule 2, promoted to a dedicated
        // recipe with an engine probe so the model never guesses the EntityId API surface.
        private static readonly MigrateRecipe GetInstanceIdRecipe = new MigrateRecipe
        {
            RuleId = "MIG.GetInstanceID",
            Summary = () => L.Tr(
                "Rewrites this file off the deprecated GetInstanceID(): call sites that only need a unique key switch to a local counter (no type changes anywhere); call sites that need real object identity move to GetEntityId() with the EntityId type propagated through this file's receivers.",
                "把此文件整体迁离已废弃的 GetInstanceID()：仅需唯一键的调用点改用本地计数器（不改任何类型）；确需对象身份的调用点改 GetEntityId() 并在本文件内连带传播 EntityId 类型。"),
            MustDisappear = new[] { "GetInstanceID" },
            ProbeEnvironment = ProbeGetInstanceIdApi,
            SystemPrompt =
                "你是资深 Unity 工程师。用户给你一个完整的 C# 源文件：其中调用了在该 Unity 版本已废弃的 GetInstanceID()。" +
                "请整体迁移此文件，迁移后代码中不得再出现 GetInstanceID。\n" +
                "用户消息中会给出【当前引擎实测 API】——反射自本机引擎的权威事实，优先于你对任何 Unity 版本的记忆。\n" +
                "【逐调用点二选一（关键判据：返回值的用途）】\n" +
                "a. 返回值仅用作「唯一键/标识」（字典 key、job 系统句柄、日志标签等，不要求与引擎对象身份一致）——" +
                "尤其当该值会传给你看不到的其他文件的 int 参数/字段/字典时【必须】用本方案：\n" +
                "   - 类内加 private static int s_NextPerfLintId; 在原赋值处改为 ++s_NextPerfLintId。\n" +
                "   - 【每个实例只取一次号并存进字段复用】——绝不能在每次使用时重新取号，否则同一对象前后拿到不同 id。\n" +
                "   - 本方案不改变任何字段/参数/返回值类型，是跨文件最安全的最小修改。\n" +
                "b. 确需真实对象身份（要与别处取到的同一对象 id 比对），且实测 API 表明 GetEntityId() 存在：改调 GetEntityId()，" +
                "并把接收该值的**本文件内**变量/字段/字典 key 的类型 int→EntityId（EntityId 可比较、可作字典 key）。" +
                "【绝不能把 EntityId 值赋给 int】——实测 API 会告知隐式转换的废弃级别；" +
                "若该值必须传出到其他文件的 int 签名，回到方案 a。\n" +
                "【反幻觉纪律】绝不调用你不能确定存在于该 Unity 版本的 API；不引入新包依赖；不改与本迁移无关的代码。\n" +
                "【硬性要求】\n" +
                "- 输出必须是完整、可直接编译的整个源文件：从第一行到最后一行逐行给出，不得省略、不得用「// ... 其余不变」占位、不得截断。\n" +
                "- 原文件声明的每个类型（class/struct/interface/enum）必须原名保留；公共 API（public 方法签名/属性）尽量不动，" +
                "确须改动时留 // TODO(PerfLint AI Migrate): 注明调用方需跟进。\n" +
                "- 只做与本迁移相关的改动：类名、命名空间、无关方法、注释与空行布局保持原样。\n" +
                "严格按如下格式回复，不要有任何其他文字或解释：\n" +
                "<<<FILE>>>\n（完整迁移后的源文件）\n<<<END>>>"
        };

        /// <summary>
        /// Reflection probe for the GetInstanceID migration: does GetEntityId() exist on THIS engine, and how
        /// deprecated is the EntityId→int implicit conversion (the fact that killed the deterministic rename).
        /// Always returns a facts block — "GetEntityId absent" is itself the decisive fact (forces plan a).
        /// </summary>
        private static string ProbeGetInstanceIdApi()
        {
            bool hasGetEntityId = false;
            int implicitState = 0; // 0 = EntityId type missing, 1 = usable, 2 = warning-obsolete, 3 = error-obsolete
            try
            {
                foreach (var m in typeof(UnityEngine.Object).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    if (m.Name == "GetEntityId" && m.GetParameters().Length == 0) { hasGetEntityId = true; break; }

                var et = Type.GetType("UnityEngine.EntityId, UnityEngine.CoreModule");
                if (et != null)
                {
                    implicitState = 1;
                    foreach (var m in et.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (m.Name != "op_Implicit" || m.ReturnType != typeof(int)) continue;
                        var attr = (ObsoleteAttribute)Attribute.GetCustomAttribute(m, typeof(ObsoleteAttribute));
                        if (attr != null) implicitState = attr.IsError ? 3 : 2;
                        break;
                    }
                }
            }
            catch { /* partial facts are still facts */ }
            return FormatGetInstanceIdFacts(hasGetEntityId, implicitState);
        }

        /// <summary>Pure formatting of the GetInstanceID probe result (unit-testable on engines without EntityId).</summary>
        internal static string FormatGetInstanceIdFacts(bool hasGetEntityId, int implicitToIntState)
        {
            var sb = new StringBuilder();
            sb.Append("【当前引擎实测 API（反射自本机引擎，权威，优先于你的记忆）】\n");
            sb.Append("Object.GetEntityId() 存在：").Append(hasGetEntityId ? "是" : "否——方案 b 不可用，所有调用点按方案 a 处理").Append('\n');
            switch (implicitToIntState)
            {
                case 3: sb.Append("EntityId→int 隐式转换：error 级废弃（编译错误——EntityId 值绝不能进 int）"); break;
                case 2: sb.Append("EntityId→int 隐式转换：warning 级废弃（能编译，但绝不要依赖）"); break;
                case 1: sb.Append("EntityId→int 隐式转换：可用（仍应保持类型一致，不要依赖它）"); break;
                default: sb.Append("EntityId 类型：不存在——所有调用点按方案 a 处理"); break;
            }
            return sb.ToString();
        }

        // ── MIG.CompileError → the GENERIC error-driven recipe (tier 3 of the repair ladder) ─────────────
        // Where per-API recipes carry deep domain playbooks, this one gets only the universal discipline plus a
        // condensed table of high-frequency upgrade breakages (the curated rules' knowledge, distilled). What it
        // trades in per-case knowledge it makes back through the SAME compile-verify + error-feedback loop —
        // errors the first pass misses come back with exact compiler messages for round two.
        private static readonly MigrateRecipe GenericCompileErrorRecipe = new MigrateRecipe
        {
            RuleId = "MIG.CompileError",
            Kind = MigrateKind.CSharp,
            ResolveTarget = ResolveCompileErrorTarget,
            MaxLines = 500,
            Summary = () => L.Tr(
                "Rewrites this file to eliminate its current compiler errors (guided by the exact error list), keeping behavior as close as possible. Compile-verified with automatic rollback and error-driven retries.",
                "按当前编译错误清单整体重写此文件以消除错误，尽可能保持行为不变。编译校验、失败自动回滚、错误喂回自动重试。"),
            SystemPrompt =
                "你是资深 Unity 工程师。用户给你一个编译失败的完整 C# 源文件，以及【当前全部编译错误】（权威，修复以消除这些错误为准）。" +
                "你的唯一目标：**做最小的修改让列出的编译错误全部消失，且运行行为尽可能不变**。这些错误通常来自 Unity 版本升级（API 被移除/改名）。\n" +
                "【高频升级破坏点对照表（按错误消息匹配后套用）】\n" +
                "1) FindObjectOfType / FindObjectsOfType 废弃 → FindAnyObjectByType<T>()（或需确定性时 FindFirstObjectByType<T>()）/ " +
                "FindObjectsByType<T>(FindObjectsSortMode.None)。\n" +
                "2) GetInstanceID 错误级废弃（CS0619）→ 先判断用途：【a. 返回值仅用作唯一键】（字典键/job 系统句柄/日志标识，不需要真实对象身份，" +
                "且该键会传给你看不到的其他文件）→ 换 static 自增计数器（private static int _nextId; … = ++_nextId;）——单文件最小修，" +
                "不碰其他文件的 int 签名；【b. 确需对象身份】→ GetEntityId()，且接收其返回值的本文件变量/字段/字典键必须把 int 改为 EntityId" +
                "（EntityId→int 无合法转换路径，隐式转换同样错误级废弃）；若 EntityId 需要传出到其他文件的 int 参数——回到 a 方案。\n" +
                "3) 'WWW' 相关错误（若该版本已将其升级为错误级或移除）→ UnityWebRequest（using UnityEngine.Networking）：GET 用 UnityWebRequest.Get(url)，" +
                "在协程中 yield return req.SendWebRequest()，结果判 req.result == UnityWebRequest.Result.Success，文本取 req.downloadHandler.text，" +
                "字节取 .data。尽量保持原方法签名与调用形态；确实无法保持时选最小改动并留 // TODO(PerfLint AI Migrate): 注明调用方需跟进。\n" +
                "4) Application.LoadLevel / LoadLevelAsync → SceneManager.LoadScene / LoadSceneAsync（using UnityEngine.SceneManagement）。\n" +
                "5) 'RenderTargetHandle' 找不到 → 属结构性 URP 迁移：声明改 RTHandle、Identifier() 改直接传句柄、" +
                "GetTemporaryRT/ReleaseTemporaryRT 改 RenderingUtils.ReAllocateIfNeeded + 显式 Release；仅做让本文件编译通过的最小迁移，" +
                "复杂 pass 逻辑保守处理并留 TODO。\n" +
                "6) TextMeshPro 相关类型找不到（Unity 6 把 TMP 合并进 UGUI）→ 保持 using TMPro 不变，通常是 asmdef 引用问题而非代码问题——" +
                "若代码层无从修复，保持原样并留 TODO 说明该改 asmdef 引用。\n" +
                "7) 其他 CS0246/CS0117（类型或成员不存在）→ 按错误消息与你对该 Unity 版本的知识选择最保守的等价 API；拿不准时宁可用" +
                "行为最接近的替代并留一行 // TODO(PerfLint AI Migrate): <说明>，绝不编造不确定存在的 API。\n" +
                "【反幻觉纪律】绝不调用你不能确定存在于该 Unity 版本的 API；不引入新包依赖；不改与错误无关的代码。\n" +
                "【硬性要求】\n" +
                "- 输出必须是完整、可直接编译的整个源文件：从第一行到最后一行逐行给出，不得省略、不得用「// ... 其余不变」占位、不得截断。\n" +
                "- 原文件声明的每个类型（class/struct/interface/enum）必须原名保留——改名或删除会破坏其他文件对它的引用。\n" +
                "- 公共 API（public 方法签名/属性）尽量不动；确须改动时留 TODO 注明调用方需跟进。\n" +
                "- 缺少的 using 请补上；只做与消除所列编译错误相关的最小改动，注释与空行布局保持原样。\n" +
                "严格按如下格式回复，不要有任何其他文字或解释：\n" +
                "<<<FILE>>>\n（完整修复后的源文件）\n<<<END>>>"
        };

        /// <summary>
        /// Target resolution for MIG.CompileError findings: the finding's TargetPath is "Assets/X.cs:line" — the
        /// file itself is the migration unit, and the per-finding facts are that file's captured compiler errors.
        /// </summary>
        private static MigrateTarget ResolveCompileErrorTarget(string findingTargetPath)
        {
            string file = TargetFileOf(findingTargetPath);
            if (file == null) return null;

            var mine = new List<Scanners.CollectedError>();
            foreach (var e in Scanners.CompileErrorCollector.Snapshot())
                if (e != null && string.Equals(e.file, file, StringComparison.OrdinalIgnoreCase))
                    mine.Add(e);

            return new MigrateTarget
            {
                FilePath = file,
                Facts = BuildCompileErrorFacts(file, mine),
                UserNotice = SpecializedRecipeNotice(mine)
            };
        }

        /// <summary>
        /// When this file's errors match a rule that carries a DEEP per-API recipe, steer the user there: the
        /// specialized playbook (probes, dual pass shapes, domain hard rules) beats the generic recipe's minimal
        /// treatment. Pure logic over the error list (unit-testable). Extend the table when adding per-API recipes.
        /// </summary>
        internal static string SpecializedRecipeNotice(IReadOnlyList<Scanners.CollectedError> errors)
        {
            if (errors == null) return null;
            foreach (var e in errors)
            {
                if (e?.message != null && Regex.IsMatch(e.message, @"\bRenderTargetHandle\b"))
                    return L.Tr(
                        "This file's errors match a specialized recipe: the MIG.RenderTargetHandle finding on this file offers a deeper, URP-aware migration (pass-shape probing, RTHandle lifecycle) — prefer it over this generic rewrite.",
                        "此文件的错误命中专用配方：本文件的 MIG.RenderTargetHandle 条目提供更深度的 URP 定制迁移（pass 形态探测、RTHandle 生命周期）——建议优先用它，而不是这里的通用重写。");
            }
            return null;
        }

        /// <summary>Pure formatting of the per-file error facts block (unit-testable).</summary>
        internal static string BuildCompileErrorFacts(string file, IReadOnlyList<Scanners.CollectedError> errors)
        {
            var sb = new StringBuilder();
            sb.Append("【当前全部编译错误（权威，修复以消除这些错误为准）】\n");
            if (errors == null || errors.Count == 0)
                sb.Append("- （错误详情暂不可用——请按源码与你对该 Unity 版本的知识修复可见的废弃/缺失 API 用法）\n");
            else
                foreach (var e in errors)
                    sb.Append("- ").Append(file).Append(':').Append(e.line).Append("  ").Append(e.message).Append('\n');
            return sb.ToString();
        }

        // Virtuals whose presence decides the pass shape. Probed by NAME on the loaded URP assembly — the compiler
        // messages from the first smoke test proved signature-level guessing wrong is fatal (CS0115 → rollback).
        private static readonly string[] CompatVirtuals = { "OnCameraSetup", "Configure", "Execute", "OnCameraCleanup" };

        /// <summary>
        /// Reflection probe over THIS project's loaded URP: which ScriptableRenderPass virtuals exist. Returns the
        /// authoritative facts block for the user message, or null when URP isn't loaded (facts then add nothing).
        /// </summary>
        private static string ProbeUrpRenderPassApi()
        {
            Type t = null;
            try
            {
                t = Type.GetType("UnityEngine.Rendering.Universal.ScriptableRenderPass, Unity.RenderPipelines.Universal.Runtime");
            }
            catch { /* fall through */ }
            if (t == null) return null;

            var present = new List<string>();
            var absent = new List<string>();
            foreach (var name in CompatVirtuals)
                (HasVirtual(t, name) ? present : absent).Add(name);
            bool renderGraph = HasVirtual(t, "RecordRenderGraph");

            return FormatUrpApiFacts(present, absent, renderGraph);
        }

        private static bool HasVirtual(Type t, string methodName)
        {
            try
            {
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    if (m.Name == methodName && (m.IsVirtual || m.IsAbstract)) return true;
            }
            catch { /* treat as absent */ }
            return false;
        }

        /// <summary>Pure formatting of the probe result (unit-testable without URP installed).</summary>
        internal static string FormatUrpApiFacts(IReadOnlyList<string> present, IReadOnlyList<string> absent, bool hasRenderGraph)
        {
            var sb = new StringBuilder();
            sb.Append("【当前工程 URP 实测 API（反射自已加载程序集，权威，优先于版本经验）】\n");
            sb.Append("ScriptableRenderPass 可覆写的虚方法：").Append(present.Count > 0 ? string.Join("、", present) : "（无 compatibility 虚方法）").Append('\n');
            if (absent.Count > 0)
                sb.Append("已不存在、绝不能 override 的方法：").Append(string.Join("、", absent)).Append('\n');
            sb.Append(hasRenderGraph
                ? "RecordRenderGraph 存在：是"
                : "RecordRenderGraph 存在：否");
            if (hasRenderGraph && absent.Count > 0)
                sb.Append("\n结论：本工程必须使用【形态 B：RenderGraph】。");
            else if (!hasRenderGraph)
                sb.Append("\n结论：本工程使用【形态 A：compatibility】。");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Whole-file AI migration. Unlike ScriptFixService (which sends only a code window), this sends the ENTIRE file
    /// to the model — the UI must disclose that explicitly before every send. Apply reuses the exact same safety net
    /// as AI Fix: backup → pending-verification registration → write → debounced compile check with auto-rollback,
    /// so a failed migration can never leave the project worse than before the click.
    /// </summary>
    public static class MigrateService
    {
        public static int FileLineCount(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? File.ReadAllLines(full).Length : 0;
            }
            catch { return 0; }
        }

        public static void Propose(MigrateRecipe recipe, string filePath, Action<MigrateProposal> onDone)
            => Propose(recipe, new MigrateTarget { FilePath = filePath }, onDone);

        /// <summary>
        /// Whether the editor domain is stale w.r.t. the disk (package change / plugin update blocked from
        /// reloading by compile errors). Swappable for tests; default = the real guard. Gating here — before
        /// any LLM call — because in that state the environment probes reflect the OLD loaded assemblies while
        /// compile verification judges against the NEW packages: every generated round is doomed and every
        /// credit wasted (real case: URP upgraded 10.8→17.5 mid-session, probe kept teaching the removed
        /// compatibility pass shape, three rounds burned on CS0115).
        /// </summary>
        internal static Func<bool> DomainStaleProbe = Core.PerfLintStaleBuildGuard.IsDomainStale;

        public static void Propose(MigrateRecipe recipe, MigrateTarget target, Action<MigrateProposal> onDone)
        {
            string filePath = target.FilePath;

            bool domainStale;
            try { domainStale = DomainStaleProbe != null && DomainStaleProbe(); }
            catch { domainStale = false; }
            if (domainStale)
            {
                onDone(new MigrateProposal
                {
                    Ok = false,
                    FilePath = filePath,
                    Error = L.Tr(
                        "The editor is running pre-change code: PerfLint or this project's packages changed on disk, but compile errors " +
                        "prevent Unity from reloading. Generating now would probe the OLD loaded packages while compilation checks the NEW " +
                        "ones — the rewrite would fail and waste AI credits. Restart the editor, then try again.",
                        "编辑器正在运行变更前的代码：PerfLint 或本工程的包已在磁盘上变更，但编译错误阻止了 Unity 重载。" +
                        "此时生成会按【内存中的旧包】探测 API、却按【磁盘上的新包】校验编译——必然失败并浪费 AI 次数。" +
                        "请先重启编辑器，再重新生成。")
                });
                return;
            }
            string raw = SafeReadAllText(filePath);
            if (raw == null)
            {
                onDone(new MigrateProposal { Ok = false, Error = L.Tr("Failed to read the source file.", "读取源文件失败。"), FilePath = filePath });
                return;
            }
            string original = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            int lineCount = CountLines(original);
            if (lineCount > recipe.MaxLines)
            {
                onDone(new MigrateProposal
                {
                    Ok = false,
                    FilePath = filePath,
                    Error = L.Tr($"This file has {lineCount} lines — above the current whole-file migration cap ({recipe.MaxLines}). Migrate it manually (see Explain for the playbook).",
                                 $"该文件有 {lineCount} 行，超过当前整文件迁移上限（{recipe.MaxLines} 行）。请手动迁移（迁移路径见 Explain）。")
                });
                return;
            }

            // Deterministic environment facts (e.g. which ScriptableRenderPass virtuals THIS project's URP exposes):
            // reflection knows, the model guesses — inject the facts so it doesn't have to.
            string facts = null;
            try { facts = recipe.ProbeEnvironment?.Invoke(); }
            catch { /* probe failure just means fewer facts */ }
            // Per-finding facts from target resolution (e.g. the shader's full compiler-error list) ride along.
            if (!string.IsNullOrEmpty(target.Facts))
                facts = string.IsNullOrEmpty(facts) ? target.Facts : facts + "\n" + target.Facts;

            string user =
                $"当前 Unity 版本：{Application.unityVersion}\n" +
                $"文件路径：{filePath}\n" +
                (string.IsNullOrEmpty(facts) ? "" : "\n" + facts + "\n") +
                "\n源文件全文：\n" + original;

            LlmClient.Send(LlmSettings.FixModel, recipe.SystemPrompt, new[] { new LlmMessage("user", user) }, recipe.MaxTokens, r =>
            {
                if (!r.Success)
                {
                    onDone(new MigrateProposal { Ok = false, Error = r.Error, FilePath = filePath });
                    return;
                }
                var p = Parse(r.Text, original, recipe, filePath);
                p.FilePath = filePath;
                p.VerifyAssetPath = target.VerifyAssetPath;
                onDone(p);
            }, disableThinking: true);
        }

        /// <summary>
        /// Parse the model's &lt;&lt;&lt;FILE&gt;&gt;&gt; block and run every acceptance guard. Pure logic (no I/O, no LLM) so the
        /// guards — the part that decides whether a whole-file rewrite is safe to even offer — are fully unit-testable.
        /// <paramref name="filePath"/> only informs guard selection for shader recipes (.shader vs included .hlsl).
        /// </summary>
        internal static MigrateProposal Parse(string text, string originalLf, MigrateRecipe recipe, string filePath = null)
        {
            var p = new MigrateProposal { Original = originalLf, Recipe = recipe };
            const string mf = "<<<FILE>>>", me = "<<<END>>>";
            int i0 = text == null ? -1 : text.IndexOf(mf, StringComparison.Ordinal);
            if (i0 < 0)
            {
                p.Ok = false;
                p.Error = L.Tr("The model did not reply in the expected format. Raw reply:\n", "模型未按预期格式返回。原始回复：\n") + text;
                return p;
            }
            int ie = text.IndexOf(me, i0 + mf.Length, StringComparison.Ordinal);
            if (ie < 0) ie = text.Length; // END occasionally omitted — same tolerance as ScriptFixService

            p.Migrated = CleanFileBlock(text.Substring(i0 + mf.Length, ie - (i0 + mf.Length)));

            string issue = recipe != null && recipe.Kind == MigrateKind.Shader
                ? GuardShaderIssue(originalLf, p.Migrated, recipe,
                    isDotShader: filePath != null && filePath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                : GuardIssue(originalLf, p.Migrated, recipe);
            if (issue != null)
            {
                p.Ok = false;
                p.Error = L.Tr("The migrated file was rejected (", "迁移结果未通过校验（") + issue +
                          L.Tr("). Nothing was written. Please regenerate, or migrate manually.", "），未写入任何内容。请重新生成，或手动迁移。");
                return p;
            }

            p.NoChange = NoWhitespace(p.Migrated) == NoWhitespace(originalLf);
            p.Ok = true;
            return p;
        }

        /// <summary>
        /// Acceptance guards for a whole-file rewrite; returns the reason text or null when clean. All checks are
        /// deterministic and pre-write — a rejected proposal never reaches the Apply button (the same philosophy as
        /// ScriptFixService's snippet guards: don't rely on compile-rollback for failures we can predict).
        /// </summary>
        internal static string GuardIssue(string original, string migrated, MigrateRecipe recipe)
        {
            if (string.IsNullOrWhiteSpace(migrated))
                return L.Tr("empty output", "输出为空");

            // Truncation, cheapest signal first: a complete C# file ends with '}'.
            if (!migrated.TrimEnd().EndsWith("}", StringComparison.Ordinal))
                return L.Tr("output does not end with '}' — likely truncated", "输出末尾不是 '}'，疑似被截断");

            // Truncation / silent elision: the migrated file lost a large share of the original's lines.
            // RTHandle-style migrations DELETE a few lines (Init/GetTemporaryRT) but never 40% of the file.
            int oLines = CountLines(original), mLines = CountLines(migrated);
            if (mLines < (int)(oLines * 0.6))
                return L.Tr($"line count collapsed ({oLines} → {mLines}) — content was likely dropped", $"行数骤减（{oLines} → {mLines}），疑似内容被丢弃");

            string masked = ScriptFixService.MaskNonCode(migrated);
            if (!ScriptFixService.BracketsBalanced(masked))
                return L.Tr("brackets are not balanced", "括号不配平");

            // "// ... rest unchanged" style elision placeholders — the model summarizing instead of emitting the file.
            if (Regex.IsMatch(migrated, @"(?m)^\s*//\s*(\.\.\.|…)") || migrated.Contains("其余不变") || migrated.Contains("rest unchanged"))
                return L.Tr("contains an elision placeholder ('// ...') instead of the full file", "包含省略占位（// ...），不是完整文件");

            // The migration's whole point: the deprecated token must be gone from CODE (comments/strings may mention it).
            if (recipe?.MustDisappear != null)
            {
                foreach (var token in recipe.MustDisappear)
                    if (Regex.IsMatch(masked, @"\b" + Regex.Escape(token) + @"\b"))
                        return L.Tr($"migration incomplete: '{token}' still appears in code", $"迁移不完整：代码中仍出现 {token}");
            }

            // Every type declared in the original must survive (a renamed/dropped class breaks every reference to it).
            var migratedTypes = TypeNames(masked);
            foreach (var t in TypeNames(ScriptFixService.MaskNonCode(original)))
                if (!migratedTypes.Contains(t))
                    return L.Tr($"type '{t}' disappeared from the migrated file", $"类型 {t} 在迁移结果中消失");

            return null;
        }

        /// <summary>
        /// Shader-flavored acceptance guards (ShaderLab/HLSL rewrites). Deliberately LIGHTER than the C# set:
        /// shader verification is synchronous and cheap (re-import + active compile, seconds, no domain reload),
        /// so pre-write guards only need to catch the structurally-broken outputs. No trailing-'}' check — an
        /// included .hlsl legitimately ends with #endif (include guard). Two shader-specific hard guards for
        /// .shader targets: the Shader "Name" must survive (materials and Shader.Find bind by it) and no
        /// Properties entry may disappear (serialized material values bind by property name).
        /// </summary>
        internal static string GuardShaderIssue(string original, string migrated, MigrateRecipe recipe, bool isDotShader)
        {
            if (string.IsNullOrWhiteSpace(migrated))
                return L.Tr("empty output", "输出为空");

            int oLines = CountLines(original), mLines = CountLines(migrated);
            if (mLines < (int)(oLines * 0.6))
                return L.Tr($"line count collapsed ({oLines} → {mLines}) — content was likely dropped", $"行数骤减（{oLines} → {mLines}），疑似内容被丢弃");

            // Comment/string syntax is the same in HLSL/ShaderLab as in C#, so the C# masker applies.
            string masked = ScriptFixService.MaskNonCode(migrated);
            if (!ScriptFixService.BracketsBalanced(masked))
                return L.Tr("brackets are not balanced", "括号不配平");

            if (Regex.IsMatch(migrated, @"(?m)^\s*//\s*(\.\.\.|…)") || migrated.Contains("其余不变") || migrated.Contains("rest unchanged"))
                return L.Tr("contains an elision placeholder ('// ...') instead of the full file", "包含省略占位（// ...），不是完整文件");

            if (recipe?.MustDisappear != null)
            {
                foreach (var token in recipe.MustDisappear)
                    if (Regex.IsMatch(masked, @"\b" + Regex.Escape(token) + @"\b"))
                        return L.Tr($"migration incomplete: '{token}' still appears in code", $"迁移不完整：代码中仍出现 {token}");
            }

            if (isDotShader)
            {
                string oName = ShaderLabName(original), mName = ShaderLabName(migrated);
                if (oName != null && oName != mName)
                    return L.Tr($"the Shader name changed ('{oName}' → '{mName ?? "missing"}') — materials bind by this name", $"Shader 名被改动（'{oName}' → '{mName ?? "缺失"}'）——材质按此名绑定");

                var mProps = ShaderLabPropertyNames(migrated);
                foreach (var prop in ShaderLabPropertyNames(original))
                    if (!mProps.Contains(prop))
                        return L.Tr($"property '{prop}' disappeared from the Properties block — material values bind by property name", $"Properties 中的 {prop} 消失——材质数据按属性名绑定");
            }

            return null;
        }

        /// <summary>The name in the ShaderLab header: Shader "Some/Name". Null when absent (not a .shader, or broken).</summary>
        internal static string ShaderLabName(string source)
        {
            var m = Regex.Match(source ?? "", "\\bShader\\s+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Property names declared in the Properties block, e.g. _MainTex ("Albedo", 2D). Empty when there is no block.</summary>
        internal static HashSet<string> ShaderLabPropertyNames(string source)
        {
            var set = new HashSet<string>();
            var block = Regex.Match(source ?? "", @"\bProperties\s*\{", RegexOptions.IgnoreCase);
            if (!block.Success) return set;

            // Take the balanced { … } body of the Properties block, then match `_Name (` declarations inside it.
            int depth = 0, start = block.Index + block.Length - 1, end = -1;
            for (int i = start; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) { end = i; break; }
            }
            if (end < 0) return set;
            string body = source.Substring(start + 1, end - start - 1);
            foreach (Match m in Regex.Matches(body, @"(?m)^\s*(?:\[[^\]]*\]\s*)*(_\w+)\s*\("))
                set.Add(m.Groups[1].Value);
            return set;
        }

        /// <summary>
        /// Apply the whole-file rewrite: verify the on-disk file hasn't changed since Propose, back it up, register
        /// compile verification, write. Same backup/verify/rollback contract as ScriptFixService.Apply.
        /// </summary>
        public static bool Apply(MigrateProposal p, out string message)
        {
            message = null;
            try
            {
                string raw = SafeReadAllText(p.FilePath);
                if (raw == null) { message = L.Tr("Failed to read the file.", "读取文件失败。"); return false; }

                // The file changed between Propose and Apply (user edit, another fix) → this rewrite is based on
                // stale content and would silently revert those edits. Refuse; regenerate against the new content.
                if (raw.Replace("\r\n", "\n").Replace("\r", "\n") != p.Original)
                {
                    message = L.Tr("The file changed after this migration was generated; applying it would overwrite those changes. Please regenerate.",
                                   "生成迁移后文件已被改动，直接应用会覆盖那些改动。请重新生成。");
                    return false;
                }

                string backup = "Temp/PerfLint_backup_" + Guid.NewGuid().ToString("N") + ".txt";
                Directory.CreateDirectory("Temp");
                File.WriteAllText(Path.GetFullPath(backup), raw);
                PerfLintScriptFixVerifier.BeginVerify(p.FilePath, backup);

                File.WriteAllText(Path.GetFullPath(p.FilePath), AdaptLineEndings(p.Migrated, raw), new UTF8Encoding(false));

                RegisterForRetry(p);
                PerfLintFixCompileScheduler.RequestSoon();

                message = LlmSettings.AutoVerifyFix
                    ? L.Tr("File rewritten. It will be compile-verified in the background in a few seconds; a failure auto-rolls back the whole file (see Console). Check the visual result in your scene afterwards.",
                           "文件已整体重写。几秒后将后台编译校验，失败会自动回滚整个文件（见 Console）。之后请进场景确认渲染效果。")
                    : L.Tr("File rewritten. It will be verified on Unity's next compile; a failure auto-rolls back the whole file (see Console). Check the visual result in your scene afterwards.",
                           "文件已整体重写。Unity 下次编译时校验，失败会自动回滚整个文件（见 Console）。之后请进场景确认渲染效果。");
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        // ── Auto-retry loop (compile-error-driven, ≤MaxRetries rounds per user-approved Apply) ────────────
        // One Apply click authorizes the whole loop: write → compile-verify → on failure the Verifier rolls the
        // file back and fires FixRolledBack WITHOUT a domain reload (compilation failed) — so this static registry
        // is still alive and can feed the compiler errors back to the model for a regeneration. On success the
        // domain DOES reload and the registry evaporates — exactly the lifecycle we want. Every retry round passes
        // the same guards and gets the same backup/rollback protection as the first: the file can never end up
        // worse than before the click. Smoke-test evidence this shape works: five manual rounds converged
        // 4 → 6 → 3 → 1 error(s), and the last error (a 'builder' scope slip) is precisely the kind a
        // "here are the errors, fix them" round eliminates.
        private const int MaxRetries = 2;

        private sealed class RetryState
        {
            public MigrateRecipe Recipe;
            public string OriginalLf;      // pre-migration file (guards + stale check baseline)
            public string FailedMigrated;  // the rejected output, fed back to the model with the errors
            public int Attempt;            // retries already consumed
        }

        private static readonly Dictionary<string, RetryState> PendingRetry = new Dictionary<string, RetryState>();
        private static bool _retryHooked;

        private static void RegisterForRetry(MigrateProposal p)
        {
            if (!_retryHooked)
            {
                PerfLintScriptFixVerifier.FixRolledBack += OnRolledBack;
                _retryHooked = true;
            }
            PendingRetry[NormPath(p.FilePath)] = new RetryState
            {
                Recipe = p.Recipe,
                OriginalLf = p.Original,
                FailedMigrated = p.Migrated,
                Attempt = p.Attempt
            };
        }

        private static void OnRolledBack(string assetPath, string errorSummary)
        {
            string key = NormPath(assetPath);
            if (!PendingRetry.TryGetValue(key, out var st)) return; // not an AI Migrate write (e.g. an AI Fix rollback)

            if (st.Recipe == null || st.Attempt >= MaxRetries)
            {
                PendingRetry.Remove(key);
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"AI Migrate: still failing to compile after {MaxRetries} automatic retries — stopping (per-round errors above). Try a stronger model (bring your own key), or migrate manually via Explain.",
                    $"AI 迁移：自动重试 {MaxRetries} 轮后仍未通过编译，停止（每轮错误见上方日志）。可换更强模型（BYO key）重试，或按 Explain 指引手动迁移。"));
                return;
            }

            // A package change mid-loop (or one that predates the loop and was only now noticed) makes every
            // further round a guaranteed loss — the probes describe the old loaded packages. Stop, don't burn credits.
            bool staleNow;
            try { staleNow = DomainStaleProbe != null && DomainStaleProbe(); }
            catch { staleNow = false; }
            if (staleNow)
            {
                PendingRetry.Remove(key);
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    "AI Migrate: stopping the retry loop — the editor is running pre-change code (packages/plugin changed on disk but compile errors block reloading). Restart the editor, then run AI Migrate again.",
                    "AI 迁移：停止自动重试——编辑器正在运行变更前的代码（包/插件已在磁盘上变更，但编译错误阻止了重载）。请重启编辑器后重新执行 AI Migrate。"));
                return;
            }

            st.Attempt++;
            Debug.Log("[PerfLint] " + L.Tr(
                $"AI Migrate: compile failed — automatic retry {st.Attempt}/{MaxRetries}, feeding the compiler errors back to the model…",
                $"AI 迁移：编译未通过——自动重试第 {st.Attempt}/{MaxRetries} 轮，把编译错误喂回模型重生成…"));

            // Re-inject the environment probe facts: the first round had them in its user message, but a retry
            // round without them lets the model drift back to the pass shape its training data prefers.
            string facts = null;
            try { facts = st.Recipe.ProbeEnvironment?.Invoke(); }
            catch { /* probe failure just means fewer facts */ }

            string user = BuildRetryUser(st.FailedMigrated, errorSummary, facts);
            LlmClient.Send(LlmSettings.FixModel, st.Recipe.SystemPrompt, new[] { new LlmMessage("user", user) }, st.Recipe.MaxTokens, r =>
            {
                if (!r.Success)
                {
                    PendingRetry.Remove(key);
                    Debug.LogWarning("[PerfLint] " + L.Tr("AI Migrate retry failed: ", "AI 迁移重试失败：") + r.Error);
                    return;
                }
                var p2 = Parse(r.Text, st.OriginalLf, st.Recipe);
                p2.FilePath = assetPath;
                p2.Attempt = st.Attempt;
                if (!p2.Ok || p2.NoChange)
                {
                    PendingRetry.Remove(key);
                    Debug.LogWarning("[PerfLint] " + L.Tr("AI Migrate retry rejected by guards: ", "AI 迁移重试被守卫拒绝：") + (p2.Error ?? "no change"));
                    return;
                }
                st.FailedMigrated = p2.Migrated; // baseline for the next round, if this one fails too
                if (Apply(p2, out string msg))
                    Debug.Log("[PerfLint] " + L.Tr($"AI Migrate: retry {st.Attempt} written, verifying…", $"AI 迁移：第 {st.Attempt} 轮已写入，正在校验…"));
                else
                {
                    PendingRetry.Remove(key);
                    Debug.LogWarning("[PerfLint] " + L.Tr("AI Migrate retry could not be written: ", "AI 迁移重试写入失败：") + msg);
                }
            }, disableThinking: true);
        }

        /// <summary>Retry-round user message: the rejected output plus the compiler errors it produced (and the probe facts, re-stated).</summary>
        internal static string BuildRetryUser(string failedMigrated, string errorSummary, string facts = null) =>
            "你上一轮输出的迁移文件应用后产生了编译错误，已被回滚。请修复这些错误后重新输出完整文件——" +
            "仍然遵守 system 提示中的全部对照表、硬性要求与输出格式（<<<FILE>>>…<<<END>>>）。\n\n" +
            (string.IsNullOrEmpty(facts) ? "" : facts + "\n\n") +
            "【你上一轮的输出（含错误）】\n" + failedMigrated + "\n\n" +
            "【它产生的编译错误】\n" + errorSummary;

        private static string NormPath(string p) => (p ?? "").Replace('\\', '/');

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Strip leading/trailing blank lines and ``` fences around the file block; normalize to \n.</summary>
        internal static string CleanFileBlock(string s)
        {
            s = (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = new List<string>(s.Split('\n'));
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```")) lines.RemoveAt(0);
            if (lines.Count > 0 && lines[lines.Count - 1].TrimStart().StartsWith("```")) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines);
        }

        /// <summary>Names of all types declared in (already-masked) code.</summary>
        private static HashSet<string> TypeNames(string maskedCode)
        {
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches(maskedCode, @"\b(?:class|struct|interface|enum)\s+(\w+)"))
                set.Add(m.Groups[1].Value);
            return set;
        }

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 1;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        private static string NoWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string AdaptLineEndings(string snippetLf, string full)
        {
            string s = snippetLf.Replace("\r\n", "\n").Replace("\r", "\n");
            return full.Contains("\r\n") ? s.Replace("\n", "\r\n") : s;
        }

        private static string SafeReadAllText(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch { return null; }
        }
    }
}
