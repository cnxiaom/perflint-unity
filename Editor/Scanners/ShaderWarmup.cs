using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Runtime shader warm-up via Graphics Settings → Preloaded Shaders (<c>m_PreloadedShaders</c>). Adding a captured
    /// ShaderVariantCollection there makes Unity compile its variants at startup, eliminating the first-use shader hitches
    /// without any runtime code. Unlike the build-time stripper, warm-up only PRE-COMPILES — it never removes anything, so
    /// it cannot break the build (no pink / black screens). This is the safe, shippable half of the B-layer.
    /// </summary>
    internal static class ShaderWarmup
    {
        private const string Prop = "m_PreloadedShaders";

        public static bool IsPreloaded(ShaderVariantCollection svc)
        {
            if (svc == null) return false;
            try
            {
                var arr = new SerializedObject(GraphicsSettings.GetGraphicsSettings()).FindProperty(Prop);
                if (arr == null) return false;
                for (int i = 0; i < arr.arraySize; i++)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == svc) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>Add the collection to Preloaded Shaders (no-op if already present). Returns false on failure.</summary>
        public static bool AddToPreload(ShaderVariantCollection svc)
        {
            if (svc == null) return false;
            try
            {
                var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                var arr = so.FindProperty(Prop);
                if (arr == null) return false;
                for (int i = 0; i < arr.arraySize; i++)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == svc) return true; // already there
                int idx = arr.arraySize;
                arr.InsertArrayElementAtIndex(idx);
                arr.GetArrayElementAtIndex(idx).objectReferenceValue = svc; // insert duplicates the prior slot; set ours explicitly
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Remove the collection from Preloaded Shaders. Returns false on failure (true even if it wasn't present).</summary>
        public static bool RemoveFromPreload(ShaderVariantCollection svc)
        {
            if (svc == null) return false;
            try
            {
                var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                var arr = so.FindProperty(Prop);
                if (arr == null) return false;
                bool changed = false;
                for (int i = arr.arraySize - 1; i >= 0; i--)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == svc)
                    {
                        // Object-reference arrays: null the slot first, then delete, or the first delete only nulls it.
                        arr.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        arr.DeleteArrayElementAtIndex(i);
                        changed = true;
                    }
                if (changed) { so.ApplyModifiedProperties(); AssetDatabase.SaveAssets(); }
                return true;
            }
            catch { return false; }
        }
    }
}
