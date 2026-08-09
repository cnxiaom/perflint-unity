using System;
using System.Collections.Generic;
using System.IO;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;


namespace PerfLint.Scanners
{
    /// <summary>Small utilities shared across scanners. public: sub-assemblies for optional modules (e.g. Addressables) need access too.</summary>
    public static class ScannerUtil
    {
        /// <summary>
        /// "Environment/Rocks/Rock_01" — a scene object's place in the hierarchy, stable enough to persist.
        ///
        /// Not unique: siblings can share a name. That is accepted deliberately, because the alternative (instance
        /// IDs) does not survive a domain reload at all, and this is used to re-find a GROUP of examples where
        /// picking up a same-named sibling is a far smaller error than picking up nothing.
        /// </summary>
        public static string HierarchyPath(UnityEngine.GameObject go)
        {
            if (go == null) return null;
            var sb = new System.Text.StringBuilder(go.name);
            for (var t = go.transform.parent; t != null; t = t.parent)
                sb.Insert(0, t.name + "/");
            return sb.ToString();
        }

        /// <summary>
        /// Re-finds objects by the paths <see cref="HierarchyPath"/> produced, across every loaded scene.
        ///
        /// Uses FindObjectsOfTypeAll so INACTIVE objects are found too — a disabled renderer is exactly the kind of
        /// thing someone is looking for. Prefab-asset objects are excluded: they live in no scene and selecting them
        /// would answer a question about the open scene with something else entirely.
        ///
        /// Returns only what still exists. A scene edited since the measurement is the normal case, not an error.
        /// </summary>
        public static List<UnityEngine.GameObject> FindByHierarchyPaths(IReadOnlyList<string> paths)
        {
            var found = new List<UnityEngine.GameObject>();
            if (paths == null || paths.Count == 0) return found;

            var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
            foreach (var go in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.GameObject>())
            {
                if (go == null) continue;
                if (!go.scene.IsValid() || string.IsNullOrEmpty(go.scene.name)) continue;   // prefab asset, not a scene object
                if (wanted.Contains(HierarchyPath(go))) found.Add(go);
            }
            return found;
        }
        /// <summary>How many heavy asset loads a scan loop may accumulate before forcing a memory reclaim. See <see cref="ThrottleReclaim"/>.</summary>
        public const int LoadReclaimInterval = 64;

        /// <summary>
        /// Memory-reclaim throttle for scan loops that <c>LoadAssetAtPath</c> heavy assets (textures, materials, audio clips).
        /// A scan runs synchronously in one call stack and never yields a frame, so every loaded asset stays resident until
        /// the scan returns. On a large project the loads accumulate in graphics/native memory until the driver can't
        /// allocate and the editor hard-crashes (d3d11 E_OUTOFMEMORY, observed on an ~12k-texture project 2026-07-05).
        /// Call once per load with the running counter; every <see cref="LoadReclaimInterval"/> loads it forces a
        /// synchronous sweep, bounding peak memory to a small constant. Returns the updated counter (reset to 0 on a
        /// sweep). The sweep cost is negligible (~1 per 64 loads).
        ///
        /// This sweep is the ONLY reclaim a scan loop may use. <c>Resources.UnloadAsset</c> looks like a cheaper, more
        /// targeted alternative but is not reference-aware: it drops an asset's payload even while the open scene is
        /// rendering with it, blacking the scene out until a reimport (bisected to TEX005 on 2026-07-25). The sweep,
        /// by contrast, reclaims only what nothing references — exactly the scan's own leftovers.
        /// </summary>
        public static int ThrottleReclaim(int loadsSinceReclaim)
        {
            if (++loadsSinceReclaim >= LoadReclaimInterval)
            {
                EditorUtility.UnloadUnusedAssetsImmediate();
                return 0;
            }
            return loadsSinceReclaim;
        }

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
        /// <summary>
        /// Says why a Locate did nothing, instead of the button appearing broken.
        ///
        /// A finding is written once and kept; the file it names can be deleted, renamed or moved afterwards, and
        /// then LoadAssetAtPath returns null and the click is swallowed. Hit for real: a script was removed while a
        /// stored session still listed the hotspot in it, and the button simply did not respond — indistinguishable
        /// from a bug in the button.
        /// </summary>
        static void ReportMissingScript(string path)
        {
            UnityEngine.Debug.LogWarning("[PerfLint] " + L.Tr(
                $"Can't open {path} — it no longer exists in the project. This finding is from an earlier measurement; sample again to refresh it.",
                $"打不开 {path}——它已不在工程里。这条结论来自更早的一次测量，重新采样即可刷新。"));
        }

        public static void OpenScript(string path, string methodName = null)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) { ReportMissingScript(path); return; }
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
                        // Allow a generic parameter list before the parens, e.g. "LoadAssetAsync<T>(" / "Get<TKey, TVal>(" — the profiler marker drops
                        // the generic args (shows "LoadAssetAsync()"), so without this an async/generic method wouldn't match and Locate fell to line 1.
                        if (p < ln.Length && ln[p] == '<')
                        {
                            int depth = 0;
                            while (p < ln.Length)
                            {
                                if (ln[p] == '<') depth++;
                                else if (ln[p] == '>') { depth--; if (depth == 0) { p++; break; } }
                                p++;
                            }
                            while (p < ln.Length && ln[p] == ' ') p++;
                        }
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
            if (obj == null) { ReportMissingScript(path); return; }
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
            if (line > 0) AssetDatabase.OpenAsset(obj, line);
            else AssetDatabase.OpenAsset(obj);
        }

        /// <summary>
        /// Splits the "Assets/X.cs:42" form the script findings use into a path and a 1-based line. False when the
        /// input is not that form.
        ///
        /// One copy, because there were two and they disagreed: <see cref="Core.ScanResultStore"/> parsed it when
        /// rebuilding a restored finding's jump, and the runtime session store did not — so the same convention
        /// worked in the static report and silently degraded to "highlight the file" in the runtime one.
        /// </summary>
        public static bool TryParsePathLine(string s, out string path, out int line)
        {
            path = null; line = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int colon = s.LastIndexOf(':');
            if (colon <= 0 || colon == s.Length - 1) return false;
            if (!int.TryParse(s.Substring(colon + 1), out line) || line <= 0) { line = 0; return false; }
            path = s.Substring(0, colon);
            return path.EndsWith(".cs", StringComparison.Ordinal);
        }

        /// <summary>
        /// The "Assets/X.cs:42" target for a finding that knows a method rather than a line, resolving the
        /// declaration line now so it survives being written to disk. Falls back to the bare path when the method
        /// cannot be located.
        ///
        /// Runtime findings knew the method all along and put it only in a closure — which does not survive a domain
        /// reload, so a restored "heaviest allocator MouseLock.Update()" could highlight the file and nothing more.
        /// </summary>
        public static string ScriptTarget(string scriptPath, string methodName)
        {
            if (string.IsNullOrEmpty(scriptPath)) return scriptPath;
            int line = FindMethodLine(scriptPath, methodName);
            return line > 0 ? $"{scriptPath}:{line}" : scriptPath;
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
        /// MD5 of a file's bytes, Base64-encoded — the identity ASSET.DUP001 groups duplicate copies by. Null when the
        /// file cannot be read. Shared so every rule asking "are these the same bytes?" answers it the same way.
        ///
        /// Pure file I/O on purpose: DUP001 hashes its candidates across cores, and <see cref="ToPhysicalFullPath"/>
        /// goes through <c>FileUtil</c>, a UnityEditor API that must not be touched from a worker thread. A path this
        /// resolver cannot open (a package under a symlink, say) hashes to null, which leaves it in a class of its own
        /// — the safe direction to fail in, since nothing is then treated as a duplicate of anything.
        /// </summary>
        public static string ContentHash(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                if (!File.Exists(full)) return null;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var fs = File.OpenRead(full))
                    return Convert.ToBase64String(md5.ComputeHash(fs));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Splits paths into classes of byte-identical files. Size decides first — a file with a unique size cannot
        /// duplicate anything, so it is never hashed. Single-file classes come back too, so a caller can walk every
        /// class the same way. Classes are returned in first-appearance order and each is ordinal-sorted, so "take the
        /// first of each class" is a stable choice across runs.
        ///
        /// Exists because two paths in one work list can be the same bytes, and an action that treats them as separate
        /// assets can do real damage: ASSET.AADUP001's "extract all" marked both copies of a duplicate pair
        /// Addressable, which pinned two copies of the same bytes in the shared group AND made both undeletable by the
        /// DUP001 merge — an Addressables entry is loaded by address, which a GUID redirect cannot repair. Measured on
        /// one project: 4 of its 8 unmergeable duplicate groups had been created that way, by us.
        /// </summary>
        public static List<List<string>> GroupByIdenticalContent(
            IReadOnlyList<string> paths, Func<string, long> sizeOf = null, Func<string, string> hashOf = null)
        {
            var classes = new List<List<string>>();
            if (paths == null || paths.Count == 0) return classes;
            sizeOf ??= FileSizeBytes;
            hashOf ??= ContentHash;

            var order = new List<string>();                                       // distinct, first-appearance order
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in paths) if (!string.IsNullOrEmpty(p) && seen.Add(p)) order.Add(p);

            // Size classes first; only a class with two or more members is worth hashing.
            var bySize = new Dictionary<long, List<string>>();
            foreach (var p in order)
            {
                long size = sizeOf(p);
                if (!bySize.TryGetValue(size, out var list)) { list = new List<string>(); bySize[size] = list; }
                list.Add(p);
            }

            var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);   // path -> "size:hash", or its own path
            foreach (var kv in bySize)
                foreach (var p in kv.Value)
                {
                    string hash = kv.Value.Count >= 2 ? hashOf(p) : null;
                    // Unhashed or unreadable: the path is its own class. Never guess two files are the same.
                    keyOf[p] = hash == null ? "\0" + p : kv.Key + ":" + hash;
                }

            var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var p in order)
            {
                string key = keyOf[p];
                if (!byKey.TryGetValue(key, out var list)) { list = new List<string>(); byKey[key] = list; classes.Add(list); }
                list.Add(p);
            }
            foreach (var c in classes) c.Sort(StringComparer.Ordinal);
            return classes;
        }

        /// <summary>
        /// Estimated in-memory byte size of an asset's MAIN object only, for ranking duplicates by actual impact
        /// rather than raw file size. For textures this uses the internal <c>TextureUtil.GetStorageMemorySizeLong</c>
        /// — the same value the texture Inspector reports (accounts for the platform's real compressed GPU format +
        /// mips). For other asset types it falls back to <c>Profiler.GetRuntimeMemorySizeLong</c>. Returns 0 when it
        /// can't be determined (caller should fall back to <see cref="FileSizeBytes"/>). Loads the asset, so call
        /// sparingly (ranking only).
        ///
        /// ⚠ MAIN OBJECT ONLY — for a composite asset this is not the asset's memory and is not close to it. A model's
        /// main object is a GameObject (~272 B) while its 45 meshes are sub-assets; a TMP font asset's main object is a
        /// ScriptableObject (~503 B) while its atlas texture is a sub-asset. Measured on one project: a 41.6 MB .fbx
        /// reported 272 B here and 51.5 MB once the sub-assets were counted. Callers that want the asset's real
        /// footprint must use <see cref="AssetMemoryBytes"/>; this one survives for rules that only need a ranking key
        /// and pair it with <c>Math.Max(mem, fileSize)</c> so the undercount cannot bury a large asset.
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

        /// <summary>
        /// An asset's real in-memory footprint: every object the file contributes (main object AND sub-assets), each
        /// measured with the gauge that is correct for its type. Returns 0 when nothing can be determined.
        ///
        /// Why not just sum <c>Profiler.GetRuntimeMemorySizeLong</c> over the sub-assets: in the EDITOR that call
        /// returns roughly twice the runtime figure for meshes and textures, because the editor keeps a CPU copy
        /// alongside the GPU one so assets stay previewable and editable. Measured three ways on one project — a
        /// 45-mesh model read 103.0 MB against 51.5 MB computed from its own vertex layout (ratio 2.001), a second
        /// model 2.039, and a 1024×1024 BC7 texture read 2.7 MB against the 1.3 MB the Inspector shows (which is what
        /// 1 byte/px + mips actually comes to). Shipping the Profiler number would have overstated this project's
        /// Resources footprint by 41%.
        ///
        /// So: textures go through <see cref="TextureStorageMemory"/> (the Inspector's value), meshes through
        /// <see cref="MeshMemoryBytes"/> (computed from the vertex layout the mesh itself declares), and everything
        /// else still through the Profiler — those types are not verified against a second gauge, so a caller
        /// presenting the total must call it an estimate.
        ///
        /// Loads every object in the file. Callers walking many assets must pass each iteration through
        /// <see cref="ThrottleReclaim"/> or the accumulated loads can exhaust graphics memory.
        /// </summary>
        public static long AssetMemoryBytes(string assetPath)
        {
            try
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (objs == null) return 0;
                long sum = 0;
                foreach (var o in objs)
                {
                    if (o == null) continue;
                    if (o is Mesh mesh) { sum += MeshMemoryBytes(mesh); continue; }
                    if (o is Texture tex)
                    {
                        long t = TextureStorageMemory(tex);
                        if (t > 0) { sum += t; continue; }
                    }
                    long rt = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o);
                    if (rt > 0) sum += rt;
                }
                return sum;
            }
            catch { return 0; }
        }

        /// <summary>
        /// A mesh's GPU memory, computed from the vertex layout it declares rather than read from the Profiler
        /// (which doubles in the editor — see <see cref="AssetMemoryBytes"/>): vertex count × the summed stride of
        /// every declared attribute, plus the index buffer at its real index width, plus BlendShape deltas.
        ///
        /// Doubled when <c>isReadable</c> is on, because that is the case where a CPU copy genuinely does persist at
        /// runtime — the same cost <c>PERF.TEX001</c> reports for textures.
        ///
        /// BlendShape deltas are estimated as frames × vertices × 36 B (position, normal and tangent deltas, three
        /// floats each) — Unity exposes no API for their true size, and a mesh storing deltas sparsely will come out
        /// below this. Verified against hand arithmetic only on meshes with no BlendShapes and no Read/Write, so
        /// those two terms are reasoned, not measured.
        /// </summary>
        public static long MeshMemoryBytes(Mesh mesh)
        {
            if (mesh == null) return 0;
            try
            {
                long stride = 0;
                foreach (UnityEngine.Rendering.VertexAttribute attr in Enum.GetValues(typeof(UnityEngine.Rendering.VertexAttribute)))
                {
                    if (!mesh.HasVertexAttribute(attr)) continue;
                    stride += mesh.GetVertexAttributeDimension(attr) * VertexFormatBytes(mesh.GetVertexAttributeFormat(attr));
                }

                long indices = 0;
                for (int i = 0; i < mesh.subMeshCount; i++) indices += (long)mesh.GetIndexCount(i);
                long indexWidth = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 ? 2 : 4;

                long bytes = (long)mesh.vertexCount * stride + indices * indexWidth;
                if (mesh.isReadable) bytes *= 2;   // CPU copy kept alongside the GPU one at runtime

                long blendShapes = 0;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    blendShapes += (long)mesh.GetBlendShapeFrameCount(i) * mesh.vertexCount * 36;

                return bytes + blendShapes;
            }
            catch { return 0; }
        }

        /// <summary>Bytes per component of a vertex attribute format. Unknown formats fall back to 1 byte (the narrowest), keeping the estimate conservative.</summary>
        private static int VertexFormatBytes(UnityEngine.Rendering.VertexAttributeFormat format)
        {
            switch (format)
            {
                case UnityEngine.Rendering.VertexAttributeFormat.Float32:
                case UnityEngine.Rendering.VertexAttributeFormat.UInt32:
                case UnityEngine.Rendering.VertexAttributeFormat.SInt32:
                    return 4;
                case UnityEngine.Rendering.VertexAttributeFormat.Float16:
                case UnityEngine.Rendering.VertexAttributeFormat.UNorm16:
                case UnityEngine.Rendering.VertexAttributeFormat.SNorm16:
                case UnityEngine.Rendering.VertexAttributeFormat.UInt16:
                case UnityEngine.Rendering.VertexAttributeFormat.SInt16:
                    return 2;
                default:
                    return 1;   // UNorm8 / SNorm8 / UInt8 / SInt8
            }
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
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024f * 1024f * 1024f):0.0} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes} B";
        }

        /// <summary>
        /// True when an asset can never reach a player build because of WHERE it lives: any folder named
        /// <c>Editor</c> (Unity's own rule), plus the editor-only special folders. Deleting such a file saves
        /// repository space and exactly zero build bytes, so anything that advertises a build saving has to
        /// exclude it. This is a LOCATION test only — an asset sitting in a shipping folder that nothing
        /// references doesn't ship either, and knowing that needs the reference closure (see UnreferencedAssetScanner).
        /// </summary>
        public static bool IsEditorOnlyPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string norm = assetPath.Replace('\\', '/');
            return norm.Contains("/Editor/")
                || norm.Contains("/Editor Default Resources/")
                || norm.Contains("/Gizmos/");
        }

        // ── Self-exclusion: never diagnose PerfLint's own shipped files ──────────────────────────
        // In the Asset Store install form the whole package lives under Assets/<PerfLint>/, so any scanner
        // that walks Assets/ would otherwise flag PerfLint's own code — e.g. MigrationScanner reporting
        // SceneBatchingAnalyzer's deliberate FindObjectsOfType (kept for 2021.3 compat, #pragma-suppressed).
        // We resolve our install root once from the main asmdef's location and skip anything under it.
        private static string _selfRoot;
        private static bool _selfRootResolved;

        /// <summary>True when <paramref name="assetPath"/> lives inside PerfLint's own install tree (UPM or Asset Store form), so scanners must skip it.</summary>
        public static bool IsPerfLintOwnAsset(string assetPath) => IsUnderRoot(assetPath, SelfRoot());

        /// <summary>
        /// PerfLint's own install root ("Assets/&lt;PerfLint&gt;" in Asset Store form, "Packages/com.perflint.unity" in UPM form),
        /// resolved from the main PerfLint.Editor.asmdef (which sits at &lt;root&gt;/Editor/). Cached; null when it can't be
        /// resolved (then nothing is excluded — same behaviour as before this guard existed).
        /// </summary>
        private static string SelfRoot()
        {
            if (_selfRootResolved) return _selfRoot;
            _selfRootResolved = true;
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid)?.Replace('\\', '/');
                    if (p != null && p.EndsWith("/PerfLint.Editor.asmdef", StringComparison.Ordinal))
                    {
                        string editorDir = Path.GetDirectoryName(p)?.Replace('\\', '/');   // <root>/Editor
                        string root = Path.GetDirectoryName(editorDir)?.Replace('\\', '/'); // <root>
                        _selfRoot = string.IsNullOrEmpty(root) ? null : root;
                        return _selfRoot;
                    }
                }
            }
            catch { _selfRoot = null; }
            return _selfRoot;
        }

        /// <summary>
        /// Pure logic: is <paramref name="assetPath"/> equal to, or nested under, <paramref name="root"/>? Path-separator and
        /// boundary aware ("Assets/PerfLint" does NOT contain "Assets/PerfLintExtras/x.cs"). Empty root → false (exclude nothing).
        /// </summary>
        internal static bool IsUnderRoot(string assetPath, string root)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(root)) return false;
            string p = assetPath.Replace('\\', '/');
            string r = root.Replace('\\', '/').TrimEnd('/');
            if (r.Length == 0) return false;
            return p.Equals(r, StringComparison.Ordinal)
                || p.StartsWith(r + "/", StringComparison.Ordinal);
        }
    }
}
