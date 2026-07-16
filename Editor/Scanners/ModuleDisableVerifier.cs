using System;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Compile verification + automatic manifest rollback for a PKG001 built-in-module disable (see
    /// <see cref="ModuleDisableService"/>). Mirrors the proven AI-fix verifier lifecycle
    /// (<see cref="PerfLint.Llm.PerfLintScriptFixVerifier"/>) but with two differences that come from editing
    /// manifest.json rather than a C# file:
    ///
    ///  • Error attribution is project-wide, not per-file. Removing a module strips types from the UnityEngine
    ///    assemblies, so a broken reference surfaces in whatever *user script* used the type — never in manifest.json
    ///    itself. Because <see cref="ModuleDisableService"/> refuses to run unless the project already compiles clean,
    ///    the baseline error set is empty, so ANY error in the post-disable pass is attributable to the removal → revert.
    ///  • Rollback restores the whole manifest from a backup snapshot (re-adding the module), then re-resolves packages.
    ///
    /// Lifecycle (identical shape to the AI-fix verifier, which is why it's robust):
    ///  - Compile FAILURE does not reload the domain → this handler is still alive → restore manifest, re-resolve.
    ///  - Compile SUCCESS reloads the domain → <see cref="OnScriptsReloaded"/> confirms and cleans up the backup.
    ///  - The stale-pass guard (writeTicks vs pass-start) prevents a pass that began before the write — which compiled
    ///    the pre-disable content — from judging the entry.
    ///
    /// Single pending slot (not a list): each backup is the entire manifest, so overlapping disables would restore each
    /// other's stale snapshot. <see cref="ModuleDisableService"/> enforces one-at-a-time via <see cref="HasPending"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class ModuleDisableVerifier
    {
        // Pending slot: "moduleName\tbackupPath\twriteTicks". SessionState so it survives the domain reload.
        private const string KPending = "PerfLint.ModuleDisable.Pending";
        private const string KPassStart = "PerfLint.ModuleDisable.PassStart";

        /// <summary>Fired on the main thread when a disable fails compile verification and the manifest is reverted: (moduleName, errorSummary).</summary>
        public static event Action<string, string> DisableRolledBack;

        private static long _passStartTicks;

        static ModuleDisableVerifier()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            long.TryParse(SessionState.GetString(KPassStart, "0"), out _passStartTicks);
        }

        /// <summary>True while a disable is awaiting its compile verdict — <see cref="ModuleDisableService"/> refuses a second disable until this clears.</summary>
        public static bool HasPending => !string.IsNullOrEmpty(SessionState.GetString(KPending, ""));

        /// <summary>Register a disable for pending verification (call before writing manifest.json).</summary>
        public static void BeginVerify(string moduleName, string backupPath)
            => SessionState.SetString(KPending,
                moduleName + "\t" + (backupPath ?? "") + "\t" + DateTime.UtcNow.Ticks.ToString());

        public static void ClearPending() => SessionState.EraseString(KPending);

        /// <summary>Pending entries older than this with no compilation in flight are considered orphaned (their verdict can no longer arrive).</summary>
        private const int OrphanAfterMinutes = 5;

        /// <summary>
        /// Clears a pending entry whose compile verdict can no longer arrive; returns true if one was cleared.
        /// A verdict requires a compilation pass that started after the manifest write — if the disable caused no
        /// assembly change, no pass ever starts and the entry would block every later disable for the whole session
        /// (historic instance: removing a module another installed package depends on is a silent no-op — now blocked
        /// upfront by <see cref="ModuleDisableService"/>'s dependency gate; this is the backstop for any other
        /// no-recompile path). Heuristic: pending is older than <see cref="OrphanAfterMinutes"/> and nothing is
        /// compiling. No manifest restore: nothing compiled, so nothing broke — the backup is just deleted.
        /// </summary>
        public static bool TryClearOrphaned() => TryClearOrphaned(DateTime.UtcNow.Ticks);

        /// <summary>Testable overload — <paramref name="nowTicks"/> stands in for the current UTC time.</summary>
        internal static bool TryClearOrphaned(long nowTicks)
        {
            if (!TryLoad(out string module, out string backup, out long writeTicks)) return false;
            if (EditorApplication.isCompiling) return false;
            if (nowTicks - writeTicks < TimeSpan.FromMinutes(OrphanAfterMinutes).Ticks) return false;
            TryDeleteBackup(backup);
            ClearPending();
            Debug.LogWarning("[PerfLint] " + L.Tr(
                $"The compile verification for disabling {module} never received a verdict (the removal triggered no recompile). Cleared the stale entry — module disables are unblocked.",
                $"禁用 {module} 的编译校验一直未等到判定（该移除未触发任何重新编译）。已清除滞留状态——模块禁用已解除阻塞。"));
            return true;
        }

        private static void OnCompilationStarted(object context)
        {
            _passStartTicks = DateTime.UtcNow.Ticks;
            SessionState.SetString(KPassStart, _passStartTicks.ToString());
        }

        private static bool TryLoad(out string module, out string backup, out long writeTicks)
        {
            module = null; backup = null; writeTicks = 0;
            string raw = SessionState.GetString(KPending, "");
            if (string.IsNullOrEmpty(raw)) return false;
            var parts = raw.Split('\t');
            if (parts.Length < 1 || parts[0].Length == 0) return false;
            module = parts[0];
            backup = parts.Length >= 2 ? parts[1] : "";
            if (parts.Length >= 3) long.TryParse(parts[2], out writeTicks);
            return true;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (!TryLoad(out string module, out string backup, out long writeTicks)) return;

            // Stale pass: it started before the disable was written, so it compiled the pre-disable content and says
            // nothing about the removal. Keep pending; the resolve-driven pass will deliver a real verdict.
            if (PerfLint.Llm.PerfLintScriptFixVerifier.IsStaleForPass(writeTicks, _passStartTicks)) return;

            bool anyError = messages != null && messages.Any(m => m.type == CompilerMessageType.Error);
            if (!anyError) return; // No errors this pass → keep pending; a clean reload confirms it.

            // Compile failure: the removed module was still referenced somewhere → revert the manifest.
            string summary = SummarizeErrors(messages);
            RestoreManifest(backup);
            ClearPending();

            // The report was optimistically saved as "module gone"; the manifest is now back, so mark the report stale
            // and let the post-revert reload prompt a rescan (which re-detects the PKG001 finding).
            PerfLintPendingRescan.SetStale(true);
            SessionState.SetBool(PerfLint.Llm.PerfLintScriptFixVerifier.RescanFlag, true);

            Debug.LogWarning("[PerfLint] " + L.Tr(
                $"Disabling {module} caused compile errors and was auto-reverted (a script still references it):\n{summary}",
                $"禁用 {module} 导致编译错误，已自动回滚（仍有脚本引用它）：\n{summary}"));

            // Re-add the module: re-resolve so the restored manifest recompiles cleanly and reloads the domain.
            try { UnityEditor.PackageManager.Client.Resolve(); } catch { }
            AssetDatabase.Refresh();

            try { DisableRolledBack?.Invoke(module, summary); }
            catch { /* a subscriber error must never break verification */ }
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            if (!TryLoad(out string module, out string backup, out long writeTicks)) return;

            // A clean reload only vouches for a disable written BEFORE the pass that reloaded started. A write made
            // mid-pass wasn't compiled here; keep it pending rather than confirm on a compile that never saw it.
            if (PerfLint.Llm.PerfLintScriptFixVerifier.IsStaleForPass(writeTicks, _passStartTicks)) return;

            // Success: the module is gone and everything still compiled. Clean up. No stale flag — the action already
            // saved the correct "module gone" report before the reload, so the restored report is accurate as-is.
            TryDeleteBackup(backup);
            ClearPending();
            Debug.Log("[PerfLint] " + L.Tr(
                $"Removal verified (project still compiles): {module}",
                $"移除已通过编译校验（项目仍可编译）：{module}"));
        }

        private static void RestoreManifest(string backup)
        {
            try
            {
                if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                    File.WriteAllText(Path.GetFullPath("Packages/manifest.json"), File.ReadAllText(backup));
            }
            catch { /* rollback is best-effort; the warning below still tells the user to re-enable manually */ }
            TryDeleteBackup(backup);
        }

        private static void TryDeleteBackup(string backup)
        {
            try { if (!string.IsNullOrEmpty(backup) && File.Exists(backup)) File.Delete(backup); }
            catch { /* best-effort */ }
        }

        /// <summary>Compact list of the pass's compile errors ("(file:line) message"), capped, for the revert warning.</summary>
        private static string SummarizeErrors(CompilerMessage[] messages, int max = 8)
        {
            if (messages == null) return "";
            var sb = new System.Text.StringBuilder();
            int total = 0, shown = 0;
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error) continue;
                total++;
                if (shown >= max) continue;
                if (shown > 0) sb.Append('\n');
                string file = string.IsNullOrEmpty(m.file) ? "" : Path.GetFileName(m.file.Replace('\\', '/'));
                sb.Append("  (").Append(file).Append(':').Append(m.line).Append(") ").Append(m.message);
                shown++;
            }
            if (total > shown) sb.Append('\n').Append("  … +").Append(total - shown).Append(" more");
            return sb.ToString();
        }
    }
}
