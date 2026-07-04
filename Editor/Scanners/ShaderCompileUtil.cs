using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Actively compiles a shader's passes (empty-keyword variant) to surface HLSL-body errors that lazy
    /// compilation hides: importing a shader only records ShaderLab PARSE errors, while HLSL bodies compile on
    /// first use (Inspector, scene, build) — so a broken body can sit silently behind ShaderHasError == false.
    /// This is the verification backbone for the shader migration recipe (docs/shader-recipe-plan.md, Spike 1):
    /// after a whole-file rewrite we must prove the result actually compiles, not merely that it re-imports.
    /// Built on the public ShaderData.Pass.CompileVariant API (2019.1+); if that API ever changes shape on a
    /// supported editor, Available comes back false and the version matrix's probe test goes red — the same
    /// contract as ShaderVariantUtil's internal-API probe.
    /// </summary>
    internal static class ShaderCompileUtil
    {
        internal sealed class Result
        {
            /// <summary>False when the CompileVariant path produced nothing (API unavailable / no compilable pass) — callers must degrade, not treat as success.</summary>
            public bool Available;
            /// <summary>True when at least one pass compiled and none produced errors.</summary>
            public bool Success;
            public readonly List<string> Errors = new List<string>();
            public int PassesCompiled;
        }

        // CompileVariant compiles ONE stage at a time. Vertex + fragment are the entry points every rasterization
        // pass must have; hull/domain/geometry are compiled too because tessellation errors hide there — real case
        // (stress test 2026-07-04): WaterTessellated's vertex+fragment compiled OK while the import state carried
        // an error from its tessellation stages. Passes without a given stage just skip it (the per-stage catch).
        private static readonly ShaderType[] Stages =
            { ShaderType.Vertex, ShaderType.Fragment, ShaderType.Hull, ShaderType.Domain, ShaderType.Geometry };

        /// <summary>
        /// Compile every pass of the shader's active subshader (vertex + fragment stages, empty-keyword variant)
        /// on the editor's own graphics API. Per-stage failures to even start a compile (UsePass/GrabPass shapes,
        /// missing stage) are skipped rather than reported — only real compiler errors land in <see cref="Result.Errors"/>.
        /// </summary>
        public static Result CompileCheck(Shader shader)
        {
            var r = new Result();
            if (shader == null) return r;
            try
            {
                var data = ShaderUtil.GetShaderData(shader);
                if (data == null || data.SubshaderCount == 0) return r;

                int subIdx = data.ActiveSubshaderIndex;
                if (subIdx < 0 || subIdx >= data.SubshaderCount) subIdx = 0;
                var sub = data.GetSubshader(subIdx);
                if (sub == null) return r;

                var platform = EditorCompilerPlatform();
                var target = EditorUserBuildSettings.activeBuildTarget;
                var seen = new HashSet<string>(); // vertex+fragment share the translation unit → dedupe identical errors

                for (int p = 0; p < sub.PassCount; p++)
                {
                    var pass = sub.GetPass(p);
                    bool compiledAnyStage = false;
                    foreach (var stage in Stages)
                    {
                        ShaderData.VariantCompileInfo info;
                        try { info = pass.CompileVariant(stage, new string[0], platform, target); }
                        catch { continue; } // this pass/stage isn't variant-compilable — skip, don't fail the check
                        compiledAnyStage = true;
                        if (info.Success) continue;
                        foreach (var m in info.Messages)
                        {
                            if (m.severity != ShaderCompilerMessageSeverity.Error) continue;
                            string line = FormatError(m, p);
                            if (seen.Add(line)) r.Errors.Add(line);
                        }
                    }
                    if (compiledAnyStage) r.PassesCompiled++;
                }

                r.Available = r.PassesCompiled > 0;
                r.Success = r.Available && r.Errors.Count == 0;
                return r;
            }
            catch
            {
                // GetShaderData / ShaderData surface changed on this editor → not available; callers degrade
                // to import-level checking (ShaderUtil.ShaderHasError) plus manual confirmation.
                return r;
            }
        }

        /// <summary>The compiler backend matching the editor's own graphics API — errors here are the ones the user actually sees.</summary>
        private static ShaderCompilerPlatform EditorCompilerPlatform()
        {
#if UNITY_EDITOR_OSX
            return ShaderCompilerPlatform.Metal;
#elif UNITY_EDITOR_LINUX
            return ShaderCompilerPlatform.Vulkan;
#else
            return ShaderCompilerPlatform.D3D;
#endif
        }

        /// <summary>One error line: "Pass N: message (file:line)" — the shape the migration retry loop feeds back to the model.</summary>
        private static string FormatError(ShaderMessage m, int passIndex)
        {
            string loc = string.IsNullOrEmpty(m.file) ? (m.line > 0 ? "line " + m.line : "") : m.file + ":" + m.line;
            return "Pass " + passIndex + ": " + m.message + (string.IsNullOrEmpty(loc) ? "" : " (" + loc + ")");
        }
    }
}
