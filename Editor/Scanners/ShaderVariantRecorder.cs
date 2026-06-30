using System;
using System.Reflection;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Editor access to Unity's "currently tracked shader variant collection" — the set of variants Unity records as
    /// shaders are actually used (rendered) in the editor / Play Mode. This is the foundation of the B-layer
    /// record-and-strip workflow: <see cref="Clear"/> → the user plays representative scenes → <see cref="Save"/> the
    /// captured set to a .shadervariants asset, which then drives a build-time stripper (keep only these) and runtime warmup.
    ///
    /// The four ShaderUtil entry points are <c>extern internal static</c>, so we resolve them by reflection and degrade
    /// gracefully (counts return -1, actions return false) when a Unity version renames/removes them — the same hardening
    /// lesson as <see cref="ShaderVariantUtil"/> (Unity 6 silently changed GetVariantCount's signature without a release note).
    /// </summary>
    internal static class ShaderVariantRecorder
    {
        private static bool _resolved;
        private static MethodInfo _save, _clear, _shaderCount, _variantCount;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var t = typeof(ShaderUtil);
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _save = t.GetMethod("SaveCurrentShaderVariantCollection", F, null, new[] { typeof(string) }, null);
                _clear = t.GetMethod("ClearCurrentShaderVariantCollection", F, null, Type.EmptyTypes, null);
                _shaderCount = t.GetMethod("GetCurrentShaderVariantCollectionShaderCount", F, null, Type.EmptyTypes, null);
                _variantCount = t.GetMethod("GetCurrentShaderVariantCollectionVariantCount", F, null, Type.EmptyTypes, null);
            }
            catch { _save = _clear = _shaderCount = _variantCount = null; }
        }

        /// <summary>True when all four current-collection entry points resolved on this Unity version.</summary>
        public static bool Available
        {
            get { Resolve(); return _save != null && _clear != null && _shaderCount != null && _variantCount != null; }
        }

        /// <summary>Number of distinct shaders in the currently tracked collection; -1 if unavailable.</summary>
        public static int ShaderCount => InvokeCount(_shaderCount);

        /// <summary>Number of variants in the currently tracked collection; -1 if unavailable.</summary>
        public static int VariantCount => InvokeCount(_variantCount);

        private static int InvokeCount(MethodInfo m)
        {
            Resolve();
            if (m == null) return -1;
            try { return m.Invoke(null, null) is int i ? i : -1; }
            catch { return -1; }
        }

        /// <summary>Reset the currently tracked collection so a fresh capture can begin. Returns false if unavailable or the call throws.</summary>
        public static bool Clear()
        {
            Resolve();
            if (_clear == null) return false;
            try { _clear.Invoke(null, null); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Save the currently tracked variants to a .shadervariants asset at the given project-relative path
        /// (e.g. "Assets/PerfLint/Captured.shadervariants"). Returns false if unavailable, the path is empty, or the call throws.
        /// </summary>
        public static bool Save(string assetPath)
        {
            Resolve();
            if (_save == null || string.IsNullOrEmpty(assetPath)) return false;
            try { _save.Invoke(null, new object[] { assetPath }); return true; }
            catch { return false; }
        }
    }
}
