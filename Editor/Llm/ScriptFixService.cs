using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PerfLint.Core;
using PerfLint.L10n;

namespace PerfLint.Llm
{
    public sealed class ScriptFixProposal
    {
        public bool Ok;
        public string Error;
        public string Original;   // The original snippet to be replaced (line endings as \n, indentation preserved)
        public string Fixed;      // The corrected snippet
        public string FilePath;
        public int ExpectedLine;  // The flagged line number (1-based). When applying multiple fixes to the same file,
                                  // content-based lookup can hit duplicate lines — use this to pick the match
                                  // "closest to the expected line" among identically-content matches, avoiding
                                  // cross-fix misplacement. 0 = no anchor (take first match, legacy behavior).
        public bool Locatable;    // Whether the original snippet can be located in the file by line (determines whether one-click apply is possible)
        public bool NoChange;     // Fix is equivalent to the original after stripping whitespace → likely a false-positive rule hit
        public string FieldDecl;  // New class field declaration to inject (cache-type fix: inserted deterministically at the top of the class body by the tool, not relying on the model to place it correctly)
        public string[] Usings;   // using namespaces referenced by FIXED but missing from the file (e.g. UnityEngine.SceneManagement); the tool deduplicates and inserts them at the top of the file

        // ── Deterministic "behavior hint" result (see ScriptFixService.BehaviorNote). Not an LLM judgment —
        // only flags known, statically-identifiable semantic traps that a non-expert can easily overlook
        // (currently: FindObjectOfType→FindAnyObjectByType losing the "return the first" semantic).
        // Intentionally does not cover standard ??= caching (that is an accepted optimization; "object may change"
        // is a universal caveat, and flagging every case would just add noise).
        public bool BehaviorRisk; // Matched a known trap → review window unchecked by default, diff annotated with ⚠
        public string RiskReason; // One-line explanation (the difference and the recommendation)
    }

    /// <summary>
    /// Script-level AI fix. Sends "the flagged line + surrounding code window" to the model
    /// (**only this snippet — never the whole file or project**), and requests an ORIGINAL/FIXED
    /// pair in return. On apply, locates the original with line-tolerant matching, replaces it,
    /// and triggers a compile verification; a failure triggers automatic rollback.
    /// </summary>
    public static class ScriptFixService
    {
        private const int Context = 24;

        // ── Deterministic rename fast path ─────────────────────────────────────────────────────────
        // Pure rename migrations (same arguments, same call shape) don't need an LLM at all: the model may not
        // even KNOW the replacement API when it's newer than its training data (real case: deepseek-flash had no
        // knowledge of GetEntityId — Unity 6.5's GetInstanceID replacement — and produced nothing applicable).
        // For rules registered here, Propose skips the LLM and builds the proposal locally: zero tokens, zero
        // hallucination risk, instant, nothing ever sent — then reuses the exact same diff/apply/verify/rollback
        // pipeline. Kept here (not on the scanner's rule table) so it works even when findings are restored from
        // disk before any scanner type initializes.
        //
        // ADMISSION BAR (learned the hard way): "rename-only" must hold for the RETURN VALUE'S CONSUMERS too,
        // not just the call shape. GetInstanceID→GetEntityId was admitted and then evicted: on Unity 6000.5 the
        // EntityId→int implicit operator is itself error-level obsolete, so every call site that stores the
        // result in an int fails CS0619 after the swap — the receiving types must migrate with it (structural).
        // The compile-verify + rollback net caught it; the entry bar now includes tracing where the value flows.
        internal static readonly Dictionary<string, (string from, string to)> DeterministicRenames =
            new Dictionary<string, (string, string)>
            {
                // (empty for now — GetInstanceID was evicted, see above; add only true renames whose value flow is unchanged)
            };

        /// <summary>Whether this rule's fix is a deterministic local rename (no LLM call, nothing sent).</summary>
        public static bool IsDeterministic(string ruleId) =>
            ruleId != null && DeterministicRenames.ContainsKey(ruleId);

        /// <summary>
        /// Build a rename proposal locally: replace the token (word-bounded) on the flagged line only.
        /// Returns a NoChange proposal when the token isn't on that line (stale finding) — honest, not a guess.
        /// </summary>
        internal static ScriptFixProposal ProposeRename(Finding f, string from, string to)
        {
            var lines = ReadLines(f.CodeFile);
            if (lines == null)
                return new ScriptFixProposal { Ok = false, Error = L.Tr("Failed to read the source file.", "读取源文件失败。"), FilePath = f.CodeFile };

            int idx = Clamp(f.CodeLine - 1, 0, lines.Length - 1);
            string flagged = lines[idx];
            string fixedLine = Regex.Replace(flagged, @"\b" + Regex.Escape(from) + @"\b", to);

            var p = new ScriptFixProposal
            {
                Ok = true,
                Original = flagged,
                Fixed = fixedLine,
                FilePath = f.CodeFile,
                ExpectedLine = f.CodeLine,
                NoChange = fixedLine == flagged
            };
            p.Locatable = !p.NoChange; // original IS the current file line, so it always locates
            return p;
        }

        private const string SystemPrompt =
            "你是资深 Unity 工程师。用户给你一段 C# 代码窗口、被标记的行与【规则说明】。请严格按规则说明，" +
            "对被标记处做最小改动的修复（可能是性能优化，也可能是 API 迁移/改名，如 FindObjectOfType→FindAnyObjectByType），" +
            "不改变原有行为、不重构无关代码。\n" +
            "【硬性要求】修复后的代码必须自洽、可直接编译：FIXED 中不得引用任何在给定代码里不存在的字段、变量或方法；" +
            "改名/迁移时注意参数与返回类型是否需要相应调整。\n" +
            "若修复需要把每帧昂贵调用的结果缓存为类字段（如 Camera.main / GetComponent / FindObjectOfType），" +
            "请用【就地 null 合并赋值】，只改最小片段——硬性规则：\n" +
            "  1) 把被标记的表达式 EXPR 原地替换为 (_字段 ??= EXPR)：首次求值时缓存、之后复用，且可用在任意位置（含方法参数里、表达式中间）。字段名用下划线开头，如 _cam。\n" +
            "     【关键】只替换 EXPR 这一小段，被标记行/语句的其余部分必须逐字保留——`yield return`、赋值左侧、调用链、行尾分号都不能丢；FIXED 必须仍是一条完整、可直接编译的语句。\n" +
            "     例：`yield return new WaitForSeconds(1.5f);` 应改成 `yield return (_wait ??= new WaitForSeconds(1.5f));`（保留 yield return 与分号），绝不能只写 `(_wait ??= new WaitForSeconds(1.5f))`。\n" +
            "  2) 【不要自己声明字段】——不要写 private XXX _cam;，工具会自动把字段声明插到类体顶部。也不要改方法签名、不要把声明放进方法体。\n" +
            "  3) ORIGINAL/FIXED 只取被改动的最小连续片段即可（不必含方法签名）。\n" +
            "  4) 【被缓存的表达式必须与原文逐字一致】——(_字段 ??= 这里) 里放的，必须是 ORIGINAL 中原本就有的那段表达式，原样照搬；" +
            "禁止改写它、禁止虚构不存在的方法（如对 WaitForSeconds 调用并不存在的 .Reset(...)）、禁止把参数换成占位值（如 new WaitForSeconds(0)）。\n" +
            "  5) 【只缓存「每次求值都相同」的表达式】——Camera.main / GetComponent / FindObjectOfType 这类返回同一对象的可以缓存；" +
            "若被标记表达式的值每次都不同（如 new WaitForSeconds(Random.Range(min,max))、依赖 Random./Time./每次不同的变量），缓存会把首次的值固定下来、改变行为——" +
            "【这种情况不要缓存】，直接返回与 ORIGINAL 完全相同的 FIXED（表示无需机械修改，留给人判断）。\n" +
            "示例一（缓存 Camera.main —— FIXED 里没有字段声明，只把表达式换成 (_cam ??= ...)）：\n" +
            "<<<ORIGINAL>>>\n" +
            "        var r = Camera.main.ScreenPointToRay(Input.mousePosition);\n" +
            "<<<FIXED>>>\n" +
            "        var r = (_cam ??= Camera.main).ScreenPointToRay(Input.mousePosition);\n<<<END>>>\n" +
            "示例二（昂贵调用在方法参数里 —— ??= 是表达式，照样原地替换，绝不要拆成单独语句）：\n" +
            "<<<ORIGINAL>>>\n" +
            "            tooltip.parent.GetComponent<RectTransform>(),\n" +
            "<<<FIXED>>>\n" +
            "            (_parentRect ??= tooltip.parent.GetComponent<RectTransform>()),\n<<<END>>>\n" +
            "若修复不涉及缓存字段（如纯 API 改名 FindObjectOfType→FindAnyObjectByType），照常给最小片段、无需 ??=。\n" +
            "【硬性要求·补充】除上面授权的 (_字段 ??= EXPR) 缓存写法外，禁止在 FIXED 里引入任何新的类字段/状态变量。\n" +
            "针对每帧字符串分配（GC001：字符串插值 $\"...\" 或含字面量的 + 拼接）——你要解决的是【字符串分配本身】，" +
            "而不是把它包进「值变化时才执行」的频率守卫里：那种写法拼接照样原地保留、只是少跑几次，并未消除分配，且需要 _lastXxx/_prevXxx 记忆字段（禁止，无处声明会编译失败）。\n" +
            "  · 只有当能在【不新增任何状态、不改变行为】的前提下真正去掉/降低该次分配时，才给出修复；\n" +
            "  · 否则——尤其当它是 Debug.Log/LogWarning 等诊断日志（每帧打本身才是问题，拼接无法无状态消除）——" +
            "请直接返回与 ORIGINAL 完全相同的 FIXED（表示无需机械修改，留给人判断是否删日志/降频），不要自创需要新字段或改变行为的方案。\n" +
            "若 FIXED 用到的类型需要给定代码窗口里尚未出现的命名空间（如 SceneManager 需 UnityEngine.SceneManagement），" +
            "在 <<<FIXED>>> 之后、<<<END>>> 之前补一段 <<<USINGS>>>，每行一个命名空间（只写命名空间本身，不带 using 关键字与分号）；" +
            "工具会去重后插到文件顶部。不需要新命名空间则整段省略。\n" +
            "严格按如下格式回复你的修复，不要有任何其他文字或解释：\n" +
            "<<<ORIGINAL>>>\n（从给定代码中逐字复制、需要被替换的原始片段，含原有缩进）\n" +
            "<<<FIXED>>>\n（修正后的片段）\n" +
            "（可选）<<<USINGS>>>\nUnityEngine.SceneManagement\n" +
            "<<<END>>>\n" +
            "ORIGINAL 必须与给定代码逐字一致（含缩进与换行），以便程序做文本替换。";

        public static int WindowLineCount(Finding f)
        {
            var lines = ReadLines(f.CodeFile);
            if (lines == null) return 0;
            int idx = Clamp(f.CodeLine - 1, 0, lines.Length - 1);
            int start = Math.Max(0, idx - Context);
            int end = Math.Min(lines.Length - 1, idx + Context);
            return end - start + 1;
        }

        public static void Propose(Finding f, Action<ScriptFixProposal> onDone)
        {
            // Deterministic rename rules: build locally, never call the LLM (see DeterministicRenames).
            if (f.RuleId != null && DeterministicRenames.TryGetValue(f.RuleId, out var rename))
            {
                onDone(ProposeRename(f, rename.from, rename.to));
                return;
            }

            var lines = ReadLines(f.CodeFile);
            if (lines == null)
            {
                onDone(new ScriptFixProposal { Ok = false, Error = L.Tr("Failed to read the source file.", "读取源文件失败。"), FilePath = f.CodeFile });
                return;
            }

            int idx = Clamp(f.CodeLine - 1, 0, lines.Length - 1);
            int start = Math.Max(0, idx - Context);
            int end = Math.Min(lines.Length - 1, idx + Context);

            var window = new StringBuilder();
            for (int i = start; i <= end; i++) window.Append(lines[i]).Append('\n');
            string flagged = lines[idx];

            string user =
                $"规则 {f.RuleId}：{f.Title}\n{f.Detail}\n\n" +
                $"被标记的问题在这一行：\n{flagged}\n\n" +
                $"代码窗口（原样，不含行号）：\n{window}";

            // FixModel: force flash (fixing is a mechanical format-output task, deep reasoning not needed).
            // disableThinking=true: disables DeepSeek thinking mode, skipping the chain-of-thought and going
            // straight to the answer — faster, saves tokens, and eliminates "thinking exhausts the budget,
            // leaving content empty". 8192 as a safety margin (in case the endpoint ignores the flag,
            // there is still budget left; paired with finish_reason error reporting).
            LlmClient.Send(LlmSettings.FixModel, SystemPrompt, new[] { new LlmMessage("user", user) }, 8192, r =>
            {
                if (!r.Success)
                {
                    onDone(new ScriptFixProposal { Ok = false, Error = r.Error, FilePath = f.CodeFile });
                    return;
                }

                var p = Parse(r.Text);
                p.FilePath = f.CodeFile;
                p.ExpectedLine = f.CodeLine; // Anchor: when applying multiple fixes to the same file, locates by the match closest to this line, avoiding cross-fix misplacement on duplicate lines
                if (p.Ok)
                {
                    string full = SafeReadAllText(f.CodeFile);
                    p.Locatable = full != null && LocateRegion(full, p.Original, p.ExpectedLine).len > 0;
                    // Field is already declared in the file (rare) → skip injecting a duplicate declaration.
                    if (!string.IsNullOrEmpty(p.FieldDecl) && full != null && FieldAlreadyDeclared(full, p.FieldDecl))
                        p.FieldDecl = null;

                    // Guard: FIXED introduced underscore fields that are neither in the original snippet,
                    // nor declared in the file, nor the cache field the tool is about to inject.
                    // (Typical case: the model smuggled in a _lastXxx memory field for "run only on change",
                    // which has nowhere to be declared → guaranteed compile failure on apply.)
                    // Reject outright to avoid the bad round-trip of apply → compile error → auto-rollback.
                    var undeclared = IntroducedUndeclaredFields(p.Original, p.Fixed, p.FieldDecl, full);
                    if (undeclared.Count > 0)
                    {
                        p.Ok = false;
                        p.Error = L.Tr("The AI fix introduced undeclared fields (", "AI 修复引入了未声明的字段（") + string.Join("、", undeclared) +
                                  L.Tr("); applying it would fail to compile, so it was rejected. Please regenerate, or fix manually.", "），应用后会编译失败，已拒绝。请重新生成，或手动修复。");
                    }

                    // Guard: FIXED truncated a complete statement from ORIGINAL into a broken fragment.
                    // (Typical case: the model mistakenly rewrote
                    // `yield return new WaitForSeconds(1.5f);` as `(_w ??= new WaitForSeconds(1.5f))`,
                    // dropping the `yield return` and the semicolon — brackets balance but it is no longer
                    // a valid statement.) Such wreckage will fail to compile once written; since a single-shot
                    // fix does not trigger an immediate compile, rollback would only take effect at the next
                    // natural compile — so rejecting before writing is the safest approach.
                    if (p.Ok)
                    {
                        string integrity = StatementIntegrityIssue(p.Original, p.Fixed);
                        if (integrity != null)
                        {
                            p.Ok = false;
                            p.Error = L.Tr("The AI fix broke statement integrity (", "AI 修复破坏了语句完整性（") + integrity +
                                      L.Tr("); applying it would fail to compile, so it was rejected. Please regenerate, or fix manually.", "），应用后会编译失败，已拒绝。请重新生成，或手动修复。");
                        }
                    }

                    // Guard: in a (_field ??= EXPR) cache, EXPR must be a real expression from the original.
                    // (Typical bad case: the model rewrote
                    // `new WaitForSeconds(Random.Range(min,max))` as `(_w ??= new WaitForSeconds(0)).Reset(...)`
                    // — fabricating a non-existent .Reset() on WaitForSeconds and substituting a placeholder
                    // argument of 0; or caching an expression whose value differs each call, changing behavior.)
                    // These "syntactically coherent but semantically wrong" fixes either fail to compile or
                    // silently drift behavior, and lazy rollback only kicks in at the next natural compile —
                    // so reject before writing.
                    if (p.Ok)
                    {
                        string fidelity = CacheFidelityIssue(p.Original, p.Fixed);
                        if (fidelity != null)
                        {
                            p.Ok = false;
                            p.Error = L.Tr("The AI fix's cache rewrite has a problem (", "AI 修复的缓存改写有问题（") + fidelity +
                                      L.Tr("); applying it would fail to compile or change behavior, so it was rejected. Please regenerate, or fix manually.", "），应用后会编译失败或改变行为，已拒绝。请重新生成，或手动修复。");
                        }
                    }

                    // Guard: FIXED discards the flagged expression entirely and replaces it with a
                    // [fabricated local variable] that was never declared (repro: GetComponent<Light>() in
                    // Update → model outputs `if (li)`, yet li was never declared). These dangling references
                    // (non-underscore) slip through IntroducedUndeclaredFields (which only checks _fields),
                    // and neither StatementIntegrity nor CacheFidelity fires (syntactically coherent, no ??=,
                    // no missing semicolon), so they would reach the "Apply" button — guaranteed compile
                    // failure AND an obvious rookie mistake that erodes user trust.
                    if (p.Ok)
                    {
                        var dangling = IntroducedDanglingLocals(p.Original, p.Fixed, full);
                        if (dangling.Count > 0)
                        {
                            p.Ok = false;
                            p.Error = L.Tr("The AI fix references undeclared variables (", "AI 修复引用了未声明的变量（") + string.Join("、", dangling) +
                                      L.Tr("); applying it would fail to compile, so it was rejected. Please regenerate, or fix manually.", "），应用后会编译失败，已拒绝。请重新生成，或手动修复。");
                        }
                    }

                    // Keep only using directives that are genuinely missing from the file — existing ones are not re-inserted, and the diff only shows truly new additions.
                    if (p.Usings != null && full != null)
                    {
                        var missing = MissingUsings(full, p.Usings);
                        p.Usings = missing.Count > 0 ? missing.ToArray() : null;
                    }
                }

                // Deterministic behavior hint: only flag ⚠ (unchecked by default in the review window)
                // for semantic traps that are known, statically identifiable, and non-obvious to non-experts.
                // No secondary LLM judgment — that would be fuzzy and badly calibrated (in practice it
                // misflagged standard ??= caching en masse, drowning real traps in noise), and burns extra
                // tokens per fix. Only hard patterns are recognized here: zero tokens, zero calibration,
                // zero false positives. Everything else is checked by default; the user reviews the diff.
                if (p.Ok && p.Locatable && !p.NoChange)
                {
                    p.RiskReason = BehaviorNote(p.Original, p.Fixed);
                    p.BehaviorRisk = p.RiskReason != null;
                }
                onDone(p);
            }, disableThinking: true);
        }

        /// <summary>
        /// Deterministic "behavior hint": returns a one-line explanation for known, statically-identifiable
        /// semantic traps that are easy for non-experts to overlook; returns null if none apply.
        /// Currently only recognizes FindObjectOfType→FindAnyObjectByType: the latter does not guarantee
        /// returning "the first" instance and has an indeterminate order, so with multiple instances a
        /// different object may be returned — the behavior-preserving migration is FindFirstObjectByType.
        /// This migration looks like a routine rename and is very easy to approve without reading the diff,
        /// hence it is flagged ⚠ and unchecked by default.
        /// Copy uses L.Tr to follow the UI language. Intentionally does not cover cache-type fixes (??=) —
        /// that is a standard optimization; "object may change" is a universal caveat, so flagging every
        /// case would just add noise.
        /// </summary>
        internal static string BehaviorNote(string original, string fixedText)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(fixedText)) return null;

            // Plural form: FindObjectsOfType→FindObjectsByType (the latter also requires passing FindObjectsSortMode, and does not sort by default → ordering changes).
            if (original.Contains("FindObjectsOfType") && fixedText.Contains("FindObjectsByType"))
                return L.Tr(
                    "FindObjectsByType does not sort by default (FindObjectsSortMode.None), unlike FindObjectsOfType; if your code relies on the returned order, pass FindObjectsSortMode.InstanceID or sort it yourself.",
                    "FindObjectsByType 默认不排序（FindObjectsSortMode.None），与 FindObjectsOfType 不同；若代码依赖返回顺序，请传 FindObjectsSortMode.InstanceID 或自行排序。");

            // Singular form: FindObjectOfType→FindAnyObjectByType (loses the "return the first" semantic).
            if (original.Contains("FindObjectOfType") && fixedText.Contains("FindAnyObjectByType"))
                return L.Tr(
                    "FindAnyObjectByType does not guarantee returning the 'first' instance like FindObjectOfType did; if multiple instances of this type can exist, use FindFirstObjectByType instead.",
                    "FindAnyObjectByType 不像 FindObjectOfType 那样保证返回「第一个」实例；若场景中可能存在多个该类型实例，请改用 FindFirstObjectByType。");

            return null;
        }

        /// <summary>
        /// Apply a fix: locate the original snippet by line and replace it → write the file → trigger compilation.
        /// A compile failure is auto-rolled back by PerfLintScriptFixVerifier.
        /// </summary>
        public static bool Apply(ScriptFixProposal p, out string message)
        {
            message = null;
            try
            {
                string full = SafeReadAllText(p.FilePath);
                if (full == null) { message = L.Tr("Failed to read the file.", "读取文件失败。"); return false; }

                var (at, len) = LocateRegion(full, p.Original, p.ExpectedLine);
                if (len <= 0) { message = L.Tr("Could not locate the original snippet in the file (it may have changed); please apply manually.", "未能在文件中定位原始片段（可能已改动），请手动应用。"); return false; }

                string fixedAdapted = AdaptLineEndings(p.Fixed, full);
                string updated = full.Substring(0, at) + fixedAdapted + full.Substring(at + len);

                // Cache-type fix: insert the field declaration deterministically at the top of the enclosing class body (not relying on the model to place it correctly).
                if (!string.IsNullOrEmpty(p.FieldDecl) && !FieldAlreadyDeclared(updated, p.FieldDecl))
                {
                    string withField = InsertFieldAtClassTop(updated, at, p.FieldDecl);
                    if (withField == null)
                    {
                        message = L.Tr("A new cache field is needed, but the class-body insertion point could not be located in the file; please apply manually.", "需要新增缓存字段，但未能在文件中定位类体插入位置，请手动应用。");
                        return false;
                    }
                    updated = withField;
                }

                // Rename/migration fixes may require namespaces not yet imported in the file (e.g. SceneManager) — insert missing using directives deterministically at the top of the file.
                if (p.Usings != null && p.Usings.Length > 0)
                    updated = EnsureUsings(updated, p.Usings);

                // Back up the original content and register it for verification, then write.
                // Note: compilation/domain reload is intentionally NOT triggered here — a domain reload
                // would clear the window and force a full 86-second re-scan.
                // After writing, the caller does an incremental re-scan of this file for an immediate
                // result refresh. Verification (compile + failure rollback) is delegated to Unity's next
                // natural compile, handled by PerfLintScriptFixVerifier via
                // assemblyCompilationFinished / DidReloadScripts.
                string backup = "Temp/PerfLint_backup_" + Guid.NewGuid().ToString("N") + ".txt";
                Directory.CreateDirectory("Temp");
                File.WriteAllText(Path.GetFullPath(backup), full);
                PerfLintScriptFixVerifier.BeginVerify(p.FilePath, backup);

                File.WriteAllText(Path.GetFullPath(p.FilePath), updated, new UTF8Encoding(false));

                // Schedule a debounced background compile-verification (enabled by default): fires silently after a few seconds, auto-rolls back on failure — no longer depends on the user switching focus or Auto Refresh.
                PerfLintFixCompileScheduler.RequestSoon();

                message = LlmSettings.AutoVerifyFix
                    ? L.Tr("Written and this file's warnings refreshed in place. It will be auto-verified in the background in a few seconds; a compile failure will auto-roll back (see Console).", "已写入并就地刷新该文件的告警。几秒后将后台自动校验，编译失败会自动回滚（见 Console）。")
                    : L.Tr("Written and this file's warnings refreshed in place. It will be verified on Unity's next compile; a compile failure will auto-roll back (see Console).", "已写入并就地刷新该文件的告警。Unity 下次编译时会校验，编译失败将自动回滚（见 Console）。");
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        // ── Parse ORIGINAL/FIXED ──
        internal static ScriptFixProposal Parse(string text)
        {
            var p = new ScriptFixProposal();
            if (string.IsNullOrEmpty(text))
            {
                p.Ok = false;
                p.Error = L.Tr("The model did not reply in the expected format. Raw reply:\n", "模型未按预期格式返回。原始回复：\n") + text;
                return p;
            }
            const string mo = "<<<ORIGINAL>>>", mf = "<<<FIXED>>>", mu = "<<<USINGS>>>", me = "<<<END>>>";
            int io = text.IndexOf(mo, StringComparison.Ordinal);
            int iff = text.IndexOf(mf, StringComparison.Ordinal);
            int ie = text.IndexOf(me, StringComparison.Ordinal);
            // <<<END>>> is optional: the model occasionally omits it (observed: a fully valid Camera.main
            // cache fix was rejected entirely just because END was missing).
            // When absent, use the end of the text as the terminator — as long as ORIGINAL and FIXED are
            // both present and in order, parse normally; don't discard a good fix for nothing.
            if (ie < 0) ie = text.Length;
            if (io < 0 || iff < 0 || !(io < iff && iff < ie))
            {
                p.Ok = false;
                p.Error = L.Tr("The model did not reply in the expected format. Raw reply:\n", "模型未按预期格式返回。原始回复：\n") + text;
                return p;
            }

            // Optional USINGS section (between FIXED and END): if present, FIXED ends right before it.
            int iu = text.IndexOf(mu, StringComparison.Ordinal);
            bool hasUsings = iu > iff && iu < ie;
            int fixedEnd = hasUsings ? iu : ie;

            p.Original = CleanBlock(text.Substring(io + mo.Length, iff - (io + mo.Length)));
            p.Fixed = CleanBlock(text.Substring(iff + mf.Length, fixedEnd - (iff + mf.Length)));
            if (hasUsings)
                p.Usings = ParseUsings(text.Substring(iu + mu.Length, ie - (iu + mu.Length)));
            p.Ok = p.Original.Length > 0;
            if (!p.Ok) { p.Error = L.Tr("The parsed original snippet is empty.", "解析出的原始片段为空。"); return p; }

            // Cache-type fix: infer the field type from FIXED's lazy assignment and let the tool inject the
            // declaration (not relying on the model to place it correctly);
            // also strip any field declaration line the model may have wrongly stuffed into the snippet.
            string fixedText = p.Fixed;
            p.FieldDecl = TrySynthesizeFieldDecl(ref fixedText);
            p.Fixed = fixedText;

            p.NoChange = NoWhitespace(p.Original) == NoWhitespace(p.Fixed);
            return p;
        }

        // ── Cache-type fix: synthesizing the field declaration and inserting it deterministically ──
        // In-place null-coalescing assignment (_f ??= EXPR): the preferred caching form, usable in both statement and expression positions (including inside method arguments).
        private static readonly Regex CoalesceInitRx = new Regex(
            @"(_\w+)\s*\?\?=\s*([^\n;]+)", RegexOptions.Compiled);
        // Backward-compat with the old form: lazy assignment if(_f==null) _f=EXPR; (statement position only).
        private static readonly Regex LazyInitRx = new Regex(
            @"if\s*\(\s*(_\w+)\s*==\s*null\s*\)\s*\1\s*=\s*([^;]+);", RegexOptions.Compiled);

        /// <summary>
        /// Infer the field type from FIXED's cache assignment (_f ??= EXPR) or if(_f==null) _f=EXPR; and return "private &lt;Type&gt; _f;".
        /// Also strip any declaration line for that field the model may have wrongly stuffed into the snippet (to avoid it being written into the method body as a statement and failing to compile).
        /// No cache assignment, or the type cannot be inferred → returns null (no declaration injected).
        /// </summary>
        private static string TrySynthesizeFieldDecl(ref string fixedText)
        {
            var m = CoalesceInitRx.Match(fixedText);
            if (!m.Success) m = LazyInitRx.Match(fixedText);
            if (!m.Success) return null;
            string field = m.Groups[1].Value;
            string rhs = m.Groups[2].Value.Trim();

            fixedText = StripFieldDeclarationLines(fixedText, field);

            string type = InferFieldType(rhs);
            return type == null ? null : "private " + type + " " + field + ";";
        }

        /// <summary>Strip standalone field declaration lines of the form "[modifiers] Type _field;" from the snippet (lazy-assignment/usage lines are unaffected).</summary>
        private static string StripFieldDeclarationLines(string fixedText, string field)
        {
            var declRx = new Regex(
                @"^\s*(?:(?:private|public|protected|internal|static|readonly)\s+)*[\w<>,\.\[\]]+\s+"
                + Regex.Escape(field) + @"\s*;\s*$");
            var keep = new List<string>();
            foreach (var line in fixedText.Replace("\r\n", "\n").Split('\n'))
                if (!declRx.IsMatch(line)) keep.Add(line);
            return string.Join("\n", keep);
        }

        /// <summary>Infer the field type from the lazy-assignment right-hand value (covers the common targets of cache-type rules); returns null if it cannot be inferred.</summary>
        internal static string InferFieldType(string rhs)
        {
            rhs = rhs.Trim();
            Match m;
            if ((m = Regex.Match(rhs, @"^new\s+([\w\.]+)")).Success) return m.Groups[1].Value;        // new WaitForSeconds(..)
            if (Regex.IsMatch(rhs, @"^Camera\s*\.\s*main\b")) return "Camera";                         // Camera.main (including (_cam ??= Camera.main).xxx)
            // Plural (returns an array)
            if ((m = Regex.Match(rhs, @"\b(?:GetComponents(?:InChildren|InParent)?|FindObjectsOfType|FindObjectsByType)\s*<\s*([\w\.]+)\s*>")).Success)
                return m.Groups[1].Value + "[]";
            // Singular
            if ((m = Regex.Match(rhs, @"\b(?:GetComponent(?:InChildren|InParent)?|FindObjectOfType|FindAnyObjectByType|FindFirstObjectByType)\s*<\s*([\w\.]+)\s*>")).Success)
                return m.Groups[1].Value;
            if (Regex.IsMatch(rhs, @"\bGameObject\s*\.\s*Find\s*\(")) return "GameObject";              // GameObject.Find(..)
            return null;
        }

        // ── Parsing and deterministic insertion of missing using directives ──
        /// <summary>Parse the USINGS section into an array of namespaces: one per line, tolerantly stripping the using keyword/semicolon/fences, keeping only namespaces of the form a.b.c.</summary>
        private static string[] ParseUsings(string block)
        {
            var list = new List<string>();
            foreach (var raw in (block ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("```")) continue;
                if (s.StartsWith("using ")) s = s.Substring(6).Trim(); // Tolerance: the model wrote "using X;"
                s = s.TrimEnd(';').Trim();
                if (s.Length == 0 || !Regex.IsMatch(s, @"^[\w\.]+$")) continue;
                if (!list.Contains(s)) list.Add(s);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>From the candidate namespaces, pick those the file does not yet have a using for (deduplicated).</summary>
        private static List<string> MissingUsings(string text, IEnumerable<string> namespaces)
        {
            var res = new List<string>();
            if (namespaces == null) return res;
            foreach (var ns in namespaces)
            {
                if (string.IsNullOrWhiteSpace(ns)) continue;
                string n = ns.Trim();
                if (Regex.IsMatch(text, @"(?m)^[ \t]*using\s+" + Regex.Escape(n) + @"\s*;")) continue;
                if (!res.Contains(n)) res.Add(n);
            }
            return res;
        }

        /// <summary>Insert the missing using directives after the last top-level using; if the file has no using, insert at the very top. Line-ending style follows the file.</summary>
        private static string EnsureUsings(string text, string[] namespaces)
        {
            var toAdd = MissingUsings(text, namespaces);
            if (toAdd.Count == 0) return text;

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string block = string.Join(nl, toAdd.ConvertAll(n => "using " + n + ";"));

            var us = Regex.Matches(text, @"(?m)^[ \t]*using\b[^;\n]*;");
            if (us.Count > 0)
            {
                var last = us[us.Count - 1];
                int at = last.Index + last.Length;
                return text.Substring(0, at) + nl + block + text.Substring(at);
            }
            return block + nl + text;
        }

        /// <summary>Whether the field is already declared in the file (to avoid injecting a duplicate declaration).</summary>
        private static bool FieldAlreadyDeclared(string text, string decl)
        {
            var name = Regex.Match(decl, @"(_\w+)\s*;");
            return name.Success && DeclaredInFile(text, name.Groups[1].Value);
        }

        /// <summary>Whether the file contains a field declaration of the form "&lt;Type&gt; _name;" or "&lt;Type&gt; _name =" (matches declarations only, not assignments/usages).</summary>
        private static bool DeclaredInFile(string text, string name) =>
            Regex.IsMatch(text, @"[\w>\]]\s+" + Regex.Escape(name) + @"\s*[;=]");

        // Underscore field references appearing in FIXED (excluding member access obj._x: not counted when the preceding char is a letter/digit/dot).
        private static readonly Regex UnderscoreFieldRx = new Regex(@"(?<![\w.])_\w+", RegexOptions.Compiled);

        /// <summary>
        /// Find underscore fields newly introduced by FIXED that have nowhere to be declared: they appear in FIXED
        /// but are neither in ORIGINAL, nor declared in the file, nor the cache field the tool will inject (synthesized).
        /// Writing such fields in is guaranteed to fail compilation (e.g. a _lastXxx memory field the model added on its own).
        /// </summary>
        private static List<string> IntroducedUndeclaredFields(string original, string fixedText, string fieldDecl, string fileText)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(fixedText)) return result;

            // First mask out comment/string contents, to avoid mistaking a "_xxx" inside a literal for a field reference.
            original = MaskNonCode(original ?? "");
            fixedText = MaskNonCode(fixedText);

            var inOriginal = new HashSet<string>();
            foreach (Match m in UnderscoreFieldRx.Matches(original)) inOriginal.Add(m.Value);

            string synthesized = null;
            if (!string.IsNullOrEmpty(fieldDecl))
            {
                var nm = Regex.Match(fieldDecl, @"(_\w+)\s*;");
                if (nm.Success) synthesized = nm.Groups[1].Value;
            }

            var seen = new HashSet<string>();
            foreach (Match m in UnderscoreFieldRx.Matches(fixedText))
            {
                string name = m.Value;
                if (!seen.Add(name)) continue;            // Report each name only once
                if (inOriginal.Contains(name)) continue;  // Already used in the original snippet → existing field
                if (name == synthesized) continue;         // The tool will inject the declaration
                if (fileText != null && DeclaredInFile(fileText, name)) continue; // Declared elsewhere in the file
                result.Add(name);
            }
            return result;
        }

        // ── Dangling local references: FIXED introduced non-underscore identifiers that cannot be resolved anywhere (guaranteed compile failure on apply) ──
        internal static readonly Regex IdentRx = new Regex(@"[A-Za-z_]\w*", RegexOptions.Compiled);

        // C# keywords + common contextual keywords (not treated as variable references).
        private static readonly HashSet<string> CsKeywords = new HashSet<string>
        {
            "abstract","as","async","await","base","bool","break","byte","case","catch","char","checked","class",
            "const","continue","decimal","default","delegate","do","double","dynamic","else","enum","event",
            "explicit","extern","false","finally","fixed","float","for","foreach","get","global","goto","if",
            "implicit","in","int","interface","internal","is","lock","long","nameof","namespace","new","null",
            "object","operator","out","override","params","private","protected","public","readonly","ref","return",
            "sbyte","sealed","set","short","sizeof","stackalloc","static","string","struct","switch","this","throw",
            "true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","value","var","virtual",
            "void","volatile","when","where","while","yield"
        };

        /// <summary>
        /// Find local variables FIXED [references out of thin air]: identifiers that start with a lowercase letter and are used as a value,
        /// yet are neither in ORIGINAL, nor declared within FIXED, nor appear anywhere else in the file under the same name. Typical bad case: replacing the
        /// whole `gameObject.GetComponent&lt;Light&gt;()` with `li`, where li was never declared → guaranteed compile failure on apply.
        /// More conservative than IntroducedUndeclaredFields (falls back to "does the word appear anywhere in the file", treating parameters/loop variables/locals elsewhere as known),
        /// and only recognizes lowercase-initial names (avoiding PascalCase types/enums/constants), pushing false positives to nearly zero; the ones that slip through (uppercase-initial dangling references) are left to compile rollback.
        /// </summary>
        internal static List<string> IntroducedDanglingLocals(string original, string fixedText, string fileText)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(fixedText)) return result;

            string oMasked = MaskNonCode(original ?? "");
            string fMasked = MaskNonCode(fixedText);

            var inOriginal = new HashSet<string>();
            foreach (Match m in IdentRx.Matches(oMasked)) inOriginal.Add(m.Value);

            var seen = new HashSet<string>();
            foreach (Match m in IdentRx.Matches(fMasked))
            {
                string name = m.Value;
                if (name.Length == 0 || !char.IsLower(name[0])) continue; // Lowercase-initial only
                if (CsKeywords.Contains(name)) continue;
                if (inOriginal.Contains(name)) continue;
                if (!seen.Add(name)) continue;                            // Report each name only once

                int s = m.Index, e = m.Index + m.Length;
                // Member access obj.name → resolved by obj's type, skip.
                int prev = s - 1;
                while (prev >= 0 && char.IsWhiteSpace(fMasked[prev])) prev--;
                if (prev >= 0 && fMasked[prev] == '.') continue;
                // Method call name(...) / named argument or label name: → not a value reference, skip.
                int next = e;
                while (next < fMasked.Length && char.IsWhiteSpace(fMasked[next])) next++;
                if (next < fMasked.Length && (fMasked[next] == '(' || fMasked[next] == ':')) continue;

                if (DeclaredInFixed(fMasked, name)) continue;             // A local newly declared within FIXED itself (var/Type/out/foreach)
                if (fileText != null && Regex.IsMatch(fileText, @"\b" + Regex.Escape(name) + @"\b")) continue; // Appears elsewhere in the file → treated as known

                result.Add(name);
            }
            return result;
        }

        /// <summary>Whether the FIXED snippet declares a local named name (var name= / Type name= / Type name; / out var name / foreach(.. name in)).</summary>
        private static bool DeclaredInFixed(string maskedFixed, string name) =>
            Regex.IsMatch(maskedFixed,
                @"(?:\bvar\b|\b[\w<>,\.\[\]]+)\s+" + Regex.Escape(name) + @"\b\s*(?:[=;,)]|\bin\b)");

        // ── Statement structural integrity: prevent FIXED from truncating a complete statement into a broken fragment (guaranteed compile failure on apply) ──
        private static readonly string[] StatementLeads = { "yield return", "return", "throw" };

        /// <summary>
        /// Whether FIXED broke ORIGINAL's statement integrity. Returns the reason text; null if there is no problem.
        /// Only performs high-certainty, low-false-positive structural checks: trailing semicolon on the last line, leading statement keyword, bracket balance.
        /// (A legitimate (_f ??= EXPR) wrap preserves the semicolon and leading word verbatim, so it will not be misflagged.)
        /// </summary>
        internal static string StatementIntegrityIssue(string original, string fixedText)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(fixedText)) return null;

            string oFirst = FirstNonBlank(original);
            string oLast = LastNonBlank(original), fLast = LastNonBlank(fixedText);
            if (oLast == null || fLast == null) return null;

            // 1) ORIGINAL's last line is a complete statement (ends with ;) → FIXED's last line must also end with ; } {, otherwise the statement was truncated (missing semicolon).
            if (oLast.EndsWith(";") && !(fLast.EndsWith(";") || fLast.EndsWith("}") || fLast.EndsWith("{")))
                return L.Tr("the last line is not a complete statement: ", "末行不是完整语句：") + fLast;

            // 2) ORIGINAL starts with a statement keyword (yield return / return / throw) → FIXED must not drop it entirely.
            string maskedFixed = MaskNonCode(fixedText);
            foreach (var kw in StatementLeads)
                if (oFirst != null && oFirst.StartsWith(kw, StringComparison.Ordinal)
                    && !Regex.IsMatch(maskedFixed, @"(?<![\w])" + Regex.Escape(kw) + @"\b"))
                    return L.Tr("lost the statement keyword: ", "丢失了语句关键字：") + kw;

            // 3) FIXED's own brackets must balance (after removing comments/strings).
            if (!BracketsBalanced(maskedFixed))
                return L.Tr("brackets are not balanced", "括号不配平");

            return null;
        }

        private static string FirstNonBlank(string s)
        {
            foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) return line.Trim();
            return null;
        }

        private static string LastNonBlank(string s)
        {
            string found = null;
            foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) found = line.Trim();
            return found;
        }

        // ── Cache fidelity: the EXPR in (_field ??= EXPR) must be a real expression from the original, and we don't cache things that "evaluate differently each time" ──
        // Matches the opening of a cache wrap "(_field ??=", used to locate the '(' and the start of the cache expression after '??='.
        private static readonly Regex WrapOpenRx = new Regex(@"\(\s*_\w+\s*\?\?=", RegexOptions.Compiled);
        // Caching these expressions that "may evaluate differently each time" would freeze the first value and change behavior.
        private static readonly Regex NonCacheableRx = new Regex(@"\b(?:Random|Time)\s*\.", RegexOptions.Compiled);

        /// <summary>
        /// Check each (_field ??= EXPR) cache in FIXED: EXPR must be an expression that exists verbatim in ORIGINAL (compared with whitespace stripped),
        /// and must not be a Random./Time.-style expression that evaluates differently each time. Returns the reason; null if there is no problem.
        /// If there is no ??= cache (pure rename, etc.), it passes directly.
        /// </summary>
        internal static string CacheFidelityIssue(string original, string fixedText)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(fixedText)) return null;

            string maskedFixed = MaskNonCode(fixedText);
            string origNoWs = NoWhitespace(MaskNonCode(original));

            foreach (Match m in WrapOpenRx.Matches(maskedFixed))
            {
                int open = m.Index;                    // Position of '('
                int rhsStart = m.Index + m.Length;     // After '??=' (start of the cache expression)
                int close = MatchingParen(maskedFixed, open);
                if (close < 0 || close <= rhsStart) continue; // Unmatched parenthesis, leave it to BracketsBalanced
                // Indices map 1:1 to the original (MaskNonCode is equal-length), so take the cache expression text from the original.
                string rhs = fixedText.Substring(rhsStart, close - rhsStart);
                string rhsNoWs = NoWhitespace(MaskNonCode(rhs));
                if (rhsNoWs.Length == 0) continue;

                if (NonCacheableRx.IsMatch(rhs))
                    return L.Tr("the cached expression \"", "被缓存的表达式 \"") + rhs.Trim() + L.Tr("\" may evaluate differently each time, so caching it would change behavior", "\" 每次取值可能不同，缓存会改变行为");
                if (!origNoWs.Contains(rhsNoWs))
                    return L.Tr("the cached expression \"", "被缓存的表达式 \"") + rhs.Trim() + L.Tr("\" is not an expression from the original code (likely rewritten arguments or fabricated members)", "\" 不是原代码里的表达式（疑似改写参数或虚构成员）");
            }
            return null;
        }

        /// <summary>Match parentheses starting from the '(' at openIdx, returning the index of its matching ')'; returns -1 if unmatched.</summary>
        private static int MatchingParen(string s, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>In code with comments/strings already masked out, whether ()[]{} are balanced. (Shared with MigrateService's whole-file guards.)</summary>
        internal static bool BracketsBalanced(string maskedCode)
        {
            int paren = 0, brack = 0, brace = 0;
            foreach (char c in maskedCode)
            {
                switch (c)
                {
                    case '(': paren++; break;
                    case ')': if (--paren < 0) return false; break;
                    case '[': brack++; break;
                    case ']': if (--brack < 0) return false; break;
                    case '{': brace++; break;
                    case '}': if (--brace < 0) return false; break;
                }
            }
            return paren == 0 && brack == 0 && brace == 0;
        }

        /// <summary>
        /// Insert the field declaration at the top of the class body of [the class that contains nearPos] (right after the class body's '{').
        /// First mask comments/strings across the whole file, then identify classes on the masked text and do brace matching,
        /// taking the innermost class that actually contains nearPos — class keywords or braces appearing in comments/strings never interfere.
        /// Returns null if it cannot be located (the caller aborts accordingly and does not write).
        /// </summary>
        private static string InsertFieldAtClassTop(string text, int nearPos, string decl)
        {
            string masked = MaskNonCode(text);
            int braceEnd = EnclosingClassBraceEnd(masked, nearPos); // Position right after the class body's '{'
            if (braceEnd < 0) return null;

            // Indentation = the indentation of the line containing the class body's '{' + one level (4 spaces).
            int ls = text.LastIndexOf('\n', Math.Min(braceEnd - 1, text.Length - 1)) + 1;
            var indent = new StringBuilder();
            for (int i = ls; i < text.Length && (text[i] == ' ' || text[i] == '\t'); i++) indent.Append(text[i]);
            indent.Append("    ");

            string insertion = AdaptLineEndings("\n" + indent + decl, text);
            return text.Substring(0, braceEnd) + insertion + text.Substring(braceEnd);
        }

        /// <summary>
        /// On the masked text, find the class body's opening '{' of the innermost class/struct containing pos, returning the position just after it; returns -1 if none found.
        /// Innermost = among classes satisfying bodyOpen &lt; pos ≤ bodyClose, the one with the largest bodyOpen (a nested class is declared later, so its '{' comes further along).
        /// </summary>
        private static int EnclosingClassBraceEnd(string masked, int pos)
        {
            int best = -1;
            foreach (Match m in Regex.Matches(masked, @"\b(?:class|struct)\s+\w+"))
            {
                int open = masked.IndexOf('{', m.Index);
                if (open < 0 || open >= pos) continue;
                int depth = 0, close = masked.Length;
                for (int i = open; i < masked.Length; i++)
                {
                    if (masked[i] == '{') depth++;
                    else if (masked[i] == '}' && --depth == 0) { close = i; break; }
                }
                if (pos <= close && open > best) best = open;
            }
            return best < 0 ? -1 : best + 1;
        }

        /// <summary>
        /// Return a copy of equal length to text, replacing the contents (including delimiters) of comments, strings, and character literals with spaces (newlines preserved).
        /// Covers: line/block comments, regular strings (\ escapes), verbatim strings @"…" ("" escapes), interpolated strings $"…" (their internally balanced {} are masked too, so they don't affect class-body brace matching).
        /// This way the masked text retains only the "code" braces and class keywords, with indices mapping 1:1 to the original.
        /// (Shared with MigrateService's whole-file guards.)
        /// </summary>
        internal static string MaskNonCode(string text)
        {
            var a = text.ToCharArray();
            int n = a.Length, i = 0;
            while (i < n)
            {
                char c = a[i];
                if (c == '/' && i + 1 < n && a[i + 1] == '/')
                {
                    while (i < n && a[i] != '\n') a[i++] = ' ';
                }
                else if (c == '/' && i + 1 < n && a[i + 1] == '*')
                {
                    a[i++] = ' '; a[i++] = ' ';
                    while (i < n)
                    {
                        bool end = i + 1 < n && a[i] == '*' && a[i + 1] == '/';
                        if (a[i] != '\n') a[i] = ' ';
                        i++;
                        if (end) { if (i < n) a[i] = ' '; i++; break; }
                    }
                }
                else if (c == '"')
                {
                    bool verbatim = (i >= 1 && a[i - 1] == '@')
                                    || (i >= 2 && a[i - 1] == '$' && a[i - 2] == '@');
                    a[i++] = ' ';
                    while (i < n)
                    {
                        if (!verbatim && a[i] == '\\' && i + 1 < n) { a[i] = ' '; a[i + 1] = ' '; i += 2; continue; }
                        if (a[i] == '"')
                        {
                            if (verbatim && i + 1 < n && a[i + 1] == '"') { a[i] = ' '; a[i + 1] = ' '; i += 2; continue; }
                            a[i++] = ' '; break;
                        }
                        if (a[i] != '\n') a[i] = ' ';
                        i++;
                    }
                }
                else if (c == '\'')
                {
                    a[i++] = ' ';
                    while (i < n)
                    {
                        if (a[i] == '\\' && i + 1 < n) { a[i] = ' '; a[i + 1] = ' '; i += 2; continue; }
                        if (a[i] == '\'') { a[i++] = ' '; break; }
                        if (a[i] != '\n') a[i] = ' ';
                        i++;
                    }
                }
                else i++;
            }
            return new string(a);
        }

        /// <summary>Remove the leading/trailing blank lines and ``` fence lines after the marker; **preserve code indentation** (normalized to \n line endings).</summary>
        private static string CleanBlock(string s)
        {
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = new List<string>(s.Split('\n'));
            // Remove leading blank lines
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            // Remove trailing blank lines
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            // Remove ``` fence lines
            if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```")) lines.RemoveAt(0);
            if (lines.Count > 0 && lines[lines.Count - 1].TrimStart().StartsWith("```")) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines);
        }

        // ── Line-tolerant matching (ignoring per-line leading/trailing whitespace and CRLF/LF differences) ──
        // anchorLine (1-based): when the file has multiple content-identical matches, pick the one whose start line is closest to anchorLine —
        // essential for batch multi-fix on the same file (duplicate lines are common, especially in third-party code); otherwise "first match" would make all fixes collide at the same spot.
        // When anchorLine ≤ 0, keep the old behavior (take the first match).
        internal static (int at, int len) LocateRegion(string full, string originalLf, int anchorLine = 0)
        {
            var orig = originalLf.Split('\n');
            int on = orig.Length;
            while (on > 0 && orig[on - 1].Trim().Length == 0) on--;
            if (on == 0) return (0, 0);

            var fileLines = SplitWithIndex(full);
            int bestStart = -1, bestLen = 0, bestDist = int.MaxValue;
            for (int i = 0; i + on <= fileLines.Count; i++)
            {
                bool ok = true;
                for (int k = 0; k < on; k++)
                {
                    if (fileLines[i + k].text.Trim() != orig[k].Trim()) { ok = false; break; }
                }
                if (!ok) continue;

                int startIdx = fileLines[i].start;
                int endIdx = fileLines[i + on - 1].contentEnd;
                int matchLen = endIdx - startIdx;

                if (anchorLine <= 0) return (startIdx, matchLen); // No anchor: take the first match (old behavior)

                int dist = Math.Abs((i + 1) - anchorLine); // i is the 0-based line index → line number i+1
                if (dist < bestDist) { bestDist = dist; bestStart = startIdx; bestLen = matchLen; }
            }
            return bestStart < 0 ? (0, 0) : (bestStart, bestLen);
        }

        private struct LineInfo { public string text; public int start; public int contentEnd; }

        private static List<LineInfo> SplitWithIndex(string s)
        {
            var res = new List<LineInfo>();
            int i = 0, n = s.Length;
            while (i <= n)
            {
                int nl = s.IndexOf('\n', i);
                int lineEnd = nl < 0 ? n : nl;
                int contentEnd = lineEnd;
                if (contentEnd > i && s[contentEnd - 1] == '\r') contentEnd--;
                res.Add(new LineInfo { text = s.Substring(i, contentEnd - i), start = i, contentEnd = contentEnd });
                if (nl < 0) break;
                i = nl + 1;
            }
            return res;
        }

        private static string AdaptLineEndings(string snippetLf, string full)
        {
            string s = snippetLf.Replace("\r\n", "\n").Replace("\r", "\n");
            return full.Contains("\r\n") ? s.Replace("\n", "\r\n") : s;
        }

        private static string NoWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string[] ReadLines(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? File.ReadAllLines(full) : null;
            }
            catch { return null; }
        }

        private static string SafeReadAllText(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch { return null; }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
