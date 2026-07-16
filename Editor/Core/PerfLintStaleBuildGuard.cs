using System;
using System.IO;
using UnityEditor;

namespace PerfLint.Core
{
    /// <summary>
    /// Detects the "this domain no longer matches the disk" trap: when a project has C# compile errors, Unity
    /// NEVER reloads the domain — so anything updated on disk keeps running as its pre-update self, with no
    /// indication anywhere. Two real costs so far (both Viking Village on 6000.5):
    ///  · 2026-07-11: an updated PerfLint build never loaded — a whole verification round re-tested old code.
    ///  · 2026-07-12: URP was upgraded 10.8→17.5 mid-session; the domain kept the OLD URP loaded while the
    ///    compiler verified against the NEW one — so AI Migrate's reflection probe injected confidently-wrong
    ///    "authoritative" API facts and three retry rounds were burned on the wrong pass shape.
    /// The predicate is deliberately narrow — change newer than this domain's load time AND compilation
    /// currently failed — so it can't false-positive on healthy projects, where Unity reloads by itself.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfLintStaleBuildGuard
    {
        // When this domain was loaded. A successful compile always reloads the domain and resets this; only a
        // compile-broken project can keep it old.
        private static readonly DateTime LoadedAtUtc = DateTime.UtcNow;

        static PerfLintStaleBuildGuard() { }

        /// <summary>
        /// Whether the RUNNING PerfLint build is older than the package source on disk while the project's
        /// compile errors prevent Unity from ever loading the update. Cheap enough for a focus-time check:
        /// the directory walk only happens in the (rare) compilation-failed state.
        /// </summary>
        public static bool IsRunningBuildStale()
        {
            try
            {
                if (!EditorUtility.scriptCompilationFailed) return false; // healthy projects reload on their own

                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PerfLintStaleBuildGuard).Assembly);
                string dir = pkg?.resolvedPath;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false; // non-package install → no reliable source root

                DateTime newest = DateTime.MinValue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) newest = t;
                }
                return IsStaleCore(newest, LoadedAtUtc, compilationFailed: true);
            }
            catch { return false; } // a diagnostics banner must never break the window
        }

        /// <summary>
        /// Whether the project's PACKAGE STATE changed after this domain loaded, while compile errors prevent
        /// the reload that would pick it up. In that window the loaded assemblies (and everything reflection
        /// says about them) describe the OLD packages, while the compiler verifies against the NEW ones — the
        /// exact split that poisoned AI Migrate's URP probe. Watches the two files every UPM change touches:
        /// Packages/manifest.json and Packages/packages-lock.json.
        /// </summary>
        public static bool IsPackageStateStale()
        {
            try
            {
                if (!EditorUtility.scriptCompilationFailed) return false;

                string root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Packages"));
                DateTime newest = DateTime.MinValue;
                foreach (var name in new[] { "manifest.json", "packages-lock.json" })
                {
                    string p = Path.Combine(root, name);
                    if (!File.Exists(p)) continue;
                    var t = File.GetLastWriteTimeUtc(p);
                    if (t > newest) newest = t;
                }
                return IsStaleCore(newest, LoadedAtUtc, compilationFailed: true);
            }
            catch { return false; }
        }

        /// <summary>
        /// The domain is stale w.r.t. the disk in any way that matters: PerfLint's own build, or the project's
        /// package set. UI banner and the AI Migrate gate both key off this.
        /// </summary>
        public static bool IsDomainStale() => IsRunningBuildStale() || IsPackageStateStale();

        /// <summary>Pure predicate (unit-tested): stale = compilation failed AND the change landed after this domain was loaded.</summary>
        internal static bool IsStaleCore(DateTime newestSourceUtc, DateTime loadedAtUtc, bool compilationFailed)
            => compilationFailed && newestSourceUtc > loadedAtUtc;
    }
}
