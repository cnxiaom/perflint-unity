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
    /// Pending entries are stored as a <b>list</b> on disk under Library/ (they survive reloads AND editor restarts):
    /// fixes are applied without compiling, so the user can edit multiple files in a row,
    /// and each must be independently rollback-able from its own backup — a single slot would
    /// lose everything except the last entry.
    ///
    /// Stale-pass guard: a compilation pass only judges entries WRITTEN BEFORE it started — a pass that began
    /// earlier compiled the pre-write content, so its errors (or their absence) say nothing about the fix.
    /// Real case (Viking Village on Unity 6): the original file itself doesn't compile there, so the
    /// post-rollback recompile of the restored original always errors; when the AI Migrate retry loop wrote
    /// round N+1 while that pass was still in flight, the ORIGINAL's errors were attributed to the fresh
    /// write and rolled back a migration that was never compiled — every retry died to its predecessor's
    /// ghost. Entries skipped as stale stay pending; the scheduler's deferred trigger (it waits out
    /// isCompiling) then compiles the real content and delivers a legitimate verdict.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfLintScriptFixVerifier
    {
        // Legacy home of the pending list (each line "assetPath\tbackupPath\twriteTicks"). The list now lives on
        // disk — see StateDir — and this key is read once, so entries in flight when PerfLint updates still get a
        // verdict instead of being stranded. Nothing writes it any more.
        internal const string KPending = "PerfLint.Fix.Pending";

        // UTC ticks of the most recent compilation-pass start; persisted so OnScriptsReloaded (new domain)
        // still knows when the pass that produced the reload began.
        private const string KPassStart = "PerfLint.Fix.PassStart";

        /// <summary>SessionState flag: set after pending-verification fixes survive a domain reload; the window uses this to trigger one full rescan for reconciliation.</summary>
        public const string RescanFlag = "PerfLint.Fix.RescanAfterFix";

        /// <summary>
        /// Fired on the main thread when an AI change (fix or whole-file migration) fails compile verification and is
        /// rolled back: (assetPath, errorSummary). Crucial in a compile-broken project: the rollback happens WITHOUT a
        /// domain reload, and no successful reload will ever come to reconcile the window via RescanFlag — so the open
        /// window must listen and un-show the "already fixed" state itself.
        /// </summary>
        public static event System.Action<string, string> FixRolledBack;

        // ── Pollable outcome record ──────────────────────────────────────────────────────────────────
        //
        // The event above only reaches subscribers alive in this domain, which is exactly what an
        // out-of-process caller is not: a successful compile reloads the domain and cuts the CLI/MCP
        // connection mid-verification (this is why perflint_ai_migrate refuses .cs files outright). So the
        // verdict is also written to SessionState, which survives the reload, and the caller polls for it
        // afterwards instead of holding a connection across it.
        //
        // Kept deliberately small: newest-first, capped, and cleared with the editor session. It is a
        // hand-off channel, not a history — the authoritative state is the file on disk.
        private const string KOutcome = "PerfLint.Fix.Outcome";
        private const int MaxOutcomes = 32;

        /// <summary>The verdict recorded for a verified write.</summary>
        public const string OutcomePassed = "passed";
        /// <summary>The verdict recorded when compile errors caused the file to be restored.</summary>
        public const string OutcomeRolledBack = "rolled_back";

        private static long _passStartTicks;

        /// <summary>
        /// Records a verdict for <paramref name="assetPath"/>, replacing any earlier one for the same file.
        /// Called from both judgement sites so a caller that was disconnected by the domain reload can still
        /// learn what happened.
        ///
        /// Internal rather than private because the shader half of perflint_apply_verified reaches its verdict
        /// synchronously (shader compilation reloads no domain) and records it here too — so a caller that polls
        /// perflint_verify_status out of habit gets the same answer for both file kinds instead of "unknown".
        /// </summary>
        internal static void RecordOutcome(string assetPath, string verdict, string errors)
        {
            var kept = new List<string> { Encode(assetPath, verdict, errors) };
            foreach (var line in SessionState.GetString(KOutcome, "").Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (DecodePath(line) == assetPath) continue;      // superseded by the entry just added
                if (kept.Count >= MaxOutcomes) break;
                kept.Add(line);
            }
            SessionState.SetString(KOutcome, string.Join("\n", kept));
        }

        /// <summary>
        /// The last verdict recorded for <paramref name="assetPath"/>, or a null verdict when the file has no
        /// record in this editor session — which, for a file still in the pending list, means "not judged yet".
        /// </summary>
        public static void ReadOutcome(string assetPath, out string verdict, out string errors)
        {
            verdict = null; errors = null;
            foreach (var line in SessionState.GetString(KOutcome, "").Split('\n'))
            {
                if (string.IsNullOrEmpty(line) || DecodePath(line) != assetPath) continue;
                var parts = line.Split('\t');
                verdict = parts.Length > 1 ? parts[1] : null;
                errors = parts.Length > 2 ? Unescape(parts[2]) : null;
                return;
            }
        }

        /// <summary>Whether <paramref name="assetPath"/> is still awaiting a compile verdict.</summary>
        public static bool IsPending(string assetPath)
        {
            foreach (var (asset, _, _) in Load())
                if (asset == assetPath) return true;
            return false;
        }

        private static string Encode(string path, string verdict, string errors)
            => path + "\t" + verdict + "\t" + Escape(errors ?? "");
        private static string DecodePath(string line)
        {
            int t = line.IndexOf('\t');
            return t < 0 ? line : line.Substring(0, t);
        }
        // Tabs and newlines are the record separators, so they cannot survive raw inside a payload.
        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");
        private static string Unescape(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
            }
            return sb.ToString();
        }

        static PerfLintScriptFixVerifier()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            long.TryParse(SessionState.GetString(KPassStart, "0"), out _passStartTicks);

            // An entry surviving into a NEW editor session was written and never judged — the session it belonged
            // to ended before its verifying compile (see StateDir for why that used to lose the entry entirely).
            // Give it a verdict now, instead of the AI's write silently becoming permanent.
            // Deferred: a static constructor is no place to touch the asset pipeline.
            EditorApplication.delayCall += ReconcileInheritedEntries;
        }

        private static void OnCompilationStarted(object context)
        {
            _passStartTicks = System.DateTime.UtcNow.Ticks;
            SessionState.SetString(KPassStart, _passStartTicks.ToString());
        }

        /// <summary>Register a fix for pending verification (call this before writing the fix to disk).</summary>
        public static void BeginVerify(string assetPath, string backupTempPath)
        {
            var list = Load();
            list.Add((assetPath, backupTempPath, System.DateTime.UtcNow.Ticks));
            Save(list);
        }

        /// <summary>
        /// Whether a compilation pass that started at <paramref name="passStartTicks"/> is stale for an entry
        /// written at <paramref name="writeTicks"/> — i.e. the pass began BEFORE the write, so it compiled the
        /// pre-write content and must not judge (roll back or confirm) the entry. Zero on either side means
        /// "unknown" (legacy entry / no pass-start event seen) and degrades to the old judge-everything behavior.
        /// </summary>
        internal static bool IsStaleForPass(long writeTicks, long passStartTicks)
            => writeTicks != 0 && passStartTicks != 0 && writeTicks > passStartTicks;

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
            var remaining = new List<(string asset, string backup, long writeTicks)>();
            foreach (var (asset, backup, writeTicks) in list)
            {
                // Stale pass: it started before this fix was written, so it compiled the PRE-write content —
                // neither its errors nor their absence judge this entry. Keep pending; the scheduler's deferred
                // trigger (or the caller's next request) compiles the real content.
                if (IsStaleForPass(writeTicks, _passStartTicks))
                {
                    remaining.Add((asset, backup, writeTicks));
                    continue;
                }

                if (!errored.Contains(NormFull(asset)))
                {
                    remaining.Add((asset, backup, writeTicks)); // This file had no errors → keep it pending until a successful reload confirms it
                    continue;
                }

                // Compile failure: restore the backup. No domain reload occurs at this point, so the handler is still alive.
                RollBack(asset, backup, SummarizeErrors(messages, asset));
                rolledBack = true;
            }

            Save(remaining);

            if (rolledBack)
            {
                SessionState.SetBool(RescanFlag, true);          // Let the window reconcile after the post-rollback reload
                CompilationPipeline.RequestScriptCompilation();  // Recompile the restored (clean) code
            }
        }

        /// <summary>
        /// Restore one pending write from its backup and announce the verdict. Shared by the compile-callback path
        /// and the startup reconciliation — the same act, reached two ways; duplicating it once meant one of them
        /// would drift.
        /// </summary>
        private static void RollBack(string asset, string backup, string summary)
        {
            try
            {
                if (File.Exists(backup))
                {
                    File.WriteAllText(Path.GetFullPath(asset), File.ReadAllText(backup));
                    File.Delete(backup);
                }
            }
            catch { /* rollback is best-effort */ }

            // Include the actual compiler errors: without them the user (and we) can't tell WHAT the AI got
            // wrong — the difference between "regenerate", "fix one line by hand" and "give up".
            Debug.LogWarning("[PerfLint] " + L.Tr(
                $"The AI change caused compile errors and was auto-rolled back: {asset}\n{summary}",
                $"AI 修改导致编译错误，已自动回滚：{asset}\n{summary}"));
            AssetDatabase.ImportAsset(asset);
            RecordOutcome(asset, OutcomeRolledBack, summary);
            try { FixRolledBack?.Invoke(asset, summary); }
            catch { /* a subscriber error must never break verification */ }
        }

        /// <summary>
        /// Judge entries inherited from a previous editor session, using errors that are ALREADY known.
        ///
        /// The startup compile happens before this class exists, so its assemblyCompilationFinished never reaches
        /// us — and asking for another one does nothing, because nothing on disk changed since that compile
        /// (AssetDatabase.Refresh is a no-op, so no pass ever runs and the entry stays pending forever; measured
        /// exactly that way on 2026-08-12 with a deliberately broken probe file surviving a restart untouched).
        /// So the verdict is taken from the error set instead of from a pass: the collector's, or — since the
        /// collector missed that compile for the very same reason — the Console's, via the same harvest that
        /// recovers per-file compile findings.
        /// </summary>
        private static void ReconcileInheritedEntries()
        {
            var list = Load();
            if (list.Count == 0) return;

            // Clean startup compile: it DID see these writes (they were on disk before the editor opened), and it
            // succeeded. That is a verdict — the strongest one available. Entries written mid-pass are not covered
            // by it (same stale rule as everywhere else), though at editor start there is no such entry.
            if (!EditorUtility.scriptCompilationFailed)
            {
                var stillPending = new List<(string asset, string backup, long writeTicks)>();
                var passed = new List<string>();
                foreach (var e in list)
                {
                    if (IsStaleForPass(e.writeTicks, _passStartTicks)) { stillPending.Add(e); continue; }
                    try { if (File.Exists(e.backup)) File.Delete(e.backup); } catch { }
                    RecordOutcome(e.asset, OutcomePassed, null);
                    passed.Add(e.asset);
                }
                if (passed.Count > 0) PerfLintPendingRescan.Record(passed);
                Save(stillPending);
                return;
            }

            var errors = Scanners.CompileErrorCollector.SnapshotOrHarvest();
            var errored = new HashSet<string>();
            foreach (var e in errors)
                if (e != null && !string.IsNullOrEmpty(e.file)) errored.Add(NormFull(e.file));

            bool rolledBack = false;
            var remaining = new List<(string asset, string backup, long writeTicks)>();
            foreach (var (asset, backup, writeTicks) in list)
            {
                if (!errored.Contains(NormFull(asset)))
                {
                    // No error names this file — but "not named" is not "clean": the file's assembly may have been
                    // skipped because another one failed first. Keep it pending for a real pass to judge.
                    remaining.Add((asset, backup, writeTicks));
                    continue;
                }
                RollBack(asset, backup, SummarizeCollectedErrors(errors, asset));
                rolledBack = true;
            }

            Save(remaining);
            if (rolledBack)
            {
                SessionState.SetBool(RescanFlag, true);
                CompilationPipeline.RequestScriptCompilation();   // compile the restored content
            }
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            var list = Load();
            if (list.Count == 0) return;

            // A domain load is not by itself proof of a clean compile: the editor loads one at STARTUP too, with
            // whatever assemblies exist, even when the project is broken. Confirming here would delete the backups
            // of entries from the previous session on the strength of a compile that never ran — the exact files
            // most in need of a verdict. The scheduler pass driven from the static constructor judges them properly.
            if (EditorUtility.scriptCompilationFailed) { PerfLintFixCompileScheduler.RequestSoon(); return; }

            // A successful reload means the pass compiled cleanly — but it only vouches for entries written
            // BEFORE that pass started. A fix written mid-pass was never compiled; confirming it here would
            // delete its backup on the strength of a compile that never saw it. Keep those pending instead.
            var confirmed = new List<(string asset, string backup, long writeTicks)>();
            var keep = new List<(string asset, string backup, long writeTicks)>();
            foreach (var e in list)
                (IsStaleForPass(e.writeTicks, _passStartTicks) ? keep : confirmed).Add(e);

            foreach (var (asset, backup, _) in confirmed)
            {
                try { if (File.Exists(backup)) File.Delete(backup); }
                catch { /* ignore */ }
                RecordOutcome(asset, OutcomePassed, null);
            }

            // Record the files that were modified and passed verification, so that after reload
            // the window can incrementally rescan only those files (instead of a full rescan).
            // Their on-disk content now differs from the persisted baseline.
            PerfLintPendingRescan.Record(confirmed.Select(e => e.asset));
            Save(keep);
            if (keep.Count > 0) PerfLintFixCompileScheduler.RequestSoon(); // drive a fresh pass for the unjudged writes

            // Backward-compat path: still set RescanFlag, but the window now primarily uses
            // "restore baseline from disk + incremental rescan of changed files" and no longer
            // forces a full rescan.
            SessionState.SetBool(RescanFlag, true);
            if (confirmed.Count > 0)
                Debug.Log("[PerfLint] " + L.Tr("AI fixes passed compile verification.", "AI 修复已通过编译校验。"));
        }

        // ── Pending-verification list: on disk, deliberately outside the editor session ──
        //
        // This used to live in SessionState with the backups in Temp/ — both wiped when the editor closes. So an
        // AI write whose verifying compile hadn't happened yet became permanent and unjudged the moment the user
        // quit, with its backup gone: the rollback contract silently did not apply across a restart. Not a corner
        // case — PerfLint's own stale-domain notice TELLS the user to restart, and a compile-broken project (the
        // one place whole-file migration is used most) can sit for a long time before any pass judges anything.
        // Library/ is the right home: per-machine, not committed, and not cleared behind our back.
        private const string StateDir = "Library/PerfLint";
        private static string PendingFile => StateDir + "/pending-verify.tsv";

        /// <summary>A fresh backup path for a write about to be verified. Lives beside the pending list, and for
        /// the same reason: a backup the editor deletes on exit cannot roll anything back the next morning.</summary>
        internal static string NewBackupPath()
        {
            Directory.CreateDirectory(Path.GetFullPath(StateDir + "/Backups"));
            return StateDir + "/Backups/PerfLint_backup_" + System.Guid.NewGuid().ToString("N") + ".txt";
        }

        /// <summary>The raw pending-list text — the itest driver asserts on it across compile passes.</summary>
        internal static string PendingRaw()
        {
            try
            {
                string full = Path.GetFullPath(PendingFile);
                return File.Exists(full) ? File.ReadAllText(full) : "";
            }
            catch { return ""; }
        }

        private static List<(string asset, string backup, long writeTicks)> Load()
        {
            var list = new List<(string, string, long)>();
            string raw = PendingRaw();

            // One-time adoption: entries written by a pre-upgrade build of PerfLint are still in SessionState and
            // still deserve a verdict. Read them once; the next Save writes them to disk in the new form.
            if (string.IsNullOrEmpty(raw)) raw = SessionState.GetString(KPending, "");
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (var line in raw.Split('\n'))
            {
                if (line.Length == 0) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                long ticks = 0;
                if (parts.Length >= 3) long.TryParse(parts[2], out ticks); // legacy 2-field line → 0 = "unknown, judge as before"
                list.Add((parts[0], parts[1], ticks));
            }
            return list;
        }

        private static void Save(List<(string asset, string backup, long writeTicks)> list)
        {
            SessionState.EraseString(KPending);   // the file is the only source of truth from here on
            try
            {
                string full = Path.GetFullPath(PendingFile);
                if (list.Count == 0)
                {
                    if (File.Exists(full)) File.Delete(full);
                    return;
                }
                Directory.CreateDirectory(Path.GetFullPath(StateDir));
                var sb = new System.Text.StringBuilder();
                foreach (var (asset, backup, writeTicks) in list)
                    sb.Append(asset).Append('\t').Append(backup).Append('\t').Append(writeTicks).Append('\n');
                File.WriteAllText(full, sb.ToString());
            }
            catch { /* a write failure must not break the compile callback; the entry stays judged in memory */ }
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

        /// <summary>
        /// Same summary, from already-captured errors rather than a live compilation pass — the form the startup
        /// reconciliation has (its pass ran before this class existed). Identical shape on purpose: the user should
        /// not be able to tell which path rolled their file back.
        /// </summary>
        internal static string SummarizeCollectedErrors(IReadOnlyList<Scanners.CollectedError> errors, string assetPath, int max = 8)
        {
            if (errors == null) return "";
            string target = NormFull(assetPath);
            var sb = new System.Text.StringBuilder();
            int total = 0, shown = 0;
            foreach (var e in errors)
            {
                if (e == null || NormFull(e.file) != target) continue;
                total++;
                if (shown >= max) continue;
                if (shown > 0) sb.Append('\n');
                sb.Append("  (").Append(e.line).Append(") ").Append(e.message);
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
