using System;
using System.Collections.Generic;
using System.IO;
using PerfLint.Llm;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace PerfLint.Ci.Pipeline
{
    /// <summary>
    /// Apply an agent-authored edit under compile verification, with automatic rollback. C# and shaders.
    ///
    /// This is the half of "AI fix" that an outside agent cannot do for itself. It already has a model, it can
    /// read the finding (perflint_list_findings gives file and line), and it can write the file. What it cannot
    /// do is find out, safely, whether its edit broke the project: judging that means crossing a real compile,
    /// and on success a compile reloads the domain — which kills the connection the agent is asking over.
    /// That is exactly why <c>perflint_ai_migrate</c> refuses .cs files outright.
    ///
    /// Because the intelligence comes from the caller's own agent, nothing here spends an AI credit or reaches an
    /// LLM: the whole command is file I/O plus the compilers Unity already runs. It is therefore FREE on both
    /// tiers — metering a call that costs us nothing, while the in-editor AI Fix we DO pay for is free, would have
    /// had the gate backwards. Pro's line stays where the licence panel already draws it: applying fixes at
    /// project scale.
    ///
    /// SHADERS TAKE THE SHORT PATH. Shader compilation does not reload the domain, so there is no connection to
    /// lose: <see cref="ApplyShader"/> writes, re-imports, actively compiles the shader's passes, restores the
    /// original on failure, and returns the final verdict on the same call — no polling. It runs the very same
    /// <see cref="ShaderMigrateService.WriteVerifyOrRollback"/> the in-editor AI Migrate uses, so an
    /// agent-authored shader edit is held to exactly the bar an LLM-authored one is.
    ///
    /// For C# the way around the reload is not to hold the connection across it at all:
    ///   1. <see cref="PerfLintApplyVerified"/> backs the file up, registers it with
    ///      <see cref="PerfLintScriptFixVerifier"/>, writes, asks for a compile, and returns IMMEDIATELY.
    ///   2. The domain reloads (or doesn't, if compilation failed). Either way the verifier survives it —
    ///      its pending list and its verdicts live in SessionState — and records the outcome.
    ///   3. <see cref="PerfLintVerifyStatus"/> reads that verdict afterwards, over a fresh connection.
    ///
    /// The backup is taken by THIS command rather than by the caller, because the verifier's contract is that
    /// the backup exists before the write does. An agent that edited the file first and then asked us to verify
    /// would have already destroyed the only copy worth restoring.
    ///
    /// The edit itself arrives either as old_text/new_text — located with the same tolerant matcher the
    /// in-editor AI Fix uses (<see cref="ScriptFixService"/>), and physically incapable of truncating the
    /// file — or as whole-file content for genuine rewrites.
    /// </summary>
    public static class PerfLintVerifyCommands
    {
        /// <summary>Include files that cannot be compiled on their own — they need a .shader to verify against.</summary>
        private static readonly string[] ShaderIncludeExts = { ".hlsl", ".cginc", ".glslinc" };

        [CliCommand("perflint_apply_verified",
            "Edit a C# or shader file under compile verification: PerfLint backs the file up first, applies your edit, and compiles it; if the edit fails to compile, the file is restored byte-for-byte. Two edit modes: old_text/new_text replaces one region (preferred — it cannot truncate the file), content/content_file rewrites the whole file. Read the file before editing, and keep edits minimal. Compiling clean proves the edit compiles, not that it is correct — if you cannot produce a fix you are confident in, say so instead of writing a guess. C# (.cs) returns 'verifying' immediately, because a successful compile reloads the domain and would cut this connection — poll perflint_verify_status for the verdict. Shaders (.shader/.hlsl/.cginc) are verified by re-importing and actively compiling the shader's passes, which does NOT reload the domain — so THIS call returns the final verdict (passed / rolled_back). Free on both tiers — the model is yours, PerfLint only verifies; no AI credit is spent. Modifies the open project.")]
        public static VerifyDto PerfLintApplyVerified(
            [CliArg("path", "The file to edit, as a project-relative path (Assets/... or Packages/...). C# (.cs) or shader family (.shader/.hlsl/.cginc).")] string path = null,
            [CliArg("old_text", "The exact lines to replace, as they appear in the file. Matched whole-line, ignoring leading/trailing whitespace per line — so the snippet must start and end at line boundaries. Pair with new_text.")] string oldText = null,
            [CliArg("new_text", "The replacement for old_text. An empty string deletes the old_text lines.")] string newText = null,
            [CliArg("line", "1-based line number to disambiguate old_text when it occurs more than once — the occurrence nearest this line is edited (use the finding's line). Optional when the snippet is unique.")] int line = 0,
            [CliArg("content", "Whole-file rewrite mode: the complete new contents. Prefer old_text/new_text for spot edits — partial content here would truncate the file.")] string content = null,
            [CliArg("content_file", "Read the whole-file contents from this file instead of the content argument. Use when the file is large enough to be awkward to inline.")] string contentFile = null,
            [CliArg("verify_path", "The .shader asset whose compilation proves the edit works. Required when editing an .hlsl/.cginc include — an include cannot be compiled on its own. For a SHDR004 finding this is the finding's path. Ignored for .cs.")] string verifyPath = null)
        {
            if (string.IsNullOrEmpty(path))
                return Fail("bad_request", "path is required — the project-relative file to edit.");

            bool isCs = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            bool isShaderAsset = path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase);
            bool isShaderInclude = false;
            foreach (var ext in ShaderIncludeExts)
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { isShaderInclude = true; break; }

            if (!isCs && !isShaderAsset && !isShaderInclude)
                return Fail("bad_request", $"'{path}' is not a file this command can verify. It verifies by compiling: "
                                         + "C# (.cs), or the shader family (.shader/.hlsl/.cginc).");

            // An include is only text until a shader pulls it in — without one there is nothing to compile, and
            // reporting "passed" off an unverified write would be a lie the caller cannot detect.
            if (isShaderInclude && string.IsNullOrEmpty(verifyPath))
                return Fail("bad_request", $"'{path}' is an include and cannot be compiled on its own. Pass verify_path "
                                         + "— the .shader that includes it (for a SHDR004 finding, the finding's own path).");
            if (isShaderAsset && string.IsNullOrEmpty(verifyPath)) verifyPath = path;
            if (verifyPath != null && !verifyPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                return Fail("bad_request", $"verify_path must be a .shader asset; got '{verifyPath}'.");

            bool editMode = oldText != null;
            if (editMode && (content != null || !string.IsNullOrEmpty(contentFile)))
                return Fail("bad_request", "Pass either old_text/new_text or content/content_file, not both.");
            if (editMode && oldText.Length == 0)
                return Fail("bad_request", "old_text is empty. Pass the lines to replace; for a full rewrite use content.");
            if (editMode && newText == null)
                return Fail("bad_request", "new_text is required with old_text — the replacement lines (an empty string deletes the old_text lines).");

            if (!editMode)
            {
                if (content == null && !string.IsNullOrEmpty(contentFile))
                {
                    try { content = File.ReadAllText(contentFile); }
                    catch (Exception ex) { return Fail("bad_request", "Could not read content_file: " + ex.Message); }
                }
                if (content == null)
                    return Fail("bad_request", "Provide the edit via old_text/new_text (preferred), or the complete new file via content/content_file.");
                if (content.Length == 0)
                    return Fail("bad_request", "Refusing to write an empty file — pass the complete new contents, not a diff.");
            }

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { return Fail("bad_request", "Unusable path: " + ex.Message); }
            if (!File.Exists(full))
                return Fail("not_found", $"No such file: {path}");

            // Refuse while a compile is already in flight. Writing now would produce an entry the running pass
            // cannot judge (it compiled the pre-write content), and the verifier would correctly hold it as
            // stale — leaving the caller polling a verdict that only arrives after another pass. Better to say so.
            if (EditorApplication.isCompiling)
                return Fail("busy", "A compilation is already running. Wait for it to finish, then retry — a write "
                                  + "landing mid-pass cannot be judged by that pass.");

            string current;
            try { current = File.ReadAllText(full); }
            catch (Exception ex) { return Fail("failed", "Could not read the file: " + ex.Message); }

            int editedAtLine = 0;
            if (editMode)
            {
                // Same tolerant matcher the in-editor AI Fix uses (ScriptFixService): whole lines, per-line
                // whitespace and CRLF/LF differences ignored, nearest-to-line anchoring for duplicates.
                int matches = ScriptFixService.CountRegionMatches(current, oldText);
                if (matches == 0)
                    return Fail("no_match", "old_text was not found in " + path + ". It matches whole lines "
                                          + "(leading/trailing whitespace per line is ignored) — re-read the file and "
                                          + "pass the lines exactly as they are. Nothing was written.");
                if (matches > 1 && line <= 0)
                    return Fail("ambiguous", "old_text occurs " + matches + " times in " + path + " — pass line "
                                           + "(e.g. the finding's line) to edit the occurrence nearest it. Nothing was written.");

                var (at, len) = ScriptFixService.LocateRegion(current, oldText, line);
                if (len <= 0)
                    return Fail("no_match", "old_text could not be located in " + path + ". Nothing was written.");

                editedAtLine = 1;
                for (int i = 0; i < at; i++) if (current[i] == '\n') editedAtLine++;

                content = current.Substring(0, at)
                        + ScriptFixService.AdaptLineEndings(newText, current)
                        + current.Substring(at + len);
            }

            if (isShaderAsset || isShaderInclude)
                return ApplyShader(path, full, current, content, verifyPath, editMode, editedAtLine);

            string backup;
            try
            {
                backup = Path.Combine(Path.GetTempPath(), "perflint-verify-" + Guid.NewGuid().ToString("N") + ".bak");
                File.Copy(full, backup, overwrite: true);
            }
            catch (Exception ex) { return Fail("failed", "Could not take a backup, so nothing was written: " + ex.Message); }

            // Registered BEFORE the write, which is the verifier's contract: the entry is what makes the file
            // restorable, and a crash between write and register would leave a broken file with no way back.
            PerfLintScriptFixVerifier.BeginVerify(path, backup);

            try { File.WriteAllText(full, content); }
            catch (Exception ex)
            {
                try { File.Copy(backup, full, overwrite: true); File.Delete(backup); } catch { /* best effort */ }
                return Fail("failed", "Write failed and the original was restored: " + ex.Message);
            }

            AssetDatabase.ImportAsset(path);

            // Prefer the debounced scheduler: an agent applying several edits in a row should get ONE compile,
            // not one domain reload per file. When the user has turned auto-verification off the scheduler goes
            // silent by design, so drive the pass directly rather than leave the write unjudged forever.
            if (LlmSettings.AutoVerifyFix) PerfLintFixCompileScheduler.RequestSoon();
            else { AssetDatabase.Refresh(); CompilationPipeline.RequestScriptCompilation(); }

            return new VerifyDto
            {
                status = "verifying",
                path = path,
               
                message = (editMode ? "Replaced the old_text region at line " + editedAtLine : "Written")
                        + " and queued for compile verification. Poll perflint_verify_status --path "
                        + path + " for the verdict; if it fails to compile the file is restored automatically.",
            };
        }

        /// <summary>
        /// The shader half: write → re-import → actively compile the shader's passes → restore on failure, all
        /// synchronous because shader compilation does not reload the domain. The verdict therefore comes back on
        /// THIS call instead of through the poll channel the C# path needs.
        ///
        /// The write/verify/rollback itself is <see cref="ShaderMigrateService.WriteVerifyOrRollback"/> — the same
        /// code the in-editor AI Migrate runs, so an agent-authored edit is held to exactly the same bar as an
        /// LLM-authored one. The outcome is also recorded for perflint_verify_status, so a caller that polls out of
        /// habit gets the same answer rather than "unknown".
        /// </summary>
        private static VerifyDto ApplyShader(string path, string full, string current, string content,
                                             string verifyPath, bool editMode, int editedAtLine)
        {
            if (AssetDatabase.LoadAssetAtPath<Shader>(verifyPath) == null)
                return Fail("not_found", $"verify_path '{verifyPath}' does not load as a shader asset — nothing was written.");

            var proposal = new MigrateProposal
            {
                FilePath = path,
                VerifyAssetPath = verifyPath,
                Original = current.Replace("\r\n", "\n").Replace("\r", "\n"),
                Migrated = content,
            };

            var outcome = ShaderMigrateService.WriteVerifyOrRollback(proposal, current, full);
            string where = editMode ? "Replaced the old_text region at line " + editedAtLine : "Rewrote the file";

            if (outcome.WriteFailure != null)
                return Fail("failed", "Nothing was written: " + outcome.WriteFailure);

            if (outcome.RollbackFailure != null)
                return new VerifyDto
                {
                    status = "failed", path = path, errors = outcome.Errors,
                    message = "The shader failed to compile AND restoring the original failed — this file is left in a "
                            + "broken state, restore it from version control: " + outcome.RollbackFailure,
                };

            if (outcome.RolledBack)
            {
                PerfLintScriptFixVerifier.RecordOutcome(path, PerfLintScriptFixVerifier.OutcomeRolledBack, outcome.Errors);
                return new VerifyDto
                {
                    status = "rolled_back", path = path, rolledBack = true, errors = outcome.Errors,
                    message = "The shader did not compile and the original file was restored. The compiler errors are "
                            + "in 'errors' — fix them and call perflint_apply_verified again.",
                };
            }

            PerfLintScriptFixVerifier.RecordOutcome(path, PerfLintScriptFixVerifier.OutcomePassed, outcome.Errors);

            // "Its own errors are gone, but compiling further exposed the next layer elsewhere" is a pass for THIS
            // file — say so plainly and name the next target, rather than reporting a bare success the caller would
            // read as "the shader is clean now".
            if (outcome.Progressed)
                return new VerifyDto
                {
                    status = "passed", path = path, verified = true, errors = outcome.Errors,
                    message = where + ". This file's own errors are gone — but compiling further revealed errors in "
                            + "OTHER files (see 'errors'). The write was kept; target the newly-erroring file next.",
                };

            return new VerifyDto
            {
                status = "passed", path = path, verified = true,
                message = where + " and " + verifyPath + " now compiles (verified by actively compiling its passes). "
                        + (outcome.Notice ?? "Compiling clean is not the same as rendering correctly — check the visual "
                                           + "result in the scene. If the view looks unlit afterwards, reopen the scene; "
                                           + "that is an editor quirk after shader recompiles, not damage."),
            };
        }

        [CliCommand("perflint_verify_status",
            "The compile verdict for a file written by perflint_apply_verified: verifying / passed / rolled_back (with the compiler errors). Needed for C# only — a successful compile reloads the domain and cuts the connection that asked, so the verdict outlives it here. Shader edits already returned their verdict on the apply call; polling one just repeats it. Free, read-only.")]
        public static VerifyDto PerfLintVerifyStatus(
            [CliArg("path", "The file to report on, as passed to perflint_apply_verified.")] string path = null)
        {
            if (string.IsNullOrEmpty(path))
                return Fail("bad_request", "path is required.");

            PerfLintScriptFixVerifier.ReadOutcome(path, out var verdict, out var errors);
            bool pending = PerfLintScriptFixVerifier.IsPending(path);

            // Pending wins over an older verdict: a second apply on the same file re-enters the queue, and
            // reporting the previous run's "passed" would be a stale answer to a live question.
            if (pending)
                return new VerifyDto
                {
                    status = "verifying", path = path,
                    message = EditorApplication.isCompiling
                        ? "Compiling now. Poll again shortly."
                        : "Queued for the next compile pass. Poll again shortly.",
                };

            if (verdict == PerfLintScriptFixVerifier.OutcomeRolledBack)
                return new VerifyDto
                {
                    status = "rolled_back", path = path, rolledBack = true, errors = errors,
                    message = "The edit did not compile and the original file was restored. The compiler errors are in "
                            + "'errors' — fix them and call perflint_apply_verified again.",
                };

            if (verdict == PerfLintScriptFixVerifier.OutcomePassed)
                return new VerifyDto
                {
                    status = "passed", path = path, verified = true,
                    message = "The edit compiled cleanly and is confirmed on disk.",
                };

            return new VerifyDto
            {
                status = "unknown", path = path,
                message = "No verification on record for this file in this editor session. Either it was never written "
                        + "by perflint_apply_verified, or the editor has restarted since.",
            };
        }

        private static VerifyDto Fail(string status, string message)
            => new VerifyDto { status = status, message = message };
    }

    [Serializable]
    public sealed class VerifyDto
    {
        /// <summary>verifying / passed / rolled_back / unknown / bad_request / not_found /
        /// no_match / ambiguous / busy / failed. No tier status: this command is free on both tiers.</summary>
        public string status;
        public string path;
        public bool verified;    // compiled cleanly and was confirmed
        public bool rolledBack;  // compile failed; the original was restored
        public string errors;    // compiler errors, when rolledBack
        public string message;
    }
}
