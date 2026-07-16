using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// One-click executable behind PKG001/PKG002/PKG003: disable unused built-in modules (or remove an unused registry
    /// package — same manifest mechanics) by removing dependency lines from Packages/manifest.json, then let
    /// <see cref="ModuleDisableVerifier"/> compile-verify the result and auto-revert if any script fails to build.
    /// PKG002 removes its whole XR module group in ONE manifest edit + ONE verification (the single pending slot rules
    /// out queueing several disables, and the group's internal dependencies — vr/ar depend on xr — only resolve when
    /// removed together).
    ///
    /// Why a compile-verify + rollback net on top of PKG001's already-strict "no references found" proof: the scanner
    /// proves non-usage against Assets/ scripts + scene/prefab YAML, but its stated blind spots include references in
    /// scripts it could not read and — crucially — scripts under Packages/ (only Assets/ is scanned). A compile-time
    /// reference the scanner missed there would break the build; the verifier catches exactly that and restores manifest.
    ///
    /// The manifest is edited by targeted line removal (not JSON reserialize) to keep the file's formatting and VCS diff
    /// minimal — <see cref="TryRemoveDependency"/> is pure and unit-tested for the first / middle / last / sole-entry cases.
    ///
    /// Dependency gate: a module that another installed package depends on is refused upfront (mirrors Package
    /// Manager's own "required by X" block) — removing its manifest line would be a silent no-op that also strands
    /// the verifier without a compile verdict; see <see cref="FindDependentIn"/>.
    /// </summary>
    internal static class ModuleDisableService
    {
        private const string ManifestRel = "Packages/manifest.json";

        /// <summary>
        /// Remove a built-in module (or registry package) and verify the build still compiles (auto-revert on failure).
        /// Returns immediately after kicking off package re-resolution; the compile verdict is delivered asynchronously
        /// by <see cref="ModuleDisableVerifier"/>.
        /// </summary>
        public static FixResult Disable(string moduleName) => DisableMany(new[] { moduleName });

        /// <summary>
        /// Remove several modules in ONE manifest edit backed by ONE compile verification (used by PKG002's XR group).
        /// The dependency gate exempts dependents that are themselves part of this batch (vr/ar depend on xr — legal
        /// only because they leave together); any OTHER installed dependent aborts the whole batch, nothing partial.
        /// </summary>
        public static FixResult DisableMany(IReadOnlyList<string> moduleNames)
        {
            if (moduleNames == null || moduleNames.Count == 0 || moduleNames.Any(string.IsNullOrEmpty))
                return FixResult.Fail(L.Tr("No module specified.", "未指定模块。"));

            // Baseline-clean requirement: the verifier attributes ANY post-disable compile error to the removal, so the
            // project must currently compile. On a compile-broken project we couldn't tell a new break from a pre-existing
            // one — and a failed compile never reloads the domain, which the confirm path depends on. Refuse, don't guess.
            if (EditorUtility.scriptCompilationFailed)
                return FixResult.Fail(L.Tr(
                    "The project currently has compile errors. Fix them first — otherwise PerfLint can't tell whether disabling this module is what broke the build.",
                    "项目当前有编译错误。请先修好——否则 PerfLint 无法判断禁用该模块是否才是构建失败的原因。"));

            // One disable in flight at a time: each backup is the whole manifest, so overlapping disables would restore
            // each other's stale snapshot on rollback. Serialize them (a disable reloads the domain quickly anyway).
            // TryClearOrphaned first: a pending entry whose verdict can never arrive (no compile was ever triggered)
            // would otherwise block every disable for the rest of the session.
            if (ModuleDisableVerifier.HasPending && !ModuleDisableVerifier.TryClearOrphaned())
                return FixResult.Fail(L.Tr(
                    "Another module disable is still being verified — wait for it to finish (the editor will reload) before disabling another.",
                    "已有一个模块禁用正在校验中——请等它完成（编辑器会重载）后再禁用下一个。"));

            // Dependency gate: Unity keeps a module installed while any other installed package depends on it, so
            // removing its manifest line would be a silent no-op — UPM resolves the module right back in as a
            // dependency, no assembly changes, no recompile, and therefore no compile verdict ever reaches the
            // verifier (the pending entry then blocks every later disable). Package Manager's own UI refuses this
            // case ("Cannot disable: required by X"); mirror it honestly instead of pretending the disable took.
            // Classic instance: Physics 2D is a dependency of Tilemap — Tilemap must go first. Dependents inside
            // this same batch are exempt (they are removed together).
            var batch = new HashSet<string>(moduleNames, StringComparer.Ordinal);
            foreach (var m in moduleNames)
            {
                string dependent = FindInstalledDependent(m, batch);
                if (dependent != null)
                    return FixResult.Fail(L.Tr(
                        $"Cannot disable {m}: {dependent} (installed) depends on it, so Unity would keep it installed anyway. " +
                        $"If {dependent} is itself removable, disable it first — otherwise this module has to stay.",
                        $"无法禁用 {m}：已安装的 {dependent} 依赖它，Unity 会把它作为依赖强制保留（禁了等于没禁）。" +
                        $"若 {dependent} 本身可移除，请先禁用它；否则本模块只能保留。"));
            }

            string manifestFull;
            string manifest;
            try
            {
                manifestFull = Path.GetFullPath(ManifestRel);
                manifest = File.ReadAllText(manifestFull);
            }
            catch (Exception e)
            {
                return FixResult.Fail(L.Tr($"Could not read manifest.json: {e.Message}", $"无法读取 manifest.json：{e.Message}"));
            }

            // Remove every batch entry still present (a stale report may list some already gone); zero hits = stale.
            if (!TryRemoveAll(manifest, moduleNames, out string updated, out var removed)
                || removed.Any(m => PackagesScanner.ModulePresent(updated, m)))
                return FixResult.Fail(L.Tr(
                    $"Could not find {string.Join(", ", moduleNames)} in manifest.json (already removed?).",
                    $"未在 manifest.json 中找到 {string.Join(", ", moduleNames)}（可能已被移除？）。"));
            string removedLabel = string.Join(", ", removed);

            // Back up the ORIGINAL manifest (with the module still present) so rollback re-adds exactly what was there.
            string backup = null;
            try
            {
                backup = FileUtil.GetUniqueTempPathInProject();
                File.WriteAllText(backup, manifest);
            }
            catch { backup = null; /* proceed without a backup file; verifier degrades to a warning-only revert */ }

            // Register the pending verification BEFORE writing, so a reload that races the write still finds the entry.
            ModuleDisableVerifier.BeginVerify(removedLabel, backup);
            try
            {
                File.WriteAllText(manifestFull, updated);
            }
            catch (Exception e)
            {
                ModuleDisableVerifier.ClearPending();
                TryDeleteBackup(backup);
                return FixResult.Fail(L.Tr($"Could not write manifest.json: {e.Message}", $"无法写入 manifest.json：{e.Message}"));
            }

            // Force package re-resolution (which recompiles dependent assemblies and reloads the domain on success).
            try { UnityEditor.PackageManager.Client.Resolve(); } catch { /* best-effort; Refresh below still nudges Unity */ }
            AssetDatabase.Refresh();

            return FixResult.Ok(L.Tr(
                $"Disabling {removedLabel}. PerfLint is compiling to verify — if any script fails to compile it will auto-revert.",
                $"正在禁用 {removedLabel}。PerfLint 正在编译校验——若有脚本编译失败会自动回滚。"));
        }

        /// <summary>
        /// Pure multi-removal core (unit-tested): applies <see cref="TryRemoveDependency"/> for every name, collecting
        /// the ones actually present and removed. Returns false — with <paramref name="result"/> equal to the input —
        /// when none of the names is present.
        /// </summary>
        internal static bool TryRemoveAll(string manifest, IReadOnlyList<string> moduleNames,
            out string result, out List<string> removed)
        {
            result = manifest;
            removed = new List<string>();
            if (string.IsNullOrEmpty(manifest) || moduleNames == null) return false;
            foreach (var m in moduleNames)
                if (TryRemoveDependency(result, m, out result))
                    removed.Add(m);
            return removed.Count > 0;
        }

        private static void TryDeleteBackup(string backup)
        {
            try { if (!string.IsNullOrEmpty(backup) && File.Exists(backup)) File.Delete(backup); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Display name of an installed package that directly depends on <paramref name="moduleName"/>, or null if
        /// none. Reads the same registry Package Manager's own "Cannot disable: required by X" check uses. Packages
        /// named in <paramref name="ignoreDependents"/> (the same removal batch) don't count — they leave together.
        /// Degrades to null (no gate) if the registry is unavailable — the verifier and the orphan backstop still apply.
        /// </summary>
        private static string FindInstalledDependent(string moduleName, ISet<string> ignoreDependents = null)
        {
            var installed = SnapshotInstalledPackages();
            return installed == null ? null : FindDependentIn(installed, moduleName, ignoreDependents);
        }

        /// <summary>
        /// One snapshot of the installed-package registry as (name, displayName, direct dependencies) tuples — the
        /// same data Package Manager's own "required by X" check reads. Null when the registry is unavailable
        /// (callers fail open: no gate / no scan-time suppression; the verifier still protects). Internal so
        /// <see cref="PackagesScanner"/> can take ONE snapshot per scan instead of one registry call per module.
        /// </summary>
        internal static List<(string name, string displayName, string[] dependencies)> SnapshotInstalledPackages()
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Select(p => (p.name,
                                  string.IsNullOrEmpty(p.displayName) ? p.name : p.displayName,
                                  p.dependencies?.Select(d => d.name).ToArray()))
                    .ToList();
            }
            catch { return null; }
        }

        /// <summary>
        /// Pure (unit-tested): the subset of <paramref name="candidates"/> that can actually leave together — i.e.
        /// modules with no installed dependent OUTSIDE the kept set. Iterates to a fixpoint: dropping a module (it has
        /// an external dependent) re-exposes anything only its batch membership was exempting. Used by PKG002 to trim
        /// its XR batch, and the shape behind PKG001's scan-time suppression (advice you cannot execute is noise).
        /// </summary>
        internal static List<string> FilterExternallyFree(
            IEnumerable<(string name, string displayName, string[] dependencies)> installed,
            IEnumerable<string> candidates)
        {
            var kept = new List<string>(candidates);
            bool changed = true;
            while (changed)
            {
                changed = false;
                var keptSet = new HashSet<string>(kept, StringComparer.Ordinal);
                for (int i = kept.Count - 1; i >= 0; i--)
                {
                    if (FindDependentIn(installed, kept[i], keptSet) != null)
                    {
                        kept.RemoveAt(i);
                        changed = true;
                        break; // recompute keptSet before judging the rest
                    }
                }
            }
            return kept;
        }

        /// <summary>Pure core of the dependency gate (unit-tested): first installed package (excluding the module itself and any <paramref name="ignoreDependents"/> batch member) whose direct dependencies include <paramref name="moduleName"/>.</summary>
        internal static string FindDependentIn(
            IEnumerable<(string name, string displayName, string[] dependencies)> installed, string moduleName,
            ISet<string> ignoreDependents = null)
        {
            foreach (var (name, displayName, dependencies) in installed)
            {
                if (name == moduleName || dependencies == null) continue;
                if (ignoreDependents != null && ignoreDependents.Contains(name)) continue;
                if (dependencies.Contains(moduleName)) return displayName;
            }
            return null;
        }

        /// <summary>
        /// Pure logic (unit-tested): remove the <c>"moduleName": "version"</c> dependency entry from manifest JSON while
        /// keeping the result valid JSON and the surrounding formatting intact. Handles the entry being first, middle,
        /// last, or the sole dependency (comma placement differs in each). Returns false — leaving <paramref name="result"/>
        /// equal to the input — when the module isn't present as a dependency entry.
        /// </summary>
        internal static bool TryRemoveDependency(string manifest, string moduleName, out string result)
        {
            result = manifest;
            if (string.IsNullOrEmpty(manifest) || string.IsNullOrEmpty(moduleName)) return false;

            string key = Regex.Escape(moduleName);
            string entry = "\"" + key + "\"[ \\t]*:[ \\t]*\"[^\"]*\"";

            // First/middle entry: has a trailing comma. Consume the line's leading indent + the entry + its comma + newline.
            var withTrailingComma = new Regex("[ \\t]*" + entry + "[ \\t]*,[ \\t]*\\r?\\n?");
            if (withTrailingComma.IsMatch(manifest))
            {
                result = withTrailingComma.Replace(manifest, "", 1);
                return true;
            }

            // Last entry (has siblings): preceded by a comma, no trailing comma. Consume the PRECEDING comma so the new
            // last sibling doesn't keep a dangling separator.
            var withLeadingComma = new Regex(",[ \\t]*\\r?\\n?[ \\t]*" + entry + "[ \\t]*");
            if (withLeadingComma.IsMatch(manifest))
            {
                result = withLeadingComma.Replace(manifest, "", 1);
                return true;
            }

            // Sole entry: no commas on either side. Consume just the line.
            var sole = new Regex("[ \\t]*" + entry + "[ \\t]*\\r?\\n?");
            if (sole.IsMatch(manifest))
            {
                result = sole.Replace(manifest, "", 1);
                return true;
            }

            return false;
        }
    }
}
