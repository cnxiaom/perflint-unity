using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using PerfLint.Core;
using PerfLint.Licensing;
using PerfLint.Llm;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Ci.Pipeline
{
    /// <summary>
    /// AI Migrate over the wire — the SHADER half only (SHDR004: shaders that fail to compile, so their materials
    /// render magenta). Same recipe, same guards and the same verification as the editor's AI Migrate button: the
    /// model rewrites the file the compiler error points at (usually an included .hlsl), then the shader is
    /// re-imported and ACTIVELY COMPILED, and the file is restored if it doesn't come back clean.
    ///
    /// WHY SHADER ONLY (docs/progress-ledger.md §3-71, probe 2026-07-25). The C# recipes verify by triggering a real
    /// compile, and a compile means a domain reload — which severs this very connection (observed twice: 503 then
    /// 401). Those stay editor-only until they're rebuilt as Unity's start+status pair. Shader rewrites never reload
    /// the domain, so their verification is synchronous and fits in one call.
    ///
    /// SHAPE. The command runs OFF the main thread on purpose. <see cref="LlmClient"/> polls completion on
    /// EditorApplication.update and fires its callback on the main thread, so a MainThreadRequired command that
    /// blocked would stall the very pump that would deliver its answer. Instead the work is posted to the main
    /// thread and this worker blocks until it reports back — measured viable end-to-end before this was written.
    ///
    /// Every outcome is also written to <see cref="MigrateStatusStore"/>, so a call that outlives its transport
    /// (a slow model, an agent that gave up) can still be collected with perflint_ai_migrate_status instead of
    /// leaving the caller unable to tell "still running" from "silently failed".
    /// </summary>
    public static class PerfLintMigrateCommands
    {
        private const string ShaderRule = "SHDR004";

        /// <summary>Upper bound on one call. Generous: a whole-file rewrite plus up to two compile-error-driven
        /// retries is minutes, not seconds. Hitting it doesn't cancel the work — it hands the caller the status
        /// command instead, and the result still lands in the store when it finishes.</summary>
        private static readonly TimeSpan WaitCap = TimeSpan.FromMinutes(6);

        [CliCommand("perflint_ai_migrate",
            "Repair a shader that fails to compile (SHDR004 — its materials render magenta), by rewriting the file its compiler error points at, then re-compiling to verify and rolling the file back if it still fails. Pro; spends one AI credit per attempt. Modifies the open project.",
            MainThreadRequired = false)]
        public static MigrateDto PerfLintAiMigrate(
            [CliArg("path", "The failing .shader asset to repair. Omit when exactly one shader is broken; if several are, the response lists them.")] string path = null,
            [CliArg("dry_run", "Generate and validate the rewrite and report what would change, without writing it. Still spends the AI credit — the model call is the expensive part.")] bool dryRun = false)
        {
            var sw = Stopwatch.StartNew();
            MigrateDto dto = null;
            using (var done = new ManualResetEventSlim(false))
            {
                PipelineMainThread.Post(() =>
                {
                    try
                    {
                        StartOnMainThread(path, dryRun, result =>
                        {
                            // Timed here rather than by the caller: the stored copy has to carry the duration too,
                            // and only this callback is guaranteed to run — the worker may have given up by now.
                            result.elapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                            MigrateStatusStore.Save(result);   // saved even if nobody is still waiting
                            dto = result;
                            done.Set();
                        });
                    }
                    catch (Exception ex)
                    {
                        var failed = Envelope("failed", "AI Migrate threw before it could start: " + ex.Message);
                        MigrateStatusStore.Save(failed);
                        dto = failed;
                        done.Set();
                    }
                });

                // Built by hand rather than through Envelope: we are still on the worker thread here, and
                // Envelope reads the license (EditorPrefs), which is main-thread-only and would throw.
                if (!done.Wait(WaitCap))
                    dto = new MigrateDto
                    {
                        status = "running",
                        ruleId = ShaderRule,
                        message = $"Still running after {WaitCap.TotalMinutes:0} minutes — the work continues in the editor. "
                            + "Poll perflint_ai_migrate_status for the outcome; do not start another migration meanwhile.",
                        privacy = PrivacyLine
                    };
            }

            sw.Stop();
            if (dto.elapsedSeconds <= 0) dto.elapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2); // the "running" branch, which the callback never reached
            return dto;
        }

        // Main thread required: SessionState (and the license read behind Envelope) throw off it. This one is
        // instant anyway — it reads a stored result, it never waits on anything.
        [CliCommand("perflint_ai_migrate_status",
            "The outcome of the last perflint_ai_migrate run, including one that outlived its caller. Free, read-only.")]
        public static MigrateDto PerfLintAiMigrateStatus()
        {
            var last = MigrateStatusStore.Load();
            if (last != null) return last;
            return Envelope("none", "No AI Migrate has run in this editor session.");
        }

        // ── main-thread body ──────────────────────────────────────────────────────────────────────

        private static void StartOnMainThread(string path, bool dryRun, Action<MigrateDto> done)
        {
            var scan = ScanResultStore.Load()?.Result;
            var findings = scan?.Findings;
            if (findings == null || findings.Count == 0)
            {
                done(Envelope("no_scan", "No scan on record — run perflint_scan first, then retry."));
                return;
            }

            // Scope is enforced here, not left to the caller: pointing at a .cs would otherwise reach a recipe whose
            // verification cannot survive this connection (see the class summary).
            if (!string.IsNullOrEmpty(path) && !path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            {
                done(Envelope("editor_only",
                    $"'{path}' isn't a shader. Only shader migrations run over the wire — a C# migration verifies by "
                    + "compiling, and that reloads the domain, which would cut this connection mid-verification. "
                    + "Open Tools > PerfLint in the editor and use AI Migrate on that finding instead."));
                return;
            }

            var broken = findings.Where(f => f != null && f.RuleId == ShaderRule && !string.IsNullOrEmpty(f.TargetPath))
                                 .Select(f => f.TargetPath)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();
            if (broken.Count == 0)
            {
                var otherMigrations = findings.Where(f => f != null && f.Domain == Domain.Migration && MigrateRecipes.ForRule(f.RuleId) != null)
                                              .Select(f => f.RuleId).Distinct().ToArray();
                done(Envelope("not_found", otherMigrations.Length > 0
                    ? $"No failing shaders (SHDR004) in the last scan. {string.Join(", ", otherMigrations)} do have migrations, but those are C# and run in the editor only."
                    : "No failing shaders (SHDR004) in the last scan — nothing for this command to repair."));
                return;
            }

            string target = path;
            if (string.IsNullOrEmpty(target))
            {
                if (broken.Count > 1)
                {
                    done(Envelope("ambiguous",
                        $"{broken.Count} shaders are failing — pass --path to choose one: {string.Join(", ", broken)}. "
                        + "Repairing a shared include often fixes several at once, so re-scan after the first."));
                    return;
                }
                target = broken[0];
            }
            else if (!broken.Any(b => string.Equals(b, target, StringComparison.OrdinalIgnoreCase)))
            {
                done(Envelope("not_found", $"'{target}' isn't among the shaders the last scan found failing: {string.Join(", ", broken)}."));
                return;
            }

            // Gates, in the same order as the editor panel, minus its dialogs (a modal here would hang the caller).
            if (!LicenseService.IsPro)
            {
                var dto = Envelope("pro_required", "AI Migrate needs Pro — activate a license in the editor. Upgrade: " + LicenseSettings.BuyUrl);
                dto.upgradeUrl = LicenseSettings.BuyUrl;
                done(dto);
                return;
            }
            string creditBlock = CreditBlockReason();
            if (creditBlock != null) { done(Envelope("no_credits", creditBlock)); return; }

            var recipe = MigrateRecipes.ForRule(ShaderRule);
            var resolved = MigrateRecipes.Resolve(recipe, target);
            if (recipe == null || resolved == null || string.IsNullOrEmpty(resolved.FilePath))
            {
                done(Envelope("not_found", $"'{target}' is flagged as failing, but no migratable source file could be resolved from its compiler errors."));
                return;
            }

            int lines = MigrateService.FileLineCount(resolved.FilePath);
            if (lines > recipe.MaxLines)
            {
                done(Envelope("too_large",
                    $"{resolved.FilePath} is {lines} lines; whole-file migration is capped at {recipe.MaxLines} because the entire "
                    + "rewritten file has to come back in one completion. Migrate it by hand, or split the file."));
                return;
            }

            MigrateService.Propose(recipe, resolved, p =>
            {
                if (!p.Ok) { done(Shaped("failed", target, resolved.FilePath, "Generating the migration failed: " + p.Error)); return; }
                if (p.NoChange)
                {
                    done(Shaped("no_change", target, resolved.FilePath,
                        "The model judged this file needs no migration — it may already be migrated, or the error lives elsewhere."));
                    return;
                }

                if (dryRun)
                {
                    var dry = Shaped("dry_run", target, p.FilePath,
                        "Dry run — nothing written. The rewrite was generated and passed the recipe's guards; "
                        + "`changes` below is exactly what applying it would do. Re-run without --dry_run to apply and verify.");
                    Describe(dry, p);
                    done(dry);
                    return;
                }

                ShaderMigrateService.ApplyWithVerify(p, (ok, msg) =>
                {
                    var result = Shaped(ok ? "applied" : "rolled_back", target, p.FilePath, msg);
                    result.applied = ok;
                    result.verified = ok;
                    result.rolledBack = !ok;
                    Describe(result, p);
                    if (ok)
                        result.message += " Re-scan (perflint_scan) to refresh the findings — repairing a shared include often clears several shaders at once."
                            + " Review `changes`: compiling proves the rewrite builds, not that it changed only what you asked for.";
                    done(result);
                });
            });
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The dialog-free half of <see cref="Entitlements.RequireAiCredit"/>: same decision, no modal. Returns the
        /// reason to refuse, or null to proceed. Kept in sync with that method by hand — it is the authority.
        /// </summary>
        private static string CreditBlockReason()
        {
            if (LlmSettings.Mode == LlmMode.ByoKey)
                return LicenseService.IsPro ? null
                    : "Using your own API key is a Pro feature. On Free, switch the LLM mode back to the built-in AI service "
                      + "in Tools > PerfLint > LLM to use the daily allowance.";
            return CreditService.HostedExhausted
                ? "Out of AI credits for this period. Upgrade to Pro for a much larger monthly allowance, or add your own "
                  + "API key under Advanced (self-funded, never counted against credits)."
                : null;
        }

        private static int CountLines(string s) => string.IsNullOrEmpty(s) ? 0 : s.Split('\n').Length;

        /// <summary>How many changed lines to return. Enough to read a real migration in full; small enough that a
        /// runaway rewrite can't bury the caller — the honest total is reported either way.</summary>
        private const int MaxReportedChanges = 40;

        /// <summary>
        /// Attach line counts and the actual per-line diff. The diff is the point: the editor shows one and waits
        /// for approval, while over the wire nobody sees the rewrite at all, and a clean compile only proves it
        /// builds. Without this the caller cannot tell a one-token rename from a file the model also tidied up.
        /// </summary>
        private static void Describe(MigrateDto dto, MigrateProposal p)
        {
            dto.originalLines = CountLines(p.Original);
            dto.migratedLines = CountLines(p.Migrated);

            var diff = LineDiff.Compute(p.Original, p.Migrated, MaxReportedChanges);
            dto.changedLines = diff.TotalChanges;
            dto.changes = diff.Changes;
            if (diff.TooLarge)
                dto.message += $" (The file was too large to diff line by line; {dto.originalLines} lines in, {dto.migratedLines} out.)";
            else if (diff.Truncated)
                dto.message += $" (Showing the first {MaxReportedChanges} of {diff.TotalChanges} changed lines.)";
        }

        private const string PrivacyLine =
            "AI Migrate sends the WHOLE target file to the configured model — unlike the scan, which uploads nothing. "
            + "Findings, paths and line numbers are returned to the agent you connected.";

        /// <summary>Main-thread only — it reads the license. Off-thread callers must build the DTO by hand.</summary>
        private static MigrateDto Envelope(string status, string message) => new MigrateDto
        {
            status = status,
            entitled = LicenseService.IsPro,
            ruleId = ShaderRule,
            message = message,
            privacy = PrivacyLine
        };

        private static MigrateDto Shaped(string status, string finding, string file, string message)
        {
            var dto = Envelope(status, message);
            dto.finding = finding;
            dto.file = file;
            return dto;
        }
    }

    /// <summary>
    /// Posts work onto the editor's update tick. A command running off the main thread cannot touch
    /// UnityWebRequest, the AssetDatabase or EditorApplication directly, so everything real happens here.
    /// Top-level on purpose — Unity's [InitializeOnLoad] scan is only dependable for top-level types, and a pump
    /// that silently failed to register would look exactly like a model that never answered.
    /// </summary>
    [InitializeOnLoad]
    internal static class PipelineMainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        static PipelineMainThread() { EditorApplication.update += Drain; }

        public static void Post(Action action) => Queue.Enqueue(action);

        private static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }
    }

    /// <summary>
    /// Last AI Migrate outcome, in SessionState so it survives the domain reloads a repaired project triggers.
    /// Exists so a run that outlives its caller is still collectable rather than simply lost.
    /// </summary>
    internal static class MigrateStatusStore
    {
        private const string Key = "PerfLint.AiMigrate.Last.v1";

        public static void Save(MigrateDto dto)
        {
            try { SessionState.SetString(Key, JsonUtility.ToJson(dto)); } catch { /* status is a convenience, never fail the run for it */ }
        }

        public static MigrateDto Load()
        {
            try
            {
                string json = SessionState.GetString(Key, null);
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<MigrateDto>(json);
            }
            catch { return null; }
        }
    }

    /// <summary>Result of an AI Migrate run over the wire (serialized to JSON by the pipeline server; public fields).</summary>
    [Serializable]
    public sealed class MigrateDto
    {
        /// <summary>applied | rolled_back | dry_run | no_change | failed | running | none · pro_required | no_credits | not_found | ambiguous | editor_only | too_large | no_scan</summary>
        public string status;
        public bool entitled;
        public string ruleId;
        public string finding;      // the failing .shader the finding sits on
        public string file;         // the file actually rewritten — often an included .hlsl, not the .shader
        public bool applied;
        public bool verified;       // the shader compiled clean after the rewrite
        public bool rolledBack;     // verification failed and the file was restored
        public int originalLines;
        public int migratedLines;
        /// <summary>Total differing lines — the real figure, even when <see cref="changes"/> is capped.</summary>
        public int changedLines;
        /// <summary>What the rewrite actually did, line by line. Present on dry_run and on a completed apply, so a
        /// change nobody asked for is visible instead of hidden behind a passing compile.</summary>
        public DiffLine[] changes;
        public double elapsedSeconds;
        public string upgradeUrl;   // set only when status == "pro_required"
        public string message;
        public string privacy;
    }
}
