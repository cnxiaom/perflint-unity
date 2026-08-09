using System;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Turns a Profiler GC.Alloc callstack into the one thing RUN.GC001 needs: which method allocated, in which file,
    /// on which line.
    ///
    /// This exists because the previous answer to that question was Deep Profile, and Deep Profile answers it at a
    /// price the rest of the flow cannot pay. It needs a recompile, it drops the user out of Play Mode, and it
    /// inflates every timing several-fold — so the sample that localises cannot be the sample that verifies, and a
    /// user chasing one allocation paid for two extra measurement rounds. Callstack recording
    /// (<c>ProfilerDriver.memoryRecordMode</c>) costs none of that: measured under this machine's own drift, and
    /// switchable inside a running Play Mode.
    ///
    /// It is also strictly better information. Marker-name attribution had to guess a file from a type name via
    /// <c>AssetDatabase.FindAssets</c>, which fails whenever the file is not named after the type; a stack line
    /// carries the path and the line number outright.
    ///
    /// A stack line looks like this (Windows/Mono, Unity 6000.3, verified live rather than assumed):
    /// <code>
    /// 0x00000175838d82f3 (Mono JIT Code) UnityEngine.VFX.Utility.VFXMultiplePositionBinder:UpdateTexture ()
    ///     (at ./Library/PackageCache/com.unity.visualeffectgraph@b5d75c68/Runtime/.../VFXMultiplePositionBinder.cs:48)
    /// </code>
    /// Everything above the first managed frame is the profiler's own capture path and the allocator itself
    /// (StackWalker, EmitCallstack, mono_gc_alloc_obj); everything below is the caller chain. The first managed frame
    /// IS the allocation site.
    /// </summary>
    public static class GcCallstackParser
    {
        /// <summary>Where an allocation happened. Null <see cref="AssetPath"/> means the stack named a method we could not map to a file in this project.</summary>
        public readonly struct Site
        {
            /// <summary>Display form, deliberately shaped like the Deep Profile markers this replaces ("Type.Method()") so <c>MethodNameOf</c> keeps working on it.</summary>
            public readonly string Method;
            /// <summary>Unity asset path ("Assets/…" or "Packages/…"), or null when the stack's path is outside the project.</summary>
            public readonly string AssetPath;
            /// <summary>1-based source line, or 0 when the stack carried no line.</summary>
            public readonly int Line;
            public bool IsValid => !string.IsNullOrEmpty(Method);

            public Site(string method, string assetPath, int line)
            {
                Method = method; AssetPath = assetPath; Line = line;
            }
        }

        const string MonoMarker = "(Mono JIT Code)";

        /// <summary>
        /// The innermost managed frame that belongs to THIS PROJECT, or the innermost managed frame of any kind when
        /// none does, or an invalid Site when there are none at all.
        ///
        /// Runtime-invoke wrappers are skipped: they are the marshalling glue between native and managed and name no
        /// code anyone wrote, so treating one as the allocation site would attribute every MonoBehaviour allocation
        /// to the same imaginary place.
        ///
        /// "Belongs to this project" rather than simply "innermost", and the difference is the whole feature. Class
        /// library code is Mono JIT Code too, so allocating a STRING puts `string:Ctor (char*,int,int)` at the
        /// innermost position — a frame with no source file here, which the caller then discards, losing the
        /// allocation entirely. Allocating an OBJECT does not, because mono_object_new_specific has no managed frame
        /// of its own and the user's method is innermost.
        ///
        /// That difference is why this looked like it worked: on urp3dsample the object allocations attributed to a
        /// Visual Effect Graph binder on one run, while the string allocations in the very same scene silently
        /// dropped out, and a later run with different sample ordering attributed nothing at all. Measured with the
        /// frame tree beside the stack — the tree said
        /// "…ScriptRunBehaviourLateUpdate > VFXPropertyBinder.LateUpdate() > GC.Alloc" while the stack said
        /// "string:Ctor", and both were telling the truth about different rungs of the same ladder.
        /// </summary>
        public static Site InnermostManagedFrame(string callstack)
        {
            if (string.IsNullOrEmpty(callstack)) return default;

            // Kept as the fallback so a stack made entirely of library frames still names something, rather than
            // reporting nothing at all.
            Site innermostAny = default;

            foreach (var raw in callstack.Split('\n'))
            {
                int mono = raw.IndexOf(MonoMarker, StringComparison.Ordinal);
                if (mono < 0) continue;
                if (raw.IndexOf("(wrapper ", StringComparison.Ordinal) >= 0) continue;

                string rest = raw.Substring(mono + MonoMarker.Length).Trim();
                if (rest.Length == 0) continue;

                // "Ns.Type:Method (args) (at ./path/File.cs:48)" — the location suffix is optional, and is taken from
                // the LAST "(at " so an argument list containing one cannot be mistaken for it.
                string signature = rest, location = null;
                int at = rest.LastIndexOf("(at ", StringComparison.Ordinal);
                if (at >= 0)
                {
                    signature = rest.Substring(0, at).TrimEnd();
                    location = rest.Substring(at + 4).TrimEnd(')').Trim();
                }

                string method = FormatMethod(signature);
                if (string.IsNullOrEmpty(method)) continue;

                ParseLocation(location, out string assetPath, out int line);
                var site = new Site(method, assetPath, line);

                // The first frame that maps to a file in this project is the answer; anything inside it is class
                // library or engine code the reader cannot open or change.
                if (!string.IsNullOrEmpty(assetPath)) return site;
                if (!innermostAny.IsValid) innermostAny = site;
            }
            return innermostAny;
        }

        /// <summary>
        /// "UnityEngine.VFX.Utility.VFXMultiplePositionBinder:UpdateTexture ()" → "VFXMultiplePositionBinder.UpdateTexture()".
        ///
        /// Namespace and argument list are dropped for the same reason the Deep Profile markers never carried them:
        /// this string is a heading on a card, and "Type.Method()" is what the rest of the code already knows how to
        /// take apart. Compiler-generated coroutine/async/lambda types (Outer+&lt;Method&gt;d__3) collapse to the outer
        /// type, since that is the file the user would open.
        /// </summary>
        internal static string FormatMethod(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return null;

            // Drop the argument list first: it can contain '.' and ':' and would corrupt every split below.
            int paren = signature.IndexOf('(');
            string head = (paren >= 0 ? signature.Substring(0, paren) : signature).Trim();
            if (head.Length == 0) return null;

            // Mono writes Type:Method; a nested generic argument list is already gone with the parens.
            int colon = head.LastIndexOf(':');
            string typeName = colon > 0 ? head.Substring(0, colon) : null;
            string methodName = colon >= 0 ? head.Substring(colon + 1).Trim() : head;
            if (methodName.Length == 0) return null;

            if (string.IsNullOrEmpty(typeName)) return methodName + "()";

            int lastDot = typeName.LastIndexOf('.');
            if (lastDot >= 0) typeName = typeName.Substring(lastDot + 1);
            int plus = typeName.IndexOf('+');
            if (plus > 0) typeName = typeName.Substring(0, plus);
            typeName = typeName.Trim();

            return typeName.Length == 0 ? methodName + "()" : typeName + "." + methodName + "()";
        }

        /// <summary>
        /// "./Assets/Scripts/Foo.cs:12" → ("Assets/Scripts/Foo.cs", 12).
        ///
        /// Package frames arrive as their ON-DISK location, which is not an asset path: Unity resolves a package to
        /// Library/PackageCache/&lt;name&gt;@&lt;hash&gt;/… but addresses it as Packages/&lt;name&gt;/…. Translating
        /// is what lets a package allocation be opened, pinged and named — the alternative is reporting a path the
        /// AssetDatabase has never heard of.
        /// </summary>
        internal static void ParseLocation(string location, out string assetPath, out int line)
        {
            assetPath = null; line = 0;
            if (string.IsNullOrEmpty(location)) return;

            string p = location.Replace('\\', '/').Trim();

            // Trailing ":48" — matched from the right, so a Windows drive letter ("C:/…") cannot be read as a line.
            int colon = p.LastIndexOf(':');
            if (colon > 1 && colon < p.Length - 1 && int.TryParse(p.Substring(colon + 1), out int parsed))
            {
                line = parsed;
                p = p.Substring(0, colon);
            }

            if (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);

            const string cache = "Library/PackageCache/";
            int cacheAt = p.IndexOf(cache, StringComparison.Ordinal);
            if (cacheAt >= 0)
            {
                string tail = p.Substring(cacheAt + cache.Length);          // "com.unity.x@hash/Runtime/A.cs"
                int slash = tail.IndexOf('/');
                if (slash > 0)
                {
                    string folder = tail.Substring(0, slash);               // "com.unity.x@hash"
                    int at = folder.IndexOf('@');
                    string pkg = at > 0 ? folder.Substring(0, at) : folder; // the hash is a local build detail
                    assetPath = "Packages/" + pkg + "/" + tail.Substring(slash + 1);
                }
                return;
            }

            int assets = p.IndexOf("Assets/", StringComparison.Ordinal);
            if (assets >= 0) { assetPath = p.Substring(assets); return; }

            int packages = p.IndexOf("Packages/", StringComparison.Ordinal);
            if (packages >= 0) assetPath = p.Substring(packages);
            // Anything else (engine sources, a path outside the project) stays null: the method name is still worth
            // reporting, but there is nothing here to open.
        }
    }
}
