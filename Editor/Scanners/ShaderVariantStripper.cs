using System.Collections.Generic;
using PerfLint.Licensing;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Build-time shader-variant stripper (B layer). Keeps only the variants recorded in a user-chosen
    /// ShaderVariantCollection (from the record step), pruning the rest so the build compiles and ships far fewer variants.
    ///
    /// Safety (stripping a needed variant only shows up as pink in the built player, so every choice biases toward keeping):
    ///  - OFF by default. Runs only when the user explicitly picks an SVC and enables it, and only for Pro.
    ///  - CONSERVATIVE by default: prunes ONLY shaders that appear in the captured SVC; shaders never captured are left
    ///    fully intact (a scene you forgot to exercise can't get nuked). Aggressive mode (prune every non-internal shader) is opt-in.
    ///  - INTERNAL shaders are never stripped, in any mode: anything in Unity's "Hidden/" namespace (CoreBlit & the other
    ///    SRP blit/utility shaders) is left intact — a missing internal variant blacks out the whole frame (vs a recoverable
    ///    magenta material), and the editor SVC recorder can't capture their platform-keyword variant set anyway.
    ///  - Fail-safe: if a variant's keywords can't be resolved, it is KEPT, never stripped.
    ///
    /// Public so Unity's build pipeline reliably discovers the IPreprocessShaders implementor; it early-outs cheaply on
    /// every build when disabled, so the always-present callback is harmless for Free users and disabled projects.
    /// </summary>
    public sealed class ShaderVariantStripper : IPreprocessShaders
    {
        // Run late so the SRP/built-in settings strippers prune first; we then prune down to the captured set.
        public int callbackOrder => 2000;

        private readonly bool _enabled;
        private readonly bool _aggressive;
        private readonly bool _logStripped;
        private readonly ShaderVariantCollection _keep;
        private readonly HashSet<Shader> _capturedShaders;

        internal static int Stripped;
        internal static int Kept;

        private const int MaxLoggedStrips = 2000; // cap diagnostic logging so a big build doesn't flood the console

        public ShaderVariantStripper()
        {
            var s = ShaderStripSettings.instance;
            _aggressive = s.aggressive;
            _logStripped = s.logStripped;
            _enabled = s.enabled
                       && Entitlements.IsPro                 // execution is a Pro feature
                       && !string.IsNullOrEmpty(s.svcPath);
            if (_enabled)
            {
                _keep = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(s.svcPath);
                if (_keep == null) _enabled = false;
                else if (!_aggressive) _capturedShaders = ReadShaders(_keep);
            }
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!_enabled || _keep == null) return;
            if (!ShouldProcessShader(_aggressive, _capturedShaders, shader)) return;

            for (int i = data.Count - 1; i >= 0; i--)
            {
                bool keep;
                string[] names = null;
                try
                {
                    var kws = data[i].shaderKeywordSet.GetShaderKeywords();
                    names = new string[kws.Length];
                    for (int k = 0; k < kws.Length; k++) names[k] = kws[k].name;
                    keep = _keep.Contains(new ShaderVariantCollection.ShaderVariant(shader, snippet.passType, names));
                }
                catch { keep = true; } // can't resolve the variant → never strip it
                if (!keep)
                {
                    data.RemoveAt(i);
                    Stripped++;
                    if (_logStripped) LogStripped(shader, snippet.passType, names);
                }
                else Kept++;
            }
        }

        /// <summary>
        /// Diagnostic: print a stripped variant exactly as Strict Shader Variant Matching reports a MISSING one
        /// (shader / pass / keywords), so the two logs can be lined up. If a variant appears in BOTH — stripped here AND
        /// reported missing by strict matching — and it's also in the SVC, that's a matching bug (we over-stripped a
        /// captured variant); if it's not in the SVC, the capture was incomplete.
        /// </summary>
        private static void LogStripped(Shader shader, PassType pass, string[] names)
        {
            if (Stripped > MaxLoggedStrips)
            {
                if (Stripped == MaxLoggedStrips + 1)
                    Debug.Log($"[PerfLint strip] … further stripped-variant logs suppressed past {MaxLoggedStrips}.");
                return;
            }
            string kw = (names == null || names.Length == 0) ? "<none>" : string.Join(" ", names);
            Debug.Log($"[PerfLint strip] STRIPPED  {shader.name}  | pass={pass} | kw=[{kw}]");
        }

        /// <summary>
        /// Conservative-policy gate (pure, unit-testable): in conservative mode only shaders present in the captured set
        /// are pruned; aggressive mode prunes every shader. Keeps the safety decision out of the build-only code path.
        /// </summary>
        internal static bool ShouldProcessShader(bool aggressive, HashSet<Shader> captured, Shader shader)
        {
            if (IsInternalShader(shader)) return false; // CoreBlit & other Hidden/ SRP shaders: a missing variant blacks out the frame — never strip, even aggressive
            return aggressive || (captured != null && captured.Contains(shader));
        }

        /// <summary>
        /// Engine/SRP-internal shaders live in Unity's <c>Hidden/</c> namespace (CoreBlit, blit, deferred, the error shader, …).
        /// They are NEVER stripped — not even in aggressive mode: a missing internal variant blacks out the whole frame
        /// (unlike a recoverable magenta material), and the editor SVC recorder can't capture their platform-keyword variant
        /// set anyway. The few KB they cost isn't worth the black-screen risk.
        /// </summary>
        internal static bool IsInternalShader(Shader shader) => shader != null && IsInternalShaderName(shader.name);

        /// <summary>Pure name test for <see cref="IsInternalShader"/> (split out so it's unit-testable without a Shader asset).</summary>
        internal static bool IsInternalShaderName(string name)
            => !string.IsNullOrEmpty(name) && name.StartsWith("Hidden/", System.StringComparison.Ordinal);

        /// <summary>Distinct shaders referenced by an SVC, read from its serialized <c>m_Shaders</c> array.</summary>
        internal static HashSet<Shader> ReadShaders(ShaderVariantCollection svc)
        {
            var set = new HashSet<Shader>();
            if (svc == null) return set;
            try
            {
                var so = new SerializedObject(svc);
                var arr = so.FindProperty("m_Shaders");
                if (arr != null)
                    for (int i = 0; i < arr.arraySize; i++)
                    {
                        var first = arr.GetArrayElementAtIndex(i).FindPropertyRelative("first");
                        if (first != null && first.objectReferenceValue is Shader sh && sh != null) set.Add(sh);
                    }
            }
            catch { /* keep whatever we gathered */ }
            return set;
        }
    }

    /// <summary>Resets the strip counters before each build and logs a one-line summary after, so the effect is visible in the build log.</summary>
    public sealed class ShaderVariantStripperReport : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) { ShaderVariantStripper.Stripped = 0; ShaderVariantStripper.Kept = 0; }
        public void OnPostprocessBuild(BuildReport report)
        {
            if (ShaderVariantStripper.Stripped > 0 || ShaderVariantStripper.Kept > 0)
                Debug.Log($"[PerfLint] Shader variant stripping — kept {ShaderVariantStripper.Kept}, stripped {ShaderVariantStripper.Stripped}.");
        }
    }
}
