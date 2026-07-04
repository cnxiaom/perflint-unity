using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;
using UnityEngine;

namespace PerfLint.Llm
{
    /// <summary>
    /// Compile verification and automatic rollback after an AI Fix is applied.
    ///
    /// When applying a fix we do NOT compile immediately (that would trigger a domain reload,
    /// wipe the window state, and force a full rescan); instead, the window does an incremental
    /// rescan of that file right after writing it.
    /// Verification piggybacks on Unity's next natural compilation (focus switch / manual Refresh):
    /// - Compile <b>failure</b> does NOT trigger a domain reload → this class's assemblyCompilationFinished
    ///   subscription is still alive → on detecting a compile error in a pending-verification file,
    ///   it immediately restores the backup and re-triggers compilation.
    /// - Compile <b>success</b> causes a domain reload → [DidReloadScripts] fires → remaining
    ///   pending-verification entries are confirmed, backups cleaned up.
    ///   The reload wipes in-memory window results, so we set RescanFlag here to make the
    ///   window perform one full rescan after reload for reconciliation.
    ///
    /// Pending entries are stored as a <b>list</b> (multiple entries survive reloads via SessionState):
    /// fixes are applied without compiling, so the user can edit multiple files in a row,
    /// and each must be independently rollback-able from its own backup — a single slot would
    /// lose everything except the last entry.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfLintScriptFixVerifier
    {
        // Pending-verification list: each line is "assetPath\tbackupPath".
        private const string KPending = "PerfLint.Fix.Pending";

        /// <summary>SessionState flag: set after pending-verification fixes survive a domain reload; the window uses this to trigger one full rescan for reconciliation.</summary>
        public const string RescanFlag = "PerfLint.Fix.RescanAfterFix";

        /// <summary>
        /// Fired on the main thread when an AI change (fix or whole-file migration) fails compile verification and is
        /// rolled back: (assetPath, errorSummary). Crucial in a compile-broken project: the rollback happens WITHOUT a
        /// domain reload, and no successful reload will ever come to reconcile the window via RescanFlag — so the open
        /// window must listen and un-show the "already fixed" state itself.
        /// </summary>
        public static event System.Action<string, string> FixRolledBack;

        static PerfLintScriptFixVerifier()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        /// <summary>Register a fix for pending verification (call this before writing the fix to disk).</summary>
        public static void BeginVerify(string assetPath, string backupTempPath)
        {
            var list = Load();
            list.Add((assetPath, backupTempPath));
            Save(list);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var list = Load();
            if (list.Count == 0) return;

            // There are pending-verification entries and compilation has finished → a domain reload
            // will follow (on success), or a re-compilation then reload (after rollback).
            // Set RescanFlag BEFORE the reload to avoid a race with the window's CreateGUI execution
            // order (DidReloadScripts may fire later than CreateGUI).
            SessionState.SetBool(RescanFlag, true);

            // Collect files that produced errors in this compilation pass (full path, normalized).
            var errored = new HashSet<string>();
            foreach (var m in messages)
                if (m.type == CompilerMessageType.Error)
                    errored.Add(NormFull(m.file));

            bool rolledBack = false;
            var remaining = new List<(string asset, string backup)>();
            foreach (var (asset, backup) in list)
            {
                if (!errored.Contains(NormFull(asset)))
                {
                    remaining.Add((asset, backup)); // This file had no errors → keep it pending until a successful reload confirms it
                    continue;
                }

                // Compile failure: restore the backup. No domain reload occurs at this point, so the handler is still alive.
                try
                {
                    if (File.Exists(backup))
                    {
                        File.WriteAllText(Path.GetFullPath(asset), File.ReadAllText(backup));
                        File.Delete(backup);
                    }
                }
                catch { /* rollback is best-effort */ }
                rolledBack = true;
                // Include the actual compiler errors: without them the user (and we) can't tell WHAT the AI got
                // wrong — the difference between "regenerate", "fix one line by hand" and "give up".
                string summary = SummarizeErrors(messages, asset);
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"The AI change caused compile errors and was auto-rolled back: {asset}\n{summary}",
                    $"AI 修改导致编译错误，已自动回滚：{asset}\n{summary}"));
                AssetDatabase.ImportAsset(asset);
                try { FixRolledBack?.Invoke(asset, summary); }
                catch { /* a subscriber error must never break verification */ }
            }

            Save(remaining);

            if (rolledBack)
            {
                SessionState.SetBool(RescanFlag, true);          // Let the window reconcile after the post-rollback reload
                CompilationPipeline.RequestScriptCompilation();  // Recompile the restored (clean) code
            }
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            var list = Load();
            if (list.Count == 0) return;

            // A successful reload means the assemblies containing all pending-verification files
            // compiled cleanly → the fixes are valid; clean up the backups.
            foreach (var (_, backup) in list)
            {
                try { if (File.Exists(backup)) File.Delete(backup); }
                catch { /* ignore */ }
            }

            // Record the files that were modified and passed verification, so that after reload
            // the window can incrementally rescan only those files (instead of a full rescan).
            // Their on-disk content now differs from the persisted baseline.
            PerfLintPendingRescan.Record(list.Select(e => e.asset));
            Save(new List<(string, string)>());

            // Backward-compat path: still set RescanFlag, but the window now primarily uses
            // "restore baseline from disk + incremental rescan of changed files" and no longer
            // forces a full rescan.
            SessionState.SetBool(RescanFlag, true);
            Debug.Log("[PerfLint] " + L.Tr("AI fixes passed compile verification.", "AI 修复已通过编译校验。"));
        }

        // ── SessionState read/write for the pending-verification list ──
        private static List<(string asset, string backup)> Load()
        {
            var list = new List<(string, string)>();
            string raw = SessionState.GetString(KPending, "");
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var line in raw.Split('\n'))
            {
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                list.Add((line.Substring(0, tab), line.Substring(tab + 1)));
            }
            return list;
        }

        private static void Save(List<(string asset, string backup)> list)
        {
            if (list.Count == 0) { SessionState.EraseString(KPending); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var (asset, backup) in list)
                sb.Append(asset).Append('\t').Append(backup).Append('\n');
            SessionState.SetString(KPending, sb.ToString());
        }

        /// <summary>
        /// The compiler errors belonging to <paramref name="assetPath"/> as a short indented list ("(line) message"),
        /// capped at <paramref name="max"/> entries with a "+N more" tail. Pure logic (unit-tested) — this string is
        /// what tells the user why their AI change was rolled back.
        /// </summary>
        // Default cap 8, not 3: for whole-file migrations the rollback summary is the primary diagnostic, and
        // hiding errors behind "+N more" cost a smoke-test round (the visible 3 were fixable, the hidden 3 unknown).
        internal static string SummarizeErrors(CompilerMessage[] messages, string assetPath, int max = 8)
        {
            if (messages == null) return "";
            string target = NormFull(assetPath);
            var sb = new System.Text.StringBuilder();
            int total = 0, shown = 0;
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error || NormFull(m.file) != target) continue;
                total++;
                if (shown >= max) continue;
                if (shown > 0) sb.Append('\n');
                sb.Append("  (").Append(m.line).Append(") ").Append(m.message);
                shown++;
            }
            if (total > shown) sb.Append('\n').Append("  … +").Append(total - shown).Append(" more");
            return sb.ToString();
        }

        private static string NormFull(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { return Path.GetFullPath(p).Replace('\\', '/').ToLowerInvariant(); }
            catch { return p.Replace('\\', '/').ToLowerInvariant(); }
        }
    }
}
