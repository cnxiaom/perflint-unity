using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Reflection access to the editor-internal shader-variant count. <c>UnityEditor.ShaderUtil.GetVariantCount</c> is
    /// internal and undocumented, and its signature has shifted across Unity versions (the historical
    /// <c>(Shader, bool usedBySceneOnly)</c> did NOT resolve on Unity 6). So we resolve it ADAPTIVELY — match by name with a
    /// <see cref="Shader"/> first parameter, then fill whatever remaining parameters it declares — and return -1 when it
    /// can't be found or the call fails (callers then skip the variant diagnosis instead of throwing). Mirrors the
    /// optional-internal-API pattern already used in <see cref="ScannerUtil"/> (TextureUtil.GetStorageMemorySizeLong).
    /// </summary>
    internal static class ShaderVariantUtil
    {
        private static bool _resolved;
        private static MethodInfo _getVariantCount;

        /// <summary>Last invocation failure detail (exception message or unexpected return type), for diagnostics/tests. Empty on success.</summary>
        public static string LastError { get; private set; } = "";

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                MethodInfo best = null;
                foreach (var m in typeof(ShaderUtil).GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "GetVariantCount") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(Shader)) continue;
                    // Prefer the classic (Shader, bool) overload; otherwise take the one with the fewest parameters
                    // (a newer single-(Shader) form, or a differently-typed second arg we fill generically below).
                    if (best == null) { best = m; continue; }
                    var bp = best.GetParameters();
                    bool mShaderBool = ps.Length == 2 && ps[1].ParameterType == typeof(bool);
                    bool bShaderBool = bp.Length == 2 && bp[1].ParameterType == typeof(bool);
                    if (mShaderBool && !bShaderBool) best = m;
                    else if (!bShaderBool && ps.Length < bp.Length) best = m;
                }
                _getVariantCount = best;
            }
            catch { _getVariantCount = null; }
        }

        /// <summary>True when the internal variant-count API resolved on this Unity version.</summary>
        public static bool Available { get { Resolve(); return _getVariantCount != null; } }

        /// <summary>The resolved method's signature (for diagnostics/tests); empty when unresolved.</summary>
        public static string ResolvedSignature
        {
            get
            {
                Resolve();
                if (_getVariantCount == null) return "";
                var ps = Array.ConvertAll(_getVariantCount.GetParameters(), p => p.ParameterType.Name);
                return _getVariantCount.Name + "(" + string.Join(",", ps) + ")";
            }
        }

        /// <summary>
        /// Variant count for a shader, via the editor's own metric — the exact values Unity's built-in Shader Inspector
        /// shows. <paramref name="usedBySceneOnly"/> maps to the API's bool (Unity 6 names it <c>shouldStrip</c>):
        /// <c>false</c> → "variants total", the full combinatorial keyword space (often astronomical);
        /// <c>true</c> → "variants included", i.e. the total minus shader_feature combinations that NO material enables.
        /// NOTE: <c>true</c> only strips unused <c>shader_feature</c> keywords — every <c>multi_compile</c> variant is still
        /// counted, so it is NOT the final shipped count (the build strips multi_compile further by settings/usage).
        /// Returns -1 when the API is unavailable or the call fails, and -1 for a null shader.
        /// </summary>
        public static long GetVariantCount(Shader shader, bool usedBySceneOnly = false)
        {
            if (shader == null) return -1;
            Resolve();
            if (_getVariantCount == null) return -1;
            try
            {
                var ps = _getVariantCount.GetParameters();
                var args = new object[ps.Length];
                args[0] = shader;
                int outIndex = -1;
                for (int i = 1; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt.IsByRef)
                    {
                        // Unity 6 form: bool GetVariantCount(Shader, bool, out ulong count) — the count comes back here.
                        outIndex = i;
                        var et = pt.GetElementType();
                        args[i] = et != null && et.IsValueType ? Activator.CreateInstance(et) : null;
                    }
                    else if (pt == typeof(bool)) args[i] = usedBySceneOnly;        // the usedBySceneOnly flag
                    else if (pt.IsEnum) args[i] = Enum.GetValues(pt).GetValue(0);
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }

                object res = _getVariantCount.Invoke(null, args);

                // Old form returns the count directly (ulong); the Unity 6 form returns a success bool and writes the
                // count to the `out ulong` arg (reflection copies it back into args[outIndex]).
                long fromReturn = ToLong(res);
                if (fromReturn >= 0) { LastError = ""; return fromReturn; }
                if (outIndex >= 0)
                {
                    long fromOut = ToLong(args[outIndex]);
                    if (fromOut >= 0) { LastError = ""; return fromOut; }
                }

                LastError = "unexpected return/out type: ret=" + (res?.GetType().FullName ?? "null")
                            + (outIndex >= 0 ? ", out=" + (args[outIndex]?.GetType().FullName ?? "null") : "");
                return -1;
            }
            catch (Exception ex)
            {
                LastError = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message;
                return -1;
            }
        }

        private static long ToLong(object o)
        {
            switch (o)
            {
                case ulong u: return (long)u;
                case long l: return l;
                case uint ui: return ui;
                case int i: return i;
                default: return -1;
            }
        }
    }
}
