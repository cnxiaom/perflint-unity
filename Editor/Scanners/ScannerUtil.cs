using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;


namespace PerfLint.Scanners
{
    /// <summary>Small utilities shared across scanners. public: sub-assemblies for optional modules (e.g. Addressables) need access too.</summary>
    public static class ScannerUtil
    {
        /// <summary>Highlights and selects an asset in the Project window; used as Finding.Ping.</summary>
        public static void PingAsset(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
        }

        /// <summary>
        /// Selects and **opens** the script in the IDE/editor (unlike PingAsset which only highlights). When methodName is provided, attempts to jump to that method's declaration line.
        /// Used for the Locate action on script findings — users expect clicking Locate to open the code directly, not merely select it in the Project window.
        /// </summary>
        public static void OpenScript(string path, string methodName = null)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
            int line = FindMethodLine(path, methodName);
            if (line > 0) AssetDatabase.OpenAsset(obj, line);
            else AssetDatabase.OpenAsset(obj);
        }

        /// <summary>
        /// Roughly locates a method declaration line in a .cs file (heuristic): looks for occurrences with a correct name boundary followed by '(', preferring the line that looks like a declaration
        /// (not starting with return/await/yield, not ending with ';', excluding calls); otherwise falls back to the first occurrence. Returns 0 if not found. Reads the file only on click.
        /// </summary>
        public static int FindMethodLine(string scriptPath, string methodName)
        {
            if (string.IsNullOrEmpty(scriptPath) || string.IsNullOrEmpty(methodName)) return 0;
            try
            {
                var full = Path.GetFullPath(scriptPath);
                if (!File.Exists(full)) return 0;
                var lines = File.ReadAllLines(full);
                int firstHit = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    int idx = ln.IndexOf(methodName, StringComparison.Ordinal);
                    while (idx >= 0)
                    {
                        char before = idx > 0 ? ln[idx - 1] : ' ';
                        int after = idx + methodName.Length;
                        bool boundaryBefore = !(char.IsLetterOrDigit(before) || before == '_' || before == '.');
                        int p = after;
                        while (p < ln.Length && ln[p] == ' ') p++;
                        bool followedByParen = p < ln.Length && ln[p] == '(';
                        if (boundaryBefore && followedByParen)
                        {
                            if (firstHit == 0) firstHit = i + 1;
                            string t = ln.TrimStart();
                            bool looksLikeCall = t.StartsWith("return ") || t.StartsWith("await ") ||
                                                 t.StartsWith("yield ") || ln.TrimEnd().EndsWith(";");
                            if (!looksLikeCall) return i + 1; // 1-based
                        }
                        idx = ln.IndexOf(methodName, idx + 1, StringComparison.Ordinal);
                    }
                }
                return firstHit;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Selects and opens the script in the IDE/editor, jumping to the specified line (1-based). When line &lt;= 0, falls back to opening at the top of the file.
        /// Used for the Locate action on line-precise script findings (e.g. the first occurrence of Debug.Log).
        /// </summary>
        public static void OpenScriptAtLine(string path, int line)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
            if (line > 0) AssetDatabase.OpenAsset(obj, line);
            else AssetDatabase.OpenAsset(obj);
        }

        /// <summary>On-disk byte size of an asset file; returns 0 if unavailable.</summary>
        public static long FileSizeBytes(string assetPath)
        {
            try
            {
                var full = ToPhysicalFullPath(assetPath);
                return File.Exists(full) ? new FileInfo(full).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Estimated in-memory byte size of an asset, for ranking duplicates by actual impact rather than raw file
        /// size. For textures this uses the internal <c>TextureUtil.GetStorageMemorySizeLong</c> — the same value the
        /// texture Inspector reports (accounts for the platform's real compressed GPU format + mips). For other asset
        /// types it falls back to <c>Profiler.GetRuntimeMemorySizeLong</c>. Returns 0 when it can't be determined
        /// (caller should fall back to <see cref="FileSizeBytes"/>). Loads the asset, so call sparingly (ranking only).
        /// </summary>
        public static long StorageMemoryBytes(string assetPath)
        {
            try
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (obj == null) return 0;
                if (obj is Texture tex)
                {
                    long s = TextureStorageMemory(tex);
                    if (s > 0) return s;
                }
                long rt = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(obj);
                return rt > 0 ? rt : 0;
            }
            catch { return 0; }
        }

        // Reflection into the internal UnityEditor.TextureUtil.GetStorageMemorySizeLong(Texture): the value shown in
        // the texture Inspector (platform's real compressed format + mips). Cached; returns 0 if the API is absent.
        private static System.Reflection.MethodInfo _texStorageMem;
        private static bool _texStorageMemResolved;
        private static long TextureStorageMemory(Texture tex)
        {
            try
            {
                if (!_texStorageMemResolved)
                {
                    _texStorageMemResolved = true;
                    var t = typeof(AssetDatabase).Assembly.GetType("UnityEditor.TextureUtil");
                    _texStorageMem = t?.GetMethod("GetStorageMemorySizeLong",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
                if (_texStorageMem == null) return 0;
                object res = _texStorageMem.Invoke(null, new object[] { tex });
                return res is long l ? l : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Resolves an asset path to its absolute on-disk path. `Assets/` can be concatenated directly; but `Packages/` is a Unity virtual mapping —
        /// for immutable packages (e.g. URP installed via Package Manager) the physical files live under `Library/PackageCache/...`,
        /// so a plain `Path.GetFullPath("Packages/...")` yields a path that does not exist on disk. `FileUtil.GetPhysicalPath`
        /// knows this mapping and resolves to the real location (returning `Assets/` paths unchanged). Falls back to plain concatenation on failure.
        /// </summary>
        public static string ToPhysicalFullPath(string assetPath)
        {
            try
            {
                var physical = FileUtil.GetPhysicalPath(assetPath);
                if (!string.IsNullOrEmpty(physical))
                    return Path.GetFullPath(physical);
            }
            catch { /* older versions or malformed paths, fall back */ }
            return Path.GetFullPath(assetPath);
        }

        /// <summary>
        /// Name of the currently active build platform, used to query per-platform overrides on texture/audio importers (GetPlatformTextureSettings, etc.).
        /// Most platforms use the BuildTargetGroup name ("Standalone"/"WebGL"/"Android"…); the texture importer keeps legacy names for iOS/WSA
        /// ("iPhone"/"Windows Store Apps"). When the name does not match, importer queries safely fall back to the Default settings without erroring.
        /// </summary>
        public static string ActivePlatformName() => PlatformName(EditorUserBuildSettings.activeBuildTarget);

        /// <summary>
        /// Maps a build target to the importer platform-override name (pure, so it's unit-testable without switching
        /// the active build target). iOS/WSA keep the importer's legacy names ("iPhone"/"Windows Store Apps"); all
        /// others use the BuildTargetGroup name ("Standalone"/"Android"/"WebGL"…).
        /// </summary>
        internal static string PlatformName(BuildTarget t)
        {
            if (t == BuildTarget.iOS) return "iPhone";
            if (t == BuildTarget.WSAPlayer) return "Windows Store Apps";
            return BuildPipeline.GetBuildTargetGroup(t).ToString();
        }

        public static string Human(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes} B";
        }
    }
}
