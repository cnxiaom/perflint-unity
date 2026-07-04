using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Shader / build-cost diagnostics — the first slice of the "shader variant slimming" killer feature (build time +
    /// build size double-win). This slice is the deterministic, low-risk "A layer":
    ///   SHDR001 — Shaders that compile a very large number of variants (diagnosis; the per-shader build-cost number).
    ///   SHDR002 — Built-in pipeline: Instancing shader-variant stripping set to "Keep All" (one-click → Strip Unused).
    ///   SHDR004 — Shader source fails to compile (Migration domain: the "everything went magenta after upgrading" case).
    ///
    /// Out of scope here (later slices): URP/HDRP Global-Settings strip toggles; the evidence-based "record actually-used
    /// variants → strip the rest at build time" workflow (the Pro killer). Variant counting goes through the internal
    /// ShaderUtil API (see <see cref="ShaderVariantUtil"/>); when it's unavailable the count rule simply produces nothing.
    /// </summary>
    public sealed class ShaderScanner : IScanner, IFileScanner
    {
        public string Name => "Shaders";
        public Domain Domain => Domain.Performance;

        /// <summary>Single-file incremental (IFileScanner): only SHDR004 is per-file; the settings/variant rules are project-level. Lets the UI clear a fixed shader's finding right after AI Migrate instead of waiting for a full scan.</summary>
        public bool Handles(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".shader", System.StringComparison.OrdinalIgnoreCase)
            && !ScannerUtil.IsPerfLintOwnAsset(assetPath);

        public IEnumerable<Finding> ScanFile(string assetPath, ScanContext context)
        {
            if (!Handles(assetPath)) yield break;
            foreach (var f in ShaderErrorFindingsAt(assetPath)) yield return f;
        }

        // Flag shaders whose total variant count is at or above this — the build compiles and ships every one of them.
        // Conservative so we only surface genuinely heavy shaders (a trivial custom shader is in the hundreds; ShaderGraph /
        // big uber-shaders reach tens of thousands). Tunable once we see real numbers on a large project.
        private const long VariantWarnThreshold = 3000;
        private const int MaxReported = 25;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            foreach (var f in ScanShaderErrors(context)) yield return f;
            foreach (var f in ScanStrippingSettings()) yield return f;
            foreach (var f in ScanUrpStripUnusedVariants()) yield return f;
            foreach (var f in ScanVariantCounts(context)) yield return f;
        }

        // ── SHDR004 — Shader source fails to compile (Migration domain) ──────────────────────────────────
        // The most visible symptom of an upgrade gone wrong: shaders written for an older Unity / pipeline version stop
        // compiling and every material using them renders magenta. This complements MAT001, which covers the OTHER
        // magenta cause (right pipeline, wrong shader family) — here the shader itself is broken. Detection reads
        // Unity's own recorded compile state (ShaderUtil.ShaderHasError): ShaderLab parse errors are recorded at import;
        // HLSL body errors are recorded when Unity first compiles the shader (Inspector, scene use, build). A broken
        // shader nobody has ever touched can therefore go unreported until then — acceptable, because in the real
        // "everything went magenta after upgrading" scenario the errors are already cached by the time the user scans.
        // Assets/ only: a broken shader under Packages/ can't be fixed in place (the fix is a package up/downgrade), and
        // pipeline packages legitimately contain target-gated shaders. Report-only on purpose: shader repair is
        // structural (include paths, macros and syntax all shift between pipeline versions), so no AI Fix — Locate plus
        // the quoted compiler error feed Explain instead.
        private static IEnumerable<Finding> ScanShaderErrors(ScanContext context)
        {
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || ScannerUtil.IsPerfLintOwnAsset(path)) continue;
                foreach (var f in ShaderErrorFindingsAt(path))
                    yield return f;
            }
        }

        /// <summary>SHDR004 for a single shader asset — shared by the full scan and the single-file rescan so both paths produce identical findings.</summary>
        private static IEnumerable<Finding> ShaderErrorFindingsAt(string path)
        {
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (sh == null) yield break;

                bool hasError;
                try { hasError = ShaderUtil.ShaderHasError(sh); }
                catch { yield break; }
                if (!hasError) yield break;

                var (errorCount, firstMessage, firstLine) = FirstShaderError(sh);
                string file = Path.GetFileName(path);
                string cap = path;

                string countEn = errorCount > 0 ? $"{errorCount} compile error(s)" : "compile errors";
                string countCn = errorCount > 0 ? $"{errorCount} 个编译错误" : "编译错误";
                string quotedEn = firstMessage == null ? "" :
                    $" First error: \"{firstMessage}\"" + (firstLine > 0 ? $" (line {firstLine})." : ".");
                string quotedCn = firstMessage == null ? "" :
                    $"首个错误：「{firstMessage}」" + (firstLine > 0 ? $"（第 {firstLine} 行）。" : "。");

                yield return new Finding(
                    ruleId: "SHDR004",
                    domain: Domain.Migration,
                    severity: Severity.Critical, // an engine-recorded shader compile ERROR: everything using it renders magenta
                    title: L.Tr($"Shader fails to compile: '{file}'", $"着色器编译失败：'{file}'"),
                    groupTitle: L.Tr("Shaders fail to compile (materials render magenta)", "着色器编译失败（材质渲染为洋红）"),
                    detail: L.Tr(
                        $"'{sh.name}' has {countEn} — every material using it renders magenta (or not at all).{quotedEn} " +
                        "Shaders written for an older Unity or render-pipeline version commonly break after an upgrade: include paths, " +
                        "macros and syntax shift between versions. Click Locate and check the shader's Inspector for the full error list; " +
                        "fix the source, or get an updated version from the asset's publisher. If the asset is no longer maintained, " +
                        "consider switching the affected materials to a shader that ships with your current pipeline.",
                        $"'{sh.name}' 有{countCn}——所有使用它的材质都会渲染为洋红（或干脆不渲染）。{quotedCn}" +
                        "为旧版 Unity / 渲染管线编写的着色器在升级后很容易失效：include 路径、宏与语法随版本变化。" +
                        "点 Locate 后在该着色器的 Inspector 查看完整错误列表；修复源码，或向资产发布者获取更新版本。" +
                        "若该资产已停止维护，考虑把受影响的材质换成当前管线自带的着色器。"),
                    targetPath: path,
                    ping: () => ScannerUtil.PingAsset(cap));
        }

        /// <summary>
        /// First compiler ERROR (message, line) plus the total error count from Unity's import-time compile result.
        /// Returns (0, null, 0) when messages can't be read — the finding still reports, just without a quoted message.
        /// </summary>
        private static (int errorCount, string message, int line) FirstShaderError(Shader sh)
        {
            try
            {
                var msgs = ShaderUtil.GetShaderMessages(sh);
                int count = 0; string first = null; int line = 0;
                foreach (var m in msgs)
                {
                    if (m.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) continue;
                    count++;
                    if (first == null) { first = m.message; line = m.line; }
                }
                return (count, first, line);
            }
            catch { return (0, null, 0); }
        }

        // ── SHDR003 — URP: "Strip Unused Variants" disabled (advisory only) ──────────────────────────────
        // URP strips variants no material uses by default; if a project turned that off, builds carry many unneeded
        // variants. This is ADVISORY ONLY (no one-click) on purpose: re-enabling stripping can break a project that
        // legitimately toggles keywords at runtime (same missing-variant risk that warm-up exists to solve), so we explain
        // the trade-off both ways and let the user decide. Reading the setting is best-effort across URP versions (the
        // fields migrated from a flat m_StripUnusedVariants to a nested m_URPShaderStrippingSetting) — if we can't read it
        // confidently we emit nothing rather than risk a false positive.
        private static IEnumerable<Finding> ScanUrpStripUnusedVariants()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null || (rp.GetType().FullName ?? "").IndexOf("Universal", StringComparison.Ordinal) < 0)
                yield break;

            var prop = FindUrpStripUnusedVariants(out _);
            if (prop == null || prop.boolValue) yield break; // can't read, or already on (the safe default) → nothing to report

            yield return new Finding(
                ruleId: "SHDR003",
                domain: Domain.ProjectSettings,
                severity: Severity.Info,
                title: L.Tr("URP \"Strip Unused Variants\" is turned off", "URP「Strip Unused Variants」已关闭"),
                detail: L.Tr(
                    "URP strips shader variants no material uses by default, but this project has that turned off (Project Settings → Graphics → URP Global Settings → Shader Stripping), so the build compiles and ships variants it doesn't need — slower builds, bigger build. If you don't rely on toggling shader keywords at runtime, re-enabling it (URP's default) cuts build size. If you DO toggle keywords at runtime, keep it off and instead record a variant collection and warm it up (PerfLint's Shader Variants window) so those variants survive and preload. Advisory only — PerfLint won't change it automatically, because re-enabling could strip variants a runtime-keyword project actually needs.",
                    "URP 默认会剥掉没有材质使用的着色器变体，但本工程把它关了（Project Settings → Graphics → URP Global Settings → Shader Stripping），导致 build 编译并打包用不到的变体——打包更慢、包体更大。若你不靠运行时切换 shader keyword，重新开启（URP 默认）可省包体。若你确实运行时切 keyword，则保持关闭，改用录制变体集合 + 预热（PerfLint 的 Shader Variants 窗口），让那些变体留存并预加载。仅建议——PerfLint 不会自动改它，因为重开可能剥掉运行时 keyword 工程真正需要的变体。"),
                targetPath: null);
        }

        /// <summary>
        /// Best-effort read of URP's "Strip Unused Variants" boolean from the URP Global Settings asset, robust to the
        /// flat→nested field migration across URP versions: scan all serialized booleans for one whose name normalizes to
        /// "stripunusedvariants", preferring the one inside the newer m_URPShaderStrippingSetting container. Returns null
        /// (and a null <paramref name="so"/>) when URP isn't installed or the field can't be located.
        /// </summary>
        private static SerializedProperty FindUrpStripUnusedVariants(out SerializedObject so)
        {
            so = null;
            try
            {
                var guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineGlobalSettings");
                if (guids == null || guids.Length == 0) return null;
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (obj == null) return null;
                so = new SerializedObject(obj);

                SerializedProperty flat = null, nested = null;
                var it = so.GetIterator();
                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.Boolean) continue;
                    string n = it.name.ToLowerInvariant();
                    if (n.StartsWith("m_", StringComparison.Ordinal)) n = n.Substring(2);
                    if (n != "stripunusedvariants") continue; // exact (won't match ...PostProcessingVariants)
                    if (it.propertyPath.IndexOf("ShaderStripping", StringComparison.OrdinalIgnoreCase) >= 0) nested = it.Copy();
                    else flat = it.Copy();
                }
                return nested ?? flat; // prefer the newer nested container when both are serialized (Unity 6 migration)
            }
            catch { return null; }
        }

        // ── SHDR002 — Built-in pipeline: Instancing Variants = "Keep All" ────────────────────────────────
        private static IEnumerable<Finding> ScanStrippingSettings()
        {
            // These Graphics-Settings shader-stripping knobs only apply to the Built-in pipeline; under an SRP they're ignored,
            // so don't even read them (avoids a misleading finding on URP/HDRP projects).
            if (GraphicsSettings.currentRenderPipeline != null) yield break;

            // m_InstancingStripping: 0 = Strip Unused, 1 = Strip All, 2 = Keep All.
            int instancing = ReadGraphicsInt("m_InstancingStripping", -1);
            if (instancing == 2)
            {
                yield return new Finding(
                    ruleId: "SHDR002",
                    domain: Domain.ProjectSettings,
                    severity: Severity.Warning,
                    title: L.Tr("Instancing shader variants set to \"Keep All\"", "实例化着色器变体设为「Keep All」"),
                    detail: L.Tr(
                        "Graphics Settings → Shader Stripping → Instancing Variants is set to \"Keep All\", so every GPU-instancing variant is compiled into the build even when no material uses instancing — inflating build time and size. \"Strip Unused\" keeps only the variants actually needed.",
                        "Graphics Settings → Shader Stripping → Instancing Variants 设为「Keep All」，会把所有 GPU 实例化变体都编进包，即使没有材质用到实例化——增大打包时间与包体。改为「Strip Unused」只保留真正需要的变体。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Set to Strip Unused", "设为 Strip Unused"),
                        confirmMessage: L.Tr(
                            "Set Graphics Settings → Instancing Variants to \"Strip Unused\".\nThis modifies project settings (GraphicsSettings) and cannot be reverted with Edit > Undo; commit to version control first.",
                            "将 Graphics Settings → Instancing Variants 设为「Strip Unused」。\n此操作修改项目设置（GraphicsSettings），无法用 Edit > Undo 撤销；建议先提交版本控制。"),
                        run: () =>
                        {
                            if (!WriteGraphicsInt("m_InstancingStripping", 0))
                                return FixResult.Fail(L.Tr("Could not write the Graphics Settings property.", "无法写入 Graphics Settings 属性。"));
                            return FixResult.Ok(L.Tr("Instancing Variants set to Strip Unused.", "已将 Instancing Variants 设为 Strip Unused。"));
                        }));
            }
        }

        // ── SHDR001 — Shaders with a very large variant count (diagnosis) ────────────────────────────────
        private IEnumerable<Finding> ScanVariantCounts(ScanContext context)
        {
            if (!ShaderVariantUtil.Available) yield break; // internal API absent on this version → skip silently

            // Union of: every shader asset in the project (Assets/ AND Packages/), plus the Always Included Shaders.
            // Searching Packages/ too is the point — the heaviest shaders are usually the pipeline's own
            // (Universal Render Pipeline/Lit, HDRP/Lit live under Packages/com.unity.render-pipelines.*), and they ship
            // into every build all the same, so leaving them out hid the biggest offenders. (Engine built-in shaders like
            // "Standard"/"Hidden/*" are not project assets, so FindAssets still can't see them — a separate probe, later.)
            var shaders = new HashSet<Shader>();
            foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (ScannerUtil.IsPerfLintOwnAsset(path)) continue;
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (sh != null) shaders.Add(sh);
            }
            foreach (var sh in AlwaysIncludedShaders()) shaders.Add(sh);

            // Full keyword space for every shader → filter to the heavy ones. Keep the Shader so we can also read its
            // "used" count for just the few we actually report (computing both numbers for every shader would double the work).
            var hits = new List<(Shader sh, string path, long total)>();
            int i = 0, n = Math.Max(1, shaders.Count);
            foreach (var sh in shaders)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i++ / n);
                long total = ShaderVariantUtil.GetVariantCount(sh, usedBySceneOnly: false);
                if (total >= VariantWarnThreshold)
                    hits.Add((sh, AssetDatabase.GetAssetPath(sh), total)); // path may be Packages/ or empty for non-asset shaders
            }

            // Only the "total" keyword space is computed (a cheap combinatorial count). We deliberately do NOT compute the
            // "variants included" count here: GetVariantCount(shader, true) triggers Unity's "Enumerating used shader
            // variants" pass, which takes seconds PER heavy shader (tens of seconds on URP Lit/ComplexLit) and made a scan
            // run for minutes. It's also platform-dependent. Users who want it can read it in the shader's Inspector.
            foreach (var h in hits.OrderByDescending(x => x.total).Take(MaxReported))
            {
                string name = h.sh.name;
                string label = string.IsNullOrEmpty(h.path) ? name : Path.GetFileName(h.path);

                yield return new Finding(
                    ruleId: "SHDR001",
                    domain: Domain.Performance,
                    severity: Severity.Info,
                    title: L.Tr($"Shader has {FormatCount(h.total)} variants (keyword space)", $"着色器有 {FormatCount(h.total)} 个变体（keyword 空间）"),
                    groupTitle: L.Tr("Shader has a very large variant space", "着色器变体空间庞大"),
                    detail: L.Tr(
                        $"'{label}' ({name}) has {FormatCount(h.total)} variants in its full shader-keyword space (Unity's \"variants total\"). This is the theoretical maximum, NOT the shipped count — Unity strips variants no material uses, and the rest can be precompiled. For the actually-included count, open the shader's Inspector → 'Compile and show code' dropdown (\"variants included\"); PerfLint doesn't compute it during a scan because Unity's used-variant enumeration is slow per shader. A space this large costs shader import and build-compile time: cut keywords where you can (prefer shader_feature over multi_compile, or dynamic branching), keep URP's default variant stripping on, and warm up the variants you actually use (PerfLint's Shader Variants window) to avoid first-use hitches.",
                        $"'{label}'（{name}）的 keyword 全空间共 {FormatCount(h.total)} 个变体（即 Unity「variants total」）。这是理论上限、不是进包数——Unity 会剥掉没有材质用的变体，其余可预热。想看实际 included 数，打开该 shader 的 Inspector →「Compile and show code」下拉（\"variants included\"）；PerfLint 扫描时不算它，因为 Unity 的「枚举已用变体」每个 shader 都很慢。空间大会拖慢着色器导入与构建编译：尽量少 keyword（用 shader_feature 替 multi_compile、或动态分支）、保持 URP 默认变体剥离开启、并对实际用到的变体做预热（PerfLint 的 Shader Variants 窗口）避免首次卡顿。"),
                    targetPath: string.IsNullOrEmpty(h.path) ? null : h.path,
                    ping: string.IsNullOrEmpty(h.path) ? (Action)null : () => ScannerUtil.PingAsset(h.path));
            }
        }

        /// <summary>
        /// Abbreviate a variant count exactly like Unity's Shader Inspector (its internal FormatCount): K/M/B with two
        /// decimals, capped at "B" (so a quadrillion reads "2674132.54B", matching the Inspector verbatim). Keeping the
        /// same units lets users cross-check PerfLint's numbers against the Inspector at a glance.
        /// </summary>
        private static string FormatCount(long count)
        {
            if (count > 1000L * 1000 * 1000) return ((double)count / 1000000000.0).ToString("f2", CultureInfo.InvariantCulture.NumberFormat) + "B";
            if (count > 1000L * 1000)        return ((double)count / 1000000.0).ToString("f2", CultureInfo.InvariantCulture.NumberFormat) + "M";
            if (count > 1000)                return ((double)count / 1000.0).ToString("f2", CultureInfo.InvariantCulture.NumberFormat) + "K";
            return count.ToString();
        }

        private static IEnumerable<Shader> AlwaysIncludedShaders()
        {
            SerializedProperty arr;
            try
            {
                var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                arr = so.FindProperty("m_AlwaysIncludedShaders");
            }
            catch { yield break; }
            if (arr == null) yield break;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                if (el?.objectReferenceValue is Shader sh && sh != null) yield return sh;
            }
        }

        private static int ReadGraphicsInt(string prop, int fallback)
        {
            try
            {
                var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                var p = so.FindProperty(prop);
                return p != null ? p.intValue : fallback;
            }
            catch { return fallback; }
        }

        private static bool WriteGraphicsInt(string prop, int value)
        {
            try
            {
                var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                var p = so.FindProperty(prop);
                if (p == null) return false;
                p.intValue = value;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                return true;
            }
            catch { return false; }
        }
    }
}
