using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Core
{
    /// <summary>
    /// "One-click enable" installer for the Roslyn script-analysis module. Automates the three steps
    /// that would otherwise require manual work:
    ///   1) Copies the bundled Microsoft.CodeAnalysis(.CSharp).dll into the project's Editor-only plugin
    ///      directory, along with pre-authored .meta files that set the correct import options
    ///      (Editor-only, Validate References off) — no per-DLL manual configuration needed;
    ///   2) Adds the scripting define `PERFLINT_ROSLYN`;
    ///   3) Triggers a recompile — after which the PerfLint.Editor.Roslyn assembly is compiled and
    ///      the GC / per-frame allocation / CPU hot-loop rules become active.
    ///
    /// Design note: this class MUST compile and run even when PERFLINT_ROSLYN is not yet defined,
    /// so it lives in the main assembly (Editor/Core), NOT in Editor/Scripting (that entire directory
    /// is gated by defineConstraints and cannot even compile itself when the module is absent).
    /// The DLLs are dormant inside the package at `Editor/Scripting/RoslynDlls~/` — the `~` suffix
    /// makes Unity ignore the directory entirely, preventing accidental compilation or assembly
    /// conflicts when the module is disabled; on install, this class copies them out via plain file IO.
    /// </summary>
    public static class RoslynSetup
    {
        public const string Define = "PERFLINT_ROSLYN";

        // Copy destination: the Editor-only plugin directory inside the project.
        private const string TargetDir = "Assets/Plugins/PerfLintRoslyn";

        // The two core DLLs used to determine whether Roslyn is bundled in the package
        // (without them, one-click install is not possible).
        // Note: the actual copy is NOT limited to these two — every DLL present in `RoslynDlls~`
        // (including the full transitive closure of support dependencies such as
        // System.Collections.Immutable) is copied; see BundledDllFiles() / Install().
        // If a dependency collides with a version already shipped by Unity and causes a
        // "defined in multiple assemblies" error, simply delete the duplicate copy as described
        // in the "Handling dependency conflicts" section of SETUP-ROSLYN.md.
        private static readonly string[] RequiredDlls =
        {
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
        };

        /// <summary>All DLL file names under the package's `RoslynDlls~` directory (including the full support-dependency closure). Returns empty if the directory cannot be located.</summary>
        private static string[] BundledDllFiles()
        {
            string dir = BundledDllDir();
            if (dir == null) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.dll").Select(Path.GetFileName).ToArray();
        }

        /// <summary>Whether the module is fully wired into the project (define + DLLs in place and compilation successful). Equivalent to ScanRunner's deep-analysis availability probe.</summary>
        public static bool IsInstalled => ScanRunner.IsDeepScriptAnalysisAvailable();

        /// <summary>Whether one-click install is possible: the package bundles the DLLs, or the user has already placed them in the project. If neither is true, only the manual/NuGet path is available.</summary>
        public static bool CanOneClickInstall => BundledDllsPresent() || DllsAlreadyInProject();

        /// <summary>Absolute path to the package's `RoslynDlls~` directory (where the dormant bundled DLLs reside). Returns null if it cannot be located.</summary>
        public static string BundledDllDir()
        {
            // Primary path: walk back from this source file's compile-time path to the package root
            // (works for both embedded and read-only packages, without relying on virtual-path resolution).
            try
            {
                string thisFile = ThisFilePath();           // .../Editor/Core/RoslynSetup.cs
                if (!string.IsNullOrEmpty(thisFile))
                {
                    var editorDir = Directory.GetParent(thisFile)?.Parent?.FullName; // .../Editor
                    if (editorDir != null)
                    {
                        string p = Path.Combine(editorDir, "Scripting", "RoslynDlls~");
                        if (Directory.Exists(p)) return p;
                    }
                }
            }
            catch { /* fall through to the virtual-path fallback below */ }

            // Fallback: UPM virtual path.
            try
            {
                string p = Path.GetFullPath("Packages/com.perflint.unity/Editor/Scripting/RoslynDlls~");
                if (Directory.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        private static bool BundledDllsPresent()
        {
            string dir = BundledDllDir();
            return dir != null && RequiredDlls.All(d => File.Exists(Path.Combine(dir, d)));
        }

        private static bool DllsAlreadyInProject() =>
            RequiredDlls.All(d => File.Exists(Path.Combine(TargetDir, d)));

        /// <summary>
        /// One-click install. Returns (ok, message). When ok=false, message contains the failure reason
        /// (missing DLLs, etc.) and the caller should show a dialog directing the user to the manual path.
        /// On success a recompile is triggered; once compilation finishes the fallback notice in the
        /// window disappears automatically.
        /// </summary>
        public static (bool ok, string message, string[] conflicts) Install()
        {
            var noConflicts = Array.Empty<string>();
            try
            {
                string bundle = BundledDllDir();
                bool haveBundle = bundle != null && RequiredDlls.All(d => File.Exists(Path.Combine(bundle, d)));

                if (haveBundle)
                {
                    // Conflict-aware + version-aware install (two-pass: classify first without touching
                    // any files, then execute only after confirming there are no blocking conflicts).
                    // If the project already contains an assembly of the same name elsewhere (many
                    // third-party packages bundle BCL shims such as System.Runtime.CompilerServices.Unsafe,
                    // and Microsoft.CodeAnalysis itself can be introduced by Odin / source generators /
                    // NuGetForUnity), copying our copy on top would trigger
                    // "Multiple precompiled assemblies with the same name".
                    var existing = ProjectDllsOutsideTarget();   // file name -> existing path in project
                    var toCopy = new System.Collections.Generic.List<string>();
                    var skipped = new System.Collections.Generic.List<string>();   // existing version is sufficient — use what's already in the project
                    var blocked = new System.Collections.Generic.List<string>();   // existing version is too old, cannot be resolved automatically (shown in UI)
                    var blockedPaths = new System.Collections.Generic.List<string>(); // project asset paths of blocking conflicts (for UI "locate" action)
                    var resolveTarget = new System.Collections.Generic.List<string>(); // old copies in TargetDir that must be removed

                    foreach (var d in BundledDllFiles())
                    {
                        if (existing.TryGetValue(d, out string existingPath))
                        {
                            resolveTarget.Add(d);   // same name exists elsewhere in the project → our copy must not remain in TargetDir
                            var ev = TryGetAssemblyVersion(existingPath);
                            var ov = TryGetAssemblyVersion(Path.Combine(bundle, d));
                            // Existing version < what we need → cannot overwrite it (it may be holding up other packages),
                            // and cannot add a second copy either → blocking conflict.
                            if (ev != null && ov != null && ev < ov)
                            {
                                string assetPath = ToAssetPath(existingPath);
                                blocked.Add(L.Tr($"{d}: project has {ev}, needs ≥ {ov} ({assetPath})", $"{d}：工程现有 {ev}，需 ≥ {ov}（{assetPath}）"));
                                blockedPaths.Add(assetPath);
                            }
                            else
                                skipped.Add(d);     // existing version ≥ ours (or version unreadable) → use what's in the project
                        }
                        else toCopy.Add(d);
                    }

                    // Blocking conflicts present: do not add the define and do not leave a partially
                    // installed state. First remove any same-named files that may have been mistakenly
                    // copied into TargetDir earlier (restoring a compilable state), then report the
                    // error honestly so the user can resolve it manually; conflicts is passed to the UI
                    // for the "locate" action.
                    if (blocked.Count > 0)
                    {
                        foreach (var d in resolveTarget) RemoveFromTarget(d);
                        AssetDatabase.Refresh();
                        return (false,
                            L.Tr(
                                "Cannot enable in one click: the project already has the following dependencies at versions below what Roslyn requires. Skipping them would cause type-load failures,\n" +
                                "while overwriting them could break the other packages that depend on them. Please upgrade/remove these old versions, or use the NuGet path in SETUP-ROSLYN.md:\n- ",
                                "无法一键启用：工程现有以下依赖版本低于 Roslyn 要求，自动跳过会导致类型加载失败，\n" +
                                "覆盖又可能破坏引入它的其它包。请升级/移除这些旧版本，或改走 SETUP-ROSLYN.md 的 NuGet 路径：\n- ")
                            + string.Join("\n- ", blocked),
                            blockedPaths.ToArray());
                    }

                    Directory.CreateDirectory(TargetDir);
                    foreach (var d in resolveTarget) RemoveFromTarget(d);   // remove old copies to avoid duplicate-assembly conflicts
                    foreach (var d in toCopy)
                    {
                        CopyIfNewer(Path.Combine(bundle, d), Path.Combine(TargetDir, d));
                        // Also copy the pre-authored .meta file (already configured: Editor-only, Validate References off),
                        // eliminating the need to configure each DLL manually. Support DLLs (the System.* closure) ship
                        // without a preset .meta — author an equivalent Editor-only one so they import the same way the
                        // core DLLs do; relying on Unity's default import (Any Platform, Validate References on) can
                        // re-break Roslyn compilation.
                        string meta = d + ".meta";
                        string srcMeta = Path.Combine(bundle, meta);
                        if (File.Exists(srcMeta)) CopyIfNewer(srcMeta, Path.Combine(TargetDir, meta));
                        else WriteEditorOnlyPluginMeta(Path.Combine(TargetDir, meta));
                    }
                    AssetDatabase.Refresh();

                    AddDefine();
                    AssetDatabase.Refresh();

                    string msg =
                        L.Tr(
                            "Roslyn script analysis enabled; recompiling.\n" +
                            "Re-scan after compilation finishes and GC001–004 / UPD001–003 / CPU001 entries will appear.",
                            "Roslyn 脚本分析已启用，正在重新编译。\n" +
                            "编译完成后重新扫描，结果会出现 GC001–004 / UPD001–003 / CPU001 条目。");
                    if (skipped.Count > 0)
                        msg += L.Tr(
                            "\n\nThe following dependencies already exist in the project at sufficient versions and were not copied again (your project's existing versions are kept):\n- ",
                            "\n\n以下依赖工程已存在且版本够用，未重复拷入（保留你工程现有版本）：\n- ")
                            + string.Join("\n- ", skipped);
                    return (true, msg, noConflicts);
                }

                if (!DllsAlreadyInProject())
                {
                    return (false,
                        L.Tr(
                            "This package does not bundle the Roslyn DLLs, so one-click install is unavailable.\n" +
                            "Please follow SETUP-ROSLYN.md to install Microsoft.CodeAnalysis.CSharp via NuGetForUnity, " +
                            "or drop the DLLs in manually and click again.",
                            "本包未内置 Roslyn DLL，无法一键安装。\n" +
                            "请按 SETUP-ROSLYN.md 用 NuGetForUnity 安装 Microsoft.CodeAnalysis.CSharp，" +
                            "或手动放入 DLL 后再点一次。"), noConflicts);
                }

                // The user has already placed the core DLLs in the project; just add the define.
                AddDefine();
                AssetDatabase.Refresh();
                return (true,
                    L.Tr(
                        "Roslyn script analysis enabled; recompiling.\n" +
                        "Re-scan after compilation finishes and GC001–004 / UPD001–003 / CPU001 entries will appear.",
                        "Roslyn 脚本分析已启用，正在重新编译。\n" +
                        "编译完成后重新扫描，结果会出现 GC001–004 / UPD001–003 / CPU001 条目。"), noConflicts);
            }
            catch (Exception e)
            {
                return (false, L.Tr("Install failed: ", "安装失败：") + e.Message, noConflicts);
            }
        }

        /// <summary>Converts an absolute path to a project-relative asset path ("Assets/…"). Returns the original path unchanged if it is not under Assets.</summary>
        private static string ToAssetPath(string fullPath)
        {
            try
            {
                string assetsAbs = Path.GetFullPath("Assets");
                string full = Path.GetFullPath(fullPath);
                if (full.StartsWith(assetsAbs, StringComparison.OrdinalIgnoreCase))
                    return "Assets" + full.Substring(assetsAbs.Length).Replace('\\', '/');
            }
            catch { }
            return fullPath;
        }

        /// <summary>Selects and highlights the given assets in the Project window (used by the UI "locate conflicting DLL" action); falls back to revealing the first path in the OS file manager if loading fails.</summary>
        public static void LocateInProject(string[] assetPaths)
        {
            if (assetPaths == null || assetPaths.Length == 0) return;
            var objs = assetPaths
                .Select(p => AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p))
                .Where(o => o != null).ToArray();
            if (objs.Length > 0)
            {
                Selection.objects = objs;
                EditorGUIUtility.PingObject(objs[0]);
            }
            else
            {
                // Load fails when the asset has not been successfully imported; fall back to
                // revealing the first path in the OS file manager.
                try { EditorUtility.RevealInFinder(Path.GetFullPath(assetPaths[0])); } catch { }
            }
        }

        /// <summary>DLLs already present under the project's Assets folder (excluding TargetDir itself): file name → first matching path. Used for conflict and version detection.</summary>
        private static System.Collections.Generic.Dictionary<string, string> ProjectDllsOutsideTarget()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string assets = Path.GetFullPath("Assets");
                if (!Directory.Exists(assets)) return map;
                string target = Path.GetFullPath(TargetDir);
                foreach (var f in Directory.GetFiles(assets, "*.dll", SearchOption.AllDirectories))
                {
                    if (Path.GetFullPath(f).StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue;
                    // Skip DLLs inside a Unity-ignored folder (any path segment ending in '~', e.g. the package's
                    // dormant Editor/Scripting/RoslynDlls~). Under an Asset Store install the package lives under
                    // Assets/, so those bundled DLLs are present on disk but invisible to Unity — counting them as
                    // "already in the project" would make Install skip copying the real DLLs, leaving PERFLINT_ROSLYN
                    // defined with no usable assemblies (CS0246 on every Roslyn type).
                    if (IsInsideHiddenFolder(f)) continue;
                    string name = Path.GetFileName(f);
                    if (!map.ContainsKey(name)) map[name] = f;
                }
            }
            catch { /* if the scan fails, treat it as no conflicts and proceed with copying */ }
            return map;
        }

        /// <summary>Reads the assembly version of a DLL without loading it into the AppDomain. Returns null if the version cannot be read (corrupt file or unmanaged DLL).</summary>
        private static Version TryGetAssemblyVersion(string path)
        {
            try { return System.Reflection.AssemblyName.GetAssemblyName(path).Version; }
            catch { return null; }
        }

        /// <summary>Removes a DLL and its .meta file (if present) from TargetDir — used to clean up previously mistakenly copied dependencies that now conflict with the project.</summary>
        private static void RemoveFromTarget(string dll)
        {
            try
            {
                string p = Path.Combine(TargetDir, dll);
                if (File.Exists(p)) File.Delete(p);
                string meta = p + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
            }
            catch { /* if deletion fails, leave it — worst case a duplicate-assembly conflict is reported and the user can delete manually */ }
        }

        /// <summary>Temporary disable: removes the define (DLLs are kept in the project for easy re-enable).</summary>
        public static void Uninstall()
        {
            RemoveDefine();
            AssetDatabase.Refresh();
        }

        private static void CopyIfNewer(string src, string dst)
        {
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(src)) return;
            File.Copy(src, dst, overwrite: true);
        }

        /// <summary>True if any directory segment of <paramref name="path"/> ends with '~' — a folder Unity ignores entirely (e.g. the package's dormant RoslynDlls~). Such files exist on disk but are invisible to the editor. Internal for regression testing.</summary>
        internal static bool IsInsideHiddenFolder(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;
            foreach (var seg in dir.Replace('\\', '/').Split('/'))
                if (seg.EndsWith("~", StringComparison.Ordinal)) return true;
            return false;
        }

        // Editor-only managed-plugin .meta (Any disabled, Editor enabled, Validate References off) — mirrors the
        // pre-authored .meta shipped for the core Roslyn DLLs and tests/roslyn-plugin-meta.template. {0} = GUID.
        private const string EditorOnlyPluginMetaTemplate =
            "fileFormatVersion: 2\n" +
            "guid: {0}\n" +
            "PluginImporter:\n" +
            "  externalObjects: {{}}\n" +
            "  serializedVersion: 2\n" +
            "  iconMap: {{}}\n" +
            "  executionOrder: {{}}\n" +
            "  defineConstraints: []\n" +
            "  isPreloaded: 0\n" +
            "  isOverridable: 0\n" +
            "  isExplicitlyReferenced: 0\n" +
            "  validateReferences: 0\n" +
            "  platformData:\n" +
            "  - first:\n" +
            "      Any:\n" +
            "    second:\n" +
            "      enabled: 0\n" +
            "      settings: {{}}\n" +
            "  - first:\n" +
            "      Editor: Editor\n" +
            "    second:\n" +
            "      enabled: 1\n" +
            "      settings:\n" +
            "        DefaultValueInitialized: true\n" +
            "  userData:\n" +
            "  assetBundleName:\n" +
            "  assetBundleVariant:\n";

        /// <summary>Writes an Editor-only plugin .meta for a copied support DLL that ships without one (the System.* closure). No-op if a .meta already exists. GUID is derived deterministically from the file name so re-running install is stable.</summary>
        private static void WriteEditorOnlyPluginMeta(string metaPath)
        {
            if (File.Exists(metaPath)) return;
            string seed = Path.GetFileName(metaPath);   // e.g. System.Collections.Immutable.dll.meta
            File.WriteAllText(metaPath, string.Format(EditorOnlyPluginMetaTemplate, DeterministicGuid(seed)));
        }

        /// <summary>32-hex-char GUID derived deterministically from <paramref name="seed"/> (stable across runs; avoids Date/random).</summary>
        private static string DeterministicGuid(string seed)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("PerfLintRoslyn:" + seed));
                var sb = new System.Text.StringBuilder(32);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ── Scripting Define operations (targeting the currently active Build Target Group) ──────────
        private static BuildTargetGroup ActiveGroup =>
            BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);

        public static bool HasDefine()
        {
            var defs = PlayerSettings.GetScriptingDefineSymbolsForGroup(ActiveGroup)
                .Split(';').Select(s => s.Trim());
            return defs.Contains(Define);
        }

        private static void AddDefine()
        {
            var group = ActiveGroup;
            var defs = PlayerSettings.GetScriptingDefineSymbolsForGroup(group)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (!defs.Contains(Define))
            {
                defs.Add(Define);
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", defs));
            }
        }

        private static void RemoveDefine()
        {
            var group = ActiveGroup;
            var defs = PlayerSettings.GetScriptingDefineSymbolsForGroup(group)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0 && s != Define).ToList();
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", defs));
        }

        private static string ThisFilePath([CallerFilePath] string path = null) => path;
    }
}
