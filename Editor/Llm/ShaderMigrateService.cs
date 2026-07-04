using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PerfLint.L10n;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PerfLint.Llm
{
    /// <summary>
    /// The shader half of AI Migrate (docs/shader-recipe-plan.md, M2). Same recipe/propose/guard skeleton as the
    /// C# path, but the Apply/verify contract is fundamentally different — and simpler: rewriting a shader or an
    /// included .hlsl never triggers a domain reload, so verification is SYNCHRONOUS (re-import the erroring
    /// .shader, then actively compile it via ShaderCompileUtil — import alone would miss lazy HLSL-body errors)
    /// and the bounded retry loop lives in an ordinary callback chain instead of the C# path's
    /// rollback-event/static-registry dance.
    /// Two Spike-2 facts shape the target resolution: the file to migrate is the one the COMPILER ERROR points at
    /// (usually an included .hlsl — the .shader itself is often a thin shell), and ShaderMessage.file is a
    /// TRUNCATED path that must be suffix-matched against project files, never used directly.
    /// </summary>
    public static class ShaderMigrateService
    {
        private const int MaxRetries = 2;

        // ── target resolution ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve what to migrate for a SHDR004 finding (its TargetPath = the erroring .shader asset):
        /// the first compiler error's file, suffix-matched into the project; the .shader itself when the error
        /// carries no usable file (or points into read-only Packages/ — the fix then belongs at the call site).
        /// Returns null only when the shader has no recorded errors at all (nothing to migrate against).
        /// </summary>
        public static MigrateTarget ResolveFromShaderFinding(string findingTargetPath)
        {
            if (string.IsNullOrEmpty(findingTargetPath) || !findingTargetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                return null;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(findingTargetPath);
            if (shader == null) return null;

            var errors = CollectErrors(shader);
            // No Error-severity MESSAGES yet the asset is flagged broken (some parse failures record the flag
            // without a message) → still migratable, just with thinner facts. Truly clean shader → nothing to do.
            bool flaggedBroken;
            try { flaggedBroken = ShaderUtil.ShaderHasError(shader); }
            catch { flaggedBroken = false; }
            if (errors.Count == 0 && !flaggedBroken) return null;

            string targetFile = null;
            string firstErrorFile = errors[0].file;
            if (!string.IsNullOrEmpty(firstErrorFile))
            {
                string matched = FindProjectFile(firstErrorFile);
                // Only user-editable files qualify; an error pointing into Packages/ means the fix belongs
                // at the call site, which lives in the .shader (or its Assets-side includes).
                if (matched != null && matched.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    targetFile = matched;
            }

            // Redefinition against a pipeline header reports the PACKAGE side as the error file (stress-test
            // real case: 'SurfaceData' collisions all pointed at PackageCache/…/SurfaceData.hlsl) — but the fix
            // lives in whichever USER include defines the colliding symbol. Locate it through the shader's
            // dependency chain so the model actually gets to touch the right file.
            if (targetFile == null)
            {
                string symbol = ExtractRedefinedSymbol(errors);
                if (symbol != null)
                    targetFile = FindSymbolDefiner(findingTargetPath, symbol);
            }

            if (targetFile == null) targetFile = findingTargetPath;

            return new MigrateTarget
            {
                FilePath = targetFile,
                VerifyAssetPath = findingTargetPath,
                Facts = BuildErrorFacts(shader, findingTargetPath, targetFile, errors),
                UserNotice = RedefinitionNotice(errors)
            };
        }

        /// <summary>
        /// Stress-test lesson (2026-07-04, InfiniteWater ×2 failed applies): a symbol REDEFINITION against the
        /// URP ShaderLibrary (e.g. a 2019-era custom 'SurfaceData' colliding with URP's own) is only auto-fixable
        /// when the symbol's uses stay inside the rewritten file — uses in OTHER files can't be synchronized by a
        /// single-file rewrite. Say so before the user spends credits; the recipe knows how to handle the
        /// in-file case (playbook rule 8). Pure logic (unit-testable).
        /// </summary>
        internal static string RedefinitionNotice(List<(string message, string file, int line)> errors)
        {
            if (errors == null) return null;
            foreach (var e in errors)
            {
                if (e.message != null && e.message.IndexOf("redefinition of", StringComparison.OrdinalIgnoreCase) >= 0)
                    return L.Tr(
                        "This error is a symbol redefinition (a name in this asset collides with your render pipeline's library). Automatic migration can rename/remove it when the symbol is only used inside the rewritten file — but uses in OTHER files can't be synchronized, and the migration will then fail and roll back. If it fails: rename the symbol manually across all files that use it.",
                        "该错误是符号重定义（此资产中的名字与渲染管线库撞名）。若该符号只在被重写的文件内使用，自动迁移可以改名/移除；但其他文件里的使用无法被同步——那种情况迁移会失败回滚。若失败：请手动跨所有使用它的文件统一改名。");
            }
            return null;
        }

        /// <summary>Structured error list currently recorded on a shader asset (empty when unloadable).</summary>
        private static List<(string message, string file, int line)> CollectErrorsAt(string shaderAssetPath)
        {
            try
            {
                var sh = string.IsNullOrEmpty(shaderAssetPath) ? null : AssetDatabase.LoadAssetAtPath<Shader>(shaderAssetPath);
                return sh == null ? new List<(string, string, int)>() : CollectErrors(sh);
            }
            catch { return new List<(string, string, int)>(); }
        }

        /// <summary>
        /// Pure logic: did the rewrite fix ITS OWN layer? True when none of the baseline errors survived AND every
        /// remaining error lives outside the rewritten file — the fix is correct, the errors just moved down a
        /// layer (typical after clearing a redefinition that halted compilation early). File identity is compared
        /// by name suffix because compiler paths are truncated/absolute while the target is project-relative.
        /// </summary>
        internal static bool MadeProgress(
            List<(string message, string file, int line)> before,
            List<(string message, string file, int line)> after,
            string targetFile)
        {
            if (after == null || after.Count == 0) return false; // no errors at all = full success, not "progress"
            if (before != null)
            {
                foreach (var b in before)
                    foreach (var a in after)
                        if (string.Equals(a.message, b.message, StringComparison.Ordinal))
                            return false; // an original error survived → the rewrite didn't do its job
            }
            string targetName = Path.GetFileName((targetFile ?? "").Replace('\\', '/'));
            if (targetName.Length == 0) return false;
            foreach (var a in after)
            {
                string errName = Path.GetFileName((a.file ?? "").Replace('\\', '/'));
                if (string.Equals(errName, targetName, StringComparison.OrdinalIgnoreCase))
                    return false; // new error in the rewritten file itself → this rewrite introduced/left problems
            }
            return true;
        }

        /// <summary>Pure logic: the symbol name from the first "redefinition of 'X'" error, or null.</summary>
        internal static string ExtractRedefinedSymbol(List<(string message, string file, int line)> errors)
        {
            if (errors == null) return null;
            foreach (var e in errors)
            {
                if (e.message == null) continue;
                var m = Regex.Match(e.message, @"redefinition of\s+'([A-Za-z_]\w*)'", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        /// <summary>Pure logic: does this source text define struct/class/cbuffer &lt;symbol&gt;?</summary>
        internal static bool DefinesSymbol(string sourceText, string symbol)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(symbol)) return false;
            return Regex.IsMatch(sourceText, @"\b(struct|class|cbuffer)\s+" + Regex.Escape(symbol) + @"\b");
        }

        /// <summary>
        /// The user-editable file in the shader's include chain that defines <paramref name="symbol"/>.
        /// Walks #include directives by hand (AssetDatabase.GetDependencies tracks object references, and its
        /// coverage of shader includes proved unreliable in the stress test — the resolver returned nothing and
        /// the target silently fell back to the .shader). Unique hit or null (ambiguity → fall back; wrong file
        /// is worse than declining).
        /// </summary>
        internal static string FindSymbolDefiner(string shaderAssetPath, string symbol)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(shaderAssetPath.Replace('\\', '/'));
            string found = null;

            while (queue.Count > 0 && visited.Count < 64) // include graphs are small; guard against cycles/explosions
            {
                string cur = queue.Dequeue();
                if (!visited.Add(cur)) continue;

                string text;
                try
                {
                    string full = Path.GetFullPath(cur);
                    text = File.Exists(full) ? File.ReadAllText(full) : null;
                }
                catch { text = null; }
                if (text == null) continue;

                if (!string.Equals(cur, shaderAssetPath, StringComparison.OrdinalIgnoreCase) &&
                    DefinesSymbol(text, symbol))
                {
                    if (found != null && !string.Equals(found, cur, StringComparison.OrdinalIgnoreCase))
                        return null; // two definers → ambiguous
                    found = cur;
                }

                foreach (Match m in Regex.Matches(text, "#include\\s+\"([^\"]+)\""))
                {
                    string resolved = ResolveIncludePath(cur, m.Groups[1].Value);
                    // Only user-editable files are worth walking into (package headers can't be the fix target).
                    if (resolved != null && resolved.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                        queue.Enqueue(resolved);
                }
            }
            return found;
        }

        /// <summary>
        /// Pure logic: resolve an #include reference against the including file's directory. Absolute project
        /// references (Assets/… or Packages/…) pass through; relative ones (including ../) resolve and normalize.
        /// Null when the path escapes the project root.
        /// </summary>
        internal static string ResolveIncludePath(string includingFile, string includeRef)
        {
            if (string.IsNullOrEmpty(includeRef)) return null;
            string inc = includeRef.Replace('\\', '/').TrimStart('/');
            if (inc.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                inc.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return inc;

            string dir = Path.GetDirectoryName((includingFile ?? "").Replace('\\', '/'))?.Replace('\\', '/') ?? "";
            var parts = new List<string>(dir.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (var seg in inc.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    if (parts.Count == 0) return null; // escaped the project root
                    parts.RemoveAt(parts.Count - 1);
                }
                else parts.Add(seg);
            }
            return string.Join("/", parts);
        }

        /// <summary>All Error-severity messages currently recorded on the shader (import-time compile state).</summary>
        private static List<(string message, string file, int line)> CollectErrors(Shader shader)
        {
            var list = new List<(string, string, int)>();
            try
            {
                foreach (var m in ShaderUtil.GetShaderMessages(shader))
                    if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                        list.Add((m.message, m.file, m.line));
            }
            catch { /* messages unavailable → empty list */ }
            return list;
        }

        /// <summary>Suffix-match a compiler-truncated path against the project's assets (unique match or null).</summary>
        internal static string FindProjectFile(string truncatedPath)
        {
            string fileName = Path.GetFileName(truncatedPath.Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName)) return null;
            var candidates = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName)))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(p) && p.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(p);
            }
            return MatchTruncatedPath(truncatedPath, candidates);
        }

        /// <summary>
        /// Pure logic: pick the single candidate whose path ends with the truncated path. The compiler truncates
        /// CHARACTER-wise, not segment-wise (Spike 2 real shape: "Boat Attack Water System/…" arrives as
        /// "System/…"), so two tiers apply: segment-aligned tail match (strong), then — only when the truncated
        /// path still carries a directory component — a raw character-level tail match (weak). A bare file name
        /// gets the strong tier only (a half-cut file name must never match). Ambiguity within the decisive tier
        /// returns null — writing to the wrong file is worse than declining.
        /// </summary>
        internal static string MatchTruncatedPath(string truncated, IReadOnlyList<string> candidates)
        {
            if (string.IsNullOrEmpty(truncated) || candidates == null) return null;
            string t = truncated.Replace('\\', '/').TrimStart('/');
            if (t.Length == 0) return null;
            bool hasDir = t.IndexOf('/') >= 0;

            string strong = null; bool strongDup = false;
            string weak = null; bool weakDup = false;
            foreach (var c in candidates)
            {
                string n = (c ?? "").Replace('\\', '/');
                if (n.Length == 0) continue;
                if (string.Equals(n, t, StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith("/" + t, StringComparison.OrdinalIgnoreCase))
                {
                    if (strong != null && !string.Equals(strong, c, StringComparison.OrdinalIgnoreCase)) strongDup = true;
                    else strong = c;
                }
                else if (hasDir && n.EndsWith(t, StringComparison.OrdinalIgnoreCase))
                {
                    if (weak != null && !string.Equals(weak, c, StringComparison.OrdinalIgnoreCase)) weakDup = true;
                    else weak = c;
                }
            }
            if (strong != null) return strongDup ? null : strong;
            if (weak != null) return weakDup ? null : weak;
            return null;
        }

        /// <summary>Per-finding authoritative facts: which shader is broken, the full error list, and why THIS file was chosen.</summary>
        private static string BuildErrorFacts(Shader shader, string shaderAssetPath, string targetFile,
            List<(string message, string file, int line)> errors)
        {
            var sb = new StringBuilder();
            sb.Append("【报错的 shader】").Append(shader.name).Append("（").Append(shaderAssetPath).Append("）\n");
            sb.Append("【当前全部编译错误（权威，修复以消除这些错误为准）】\n");
            if (errors.Count == 0)
                sb.Append("- （引擎标记该 shader 编译失败，但未提供错误详情——请自行分析源文件中的语法/结构问题）\n");
            foreach (var e in errors)
            {
                sb.Append("- ").Append(e.message);
                if (!string.IsNullOrEmpty(e.file)) sb.Append("  @ ").Append(e.file).Append(':').Append(e.line);
                sb.Append('\n');
            }
            if (!string.Equals(targetFile, shaderAssetPath, StringComparison.OrdinalIgnoreCase))
                sb.Append("【说明】你要重写的是错误所在的 include 文件 ").Append(targetFile)
                  .Append("，而不是 .shader 主文件——修好它即可让上述 shader 编译通过。");
            return sb.ToString();
        }

        // ── synchronous apply + verify + bounded retry ────────────────────────────────────────────

        /// <summary>
        /// Write the rewrite, re-import the erroring shader, actively compile it, and either finish or roll back
        /// and retry with the fresh errors (≤2 rounds, same guards each round). One user-approved click authorizes
        /// the whole loop — mirroring the C# contract: the file can never end up worse than before the click.
        /// Asynchronous only because retries call the LLM; all verification is synchronous.
        /// </summary>
        public static void ApplyWithVerify(MigrateProposal p, Action<bool, string> onDone)
        {
            Attempt(p, onDone);
        }

        private static void Attempt(MigrateProposal p, Action<bool, string> onDone)
        {
            string fullTarget;
            string raw;
            try
            {
                fullTarget = Path.GetFullPath(p.FilePath);
                raw = File.Exists(fullTarget) ? File.ReadAllText(fullTarget) : null;
            }
            catch (Exception ex) { onDone(false, ex.Message); return; }
            if (raw == null) { onDone(false, L.Tr("Failed to read the file.", "读取文件失败。")); return; }

            // Stale check every round: attempt 0 guards against user edits since Propose; retry rounds compare
            // against the same baseline because rollback restored exactly it.
            if (raw.Replace("\r\n", "\n").Replace("\r", "\n") != p.Original)
            {
                onDone(false, L.Tr("The file changed after this migration was generated; applying it would overwrite those changes. Please regenerate.",
                                   "生成迁移后文件已被改动，直接应用会覆盖那些改动。请重新生成。"));
                return;
            }

            // Baseline: the errors this migration set out to fix (the shader's current recorded state).
            var baseline = CollectErrorsAt(p.VerifyAssetPath);

            try
            {
                File.WriteAllText(fullTarget, AdaptLineEndings(p.Migrated, raw), new UTF8Encoding(false));
            }
            catch (Exception ex) { onDone(false, ex.Message); return; }

            var (ok, errorSummary, caveat) = VerifyShader(p);

            // Layered-error progress (stress-test lesson: fixing the SurfaceData redefinition let compilation
            // advance and EXPOSE the next layer in a DIFFERENT file — retries can only edit this round's target,
            // so the correct fix kept getting rolled back with it). If the original errors are gone and every
            // remaining error lives outside the rewritten file, this file IS fixed: keep it, report the next
            // layer honestly, and let the next click target the newly-erroring file.
            if (!ok)
            {
                var after = CollectErrorsAt(p.VerifyAssetPath);
                if (MadeProgress(baseline, after, p.FilePath))
                {
                    onDone(true, L.Tr(
                        "This file is fixed — its original errors are gone. Compiling further revealed the next layer of errors elsewhere:\n" +
                        errorSummary + "\nClick AI Migrate on this shader again: the next round will target the newly-erroring file.",
                        "此文件已修复——原有错误已消除。继续编译揭示了位于其他文件的下一层错误：\n" +
                        errorSummary + "\n再次对该 shader 点 AI Migrate 即可继续——下一轮会自动指向新报错的文件。"));
                    return;
                }
            }

            if (ok)
            {
                // The shader reimport can leave the editor's scene lighting state stale (ambient probe /
                // lighting-data bindings) — the same thing happens when a shader file is edited by hand.
                // Nudge the environment lighting; a scene reload fixes the rest, so the message says so.
                try { DynamicGI.UpdateEnvironment(); } catch { }
                onDone(true, caveat ?? L.Tr(
                    "File rewritten and the shader now compiles (verified by actively compiling its passes). Check the visual result in your scene. " +
                    "If the Scene/Game view looks dark or unlit afterwards, reopen the scene — a known editor quirk after shader recompiles; your assets are unaffected.",
                    "文件已重写，该 shader 现已编译通过（已主动编译其 pass 验证）。请进场景确认渲染效果。" +
                    "若 Scene/Game 视图出现变暗或光照丢失，重新打开场景即可恢复——这是 Unity 在 shader 重编译后的编辑器已知表现，资产本身不受影响。"));
                return;
            }

            // Failed → roll the file back BEFORE anything else (the project must never sit in a broken state).
            try
            {
                File.WriteAllText(fullTarget, raw, new UTF8Encoding(false));
                Reimport(p);
            }
            catch (Exception ex)
            {
                onDone(false, L.Tr("Verification failed AND rollback failed — restore the file from version control: ", "验证失败且回滚失败——请从版本控制恢复该文件：") + ex.Message);
                return;
            }

            if (p.Attempt >= MaxRetries)
            {
                onDone(false, L.Tr(
                    $"Still failing to compile after {MaxRetries} automatic retries — rolled back (errors below). Try a stronger model (bring your own key), or migrate manually via Explain.\n",
                    $"自动重试 {MaxRetries} 轮后仍未通过编译——已回滚（错误见下）。可换更强模型（BYO key）重试，或按 Explain 指引手动迁移。\n") + errorSummary);
                return;
            }

            int nextAttempt = p.Attempt + 1;
            Debug.Log("[PerfLint] " + L.Tr(
                $"AI Migrate (shader): compile failed — automatic retry {nextAttempt}/{MaxRetries}, feeding the compiler errors back to the model…",
                $"AI 迁移（shader）：编译未通过——自动重试第 {nextAttempt}/{MaxRetries} 轮，把编译错误喂回模型重生成…"));

            string user = MigrateService.BuildRetryUser(p.Migrated, errorSummary);
            LlmClient.Send(LlmSettings.FixModel, p.Recipe.SystemPrompt, new[] { new LlmMessage("user", user) }, p.Recipe.MaxTokens, r =>
            {
                if (!r.Success) { onDone(false, L.Tr("Retry failed: ", "重试失败：") + r.Error); return; }
                var p2 = MigrateService.Parse(r.Text, p.Original, p.Recipe, p.FilePath);
                if (!p2.Ok || p2.NoChange)
                {
                    onDone(false, L.Tr("Retry rejected by guards: ", "重试被守卫拒绝：") + (p2.Error ?? "no change"));
                    return;
                }
                p2.FilePath = p.FilePath;
                p2.VerifyAssetPath = p.VerifyAssetPath;
                p2.Attempt = nextAttempt;
                Attempt(p2, onDone);
            }, disableThinking: true);
        }

        /// <summary>
        /// Synchronous verification: re-import the erroring shader (reads the just-written include from disk),
        /// check the import-time state, then actively compile its passes — import alone would miss lazy
        /// HLSL-body errors (the Spike-1 premise). Returns (ok, errorSummary, caveat).
        /// </summary>
        private static (bool ok, string errors, string caveat) VerifyShader(MigrateProposal p)
        {
            string verifyPath = !string.IsNullOrEmpty(p.VerifyAssetPath) ? p.VerifyAssetPath : p.FilePath;
            if (!verifyPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                return (true, null, L.Tr("File rewritten. No shader asset was associated for automatic verification — confirm the result manually.",
                                          "文件已重写。没有可用于自动验证的 shader 资产——请手动确认结果。"));
            try
            {
                Reimport(p);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(verifyPath);
                if (shader == null)
                    return (false, "verification shader asset failed to load: " + verifyPath, null);

                if (ShaderUtil.ShaderHasError(shader))
                {
                    var sb = new StringBuilder();
                    foreach (var e in CollectErrors(shader))
                        sb.Append(e.message).Append(string.IsNullOrEmpty(e.file) ? "" : "  @ " + e.file + ":" + e.line).Append('\n');
                    return (false, sb.Length > 0 ? sb.ToString() : "shader import reported errors", null);
                }

                var r = ShaderCompileUtil.CompileCheck(shader);
                if (r.Available && !r.Success)
                    return (false, string.Join("\n", r.Errors), null);
                if (!r.Available)
                    return (true, null, L.Tr("File rewritten; the shader imports without errors, but deep per-pass verification was unavailable on this editor — check the visual result in your scene.",
                                              "文件已重写；该 shader 导入无错误，但本编辑器无法做逐 pass 深度验证——请进场景确认渲染效果。"));
                return (true, null, null);
            }
            catch (Exception ex)
            {
                return (false, "verification threw: " + ex.Message, null);
            }
        }

        /// <summary>
        /// Every shader whose SHDR004 finding should be re-checked after a successful shader migration: the one
        /// just verified, plus every OTHER shader currently flagged SHDR004 — rewriting a shared include often
        /// heals several shaders at once (real smoke-test case: one WaterCommon.hlsl fix healed both Water.shader
        /// and WaterTessellated.shader, and the sibling's finding sat stale until a full rescan). The flagged set
        /// is tiny by definition, so rescanning all of it is cheap and precise — no reverse-dependency query needed.
        /// </summary>
        internal static List<string> AffectedShaderPaths(IEnumerable<PerfLint.Core.Finding> currentFindings, string primaryPath)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(primaryPath) && seen.Add(primaryPath)) paths.Add(primaryPath);
            if (currentFindings != null)
            {
                foreach (var f in currentFindings)
                    if (f != null && f.RuleId == "SHDR004" && !string.IsNullOrEmpty(f.TargetPath) && seen.Add(f.TargetPath))
                        paths.Add(f.TargetPath);
            }
            return paths;
        }

        private static void Reimport(MigrateProposal p)
        {
            // The rewritten file first (so the asset database sees the change), then the shader that includes it.
            try { AssetDatabase.ImportAsset(p.FilePath, ImportAssetOptions.ForceSynchronousImport); } catch { }
            if (!string.IsNullOrEmpty(p.VerifyAssetPath) &&
                !string.Equals(p.VerifyAssetPath, p.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                try { AssetDatabase.ImportAsset(p.VerifyAssetPath, ImportAssetOptions.ForceSynchronousImport); } catch { }
            }
        }

        private static string AdaptLineEndings(string snippetLf, string full)
        {
            string s = snippetLf.Replace("\r\n", "\n").Replace("\r", "\n");
            return full.Contains("\r\n") ? s.Replace("\n", "\r\n") : s;
        }
    }

    /// <summary>Shader migration recipes (the SHDR004-triggered URP upgrade playbook).</summary>
    public static class ShaderRecipes
    {
        // ── SHDR004 → migrate the erroring shader/include to the project's current URP version ──────
        public static readonly MigrateRecipe UrpShaderRecipe = new MigrateRecipe
        {
            RuleId = "SHDR004",
            Kind = MigrateKind.Shader,
            ResolveTarget = ShaderMigrateService.ResolveFromShaderFinding,
            MaxLines = 600,
            Summary = () => L.Tr(
                "Rewrites the file the compiler error points at (often an included .hlsl) to fix this shader's compile errors against your current URP version, then re-compiles the shader to verify.",
                "重写编译错误所指向的文件（通常是被 include 的 .hlsl），按你当前的 URP 版本修复该 shader 的编译错误，并重新编译验证。"),
            ProbeEnvironment = ProbeUrpShaderEnvironment,
            SystemPrompt =
                "你是资深 Unity 图形工程师。用户给你一个 shader 相关源文件的全文（可能是 .shader，也可能是被 include 的 .hlsl/.cginc），" +
                "它导致某个 shader 在当前 Unity/URP 版本编译失败。用户消息里给出了【当前全部编译错误】与【当前工程 URP 实测环境】——" +
                "它们是权威事实，优先于你对任何 Unity/URP 版本的记忆。你的唯一目标：**做最小的修改，让列出的编译错误全部消失，且渲染行为尽可能不变**。\n" +
                "【高频升级破坏点对照表（按错误症状匹配后套用）】\n" +
                "1) 'DirectBDRF': no matching N parameter function → URP 12 起改名，写 DirectBRDF（同参调用）。\n" +
                "2) Couldn't open include file '….lightweight…' → LWRP 旧包路径，换 Packages/com.unity.render-pipelines.universal/ShaderLibrary/ 下的同名文件。\n" +
                "3) undeclared identifier 'SampleSceneDepth' / 'SampleSceneColor' → 补 include DeclareDepthTexture.hlsl / DeclareOpaqueTexture.hlsl（在 URP ShaderLibrary 下）。\n" +
                "4) 'GetShadowFade' 相关错误 → 换 GetMainLightShadowFade(positionWS)（主光）或 GetAdditionalLightShadowFade。\n" +
                "5) GetMainLight / LightingPhysicallyBased 参数不匹配 → 按当前版本现存的最简重载重排实参；不确定时改走 UniversalFragmentPBR。\n" +
                "6) 'fixed'/'fixed4' 无法识别 → HLSL 无 fixed 家族，全部换 half/half4。\n" +
                "7) 源文件是 CGPROGRAM 且错误因 CG 库而起 → CGPROGRAM/ENDCG 换 HLSLPROGRAM/ENDHLSL，UnityCG.cginc 换 URP 的 Core.hlsl，" +
                "UnityObjectToClipPos→TransformObjectToHClip（SpaceTransforms 已含于 Core.hlsl）。仅在错误确实因此而起时才做，不做无关换血。\n" +
                "8) redefinition of 'X'（与 URP 库符号撞名）→ 若 X 的定义与全部引用都在本文件内：统一改名（如 X→WaterX）；" +
                "若引用在其他文件（你看不到它们）：**不要改名**——那会把错误扩散到别处，改不完；此时保持原样并在定义处留 " +
                "// TODO(PerfLint AI Migrate): 需跨文件统一改名 注释，让人工处理。\n" +
                "9) l-value specifies const object → 几乎总是给 inout/out 参数传了字面量或常量表达式（URP 新版把一些参数改成了 inout，" +
                "典型：InitializeBRDFData 的 alpha 参数）——把字面量提取为局部变量再传：half alpha = 1; InitializeBRDFData(..., alpha, brdfData);。" +
                "【注意】报错行号常指向相邻行（如下一行）：检查报错行及其上下几行的函数调用实参，找到那个字面量。\n" +
                "【反幻觉纪律】\n" +
                "- 绝不调用你不能确定存在于当前 URP 版本的 ShaderLibrary 函数/宏；环境事实里列出的 include 文件才可用。\n" +
                "- 不新增 multi_compile/shader_feature 关键字、不增删 Pass、不改 Tags/LOD/Fallback（除非错误直接因它而起）。\n" +
                "- 某处旧逻辑在新 API 下确实找不到对应物时，宁可用行为最接近的保守等价实现并留一行 // TODO(PerfLint AI Migrate): <说明>，也不要编造 API。\n" +
                "【硬性要求】\n" +
                "- 输出必须是完整的整个源文件：从第一行到最后一行逐行给出，不得省略、不得用「// ... 其余不变」占位、不得截断。\n" +
                "- 若文件是 .shader：Shader \"名字\" 一个字符都不能改；Properties 块里已有的属性一个都不能删或改名。\n" +
                "- 只做与消除所列编译错误相关的最小改动：函数结构、变量名、注释、空行布局全部保持原样。\n" +
                "严格按如下格式回复，不要有任何其他文字或解释：\n" +
                "<<<FILE>>>\n（完整修改后的源文件）\n<<<END>>>"
        };

        // ShaderLibrary files whose presence the model may rely on for #include fixes. Existence is checked on
        // disk via the package's resolvedPath — Path.GetFullPath cannot resolve the Packages/ virtual folder.
        private static readonly string[] ProbedLibs =
            { "Core.hlsl", "Lighting.hlsl", "DeclareDepthTexture.hlsl", "DeclareOpaqueTexture.hlsl", "SurfaceInput.hlsl", "Shadows.hlsl" };

        /// <summary>Deterministic facts: the project's URP version and which ShaderLibrary includes actually exist on disk.</summary>
        private static string ProbeUrpShaderEnvironment()
        {
            PackageInfo info = null;
            try { info = PackageInfo.FindForAssetPath("Packages/com.unity.render-pipelines.universal"); }
            catch { /* fall through */ }
            if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                return "【当前工程 URP 实测环境】URP 包未安装——不要引用任何 URP ShaderLibrary include。";

            var present = new List<string>();
            var absent = new List<string>();
            foreach (var lib in ProbedLibs)
            {
                bool exists;
                try { exists = File.Exists(Path.Combine(info.resolvedPath, "ShaderLibrary", lib)); }
                catch { exists = false; }
                (exists ? present : absent).Add(lib);
            }
            return FormatUrpShaderFacts(info.version, present, absent);
        }

        /// <summary>Pure formatting of the probe result (unit-testable without URP installed).</summary>
        internal static string FormatUrpShaderFacts(string urpVersion, IReadOnlyList<string> present, IReadOnlyList<string> absent)
        {
            var sb = new StringBuilder();
            sb.Append("【当前工程 URP 实测环境（磁盘实查，权威，优先于版本经验）】\n");
            sb.Append("URP 版本：").Append(urpVersion).Append('\n');
            sb.Append("ShaderLibrary 下确认存在的 include：").Append(present.Count > 0 ? string.Join("、", present) : "（无）");
            if (absent.Count > 0)
                sb.Append("\n不存在、绝不能 include 的文件：").Append(string.Join("、", absent));
            return sb.ToString();
        }
    }
}
