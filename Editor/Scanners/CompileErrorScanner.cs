using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Turns captured C# compiler errors (CompileErrorCollector) into findings — the universal "project won't
    /// compile after upgrading" detection that the curated MIG.* list can't keep up with (docs/compile-error-
    /// ingestion-plan.md). Grouped per FILE, not per error: one bad using can spray hundreds of CS0246s, and a
    /// file is also the AI Migrate unit, so file granularity is both readable and actionable.
    ///   MIG.CompileError        — a script under Assets/ fails to compile (per file, Critical, AI Migrate-able).
    ///   MIG.CompileErrorPackage — errors in files the user can't edit (Packages/…), one summary finding.
    ///   MIG.CompileErrorPending — compilation failed but no details were captured yet (cold-start blind spot).
    /// Deliberately NO codeFile/codeLine on these findings: that would light up the snippet-level AI Fix, which is
    /// exactly the wrong tool here (the WWW lesson) — repair goes through the whole-file generic recipe instead.
    /// </summary>
    public sealed class CompileErrorScanner : IScanner
    {
        public string Name => "Compile Errors";
        public Domain Domain => Domain.Migration;

        private const int MaxFilesReported = 50;   // a hosed project can break hundreds of files; cap the list
        private const int MaxErrorsQuoted = 5;     // per-file detail quotes the first few errors

        public IEnumerable<Finding> Scan(ScanContext context)
            => BuildFindings(CompileErrorCollector.Snapshot(), EditorUtility.scriptCompilationFailed);

        /// <summary>Pure logic (unit-testable): captured errors + the compilation-failed flag → findings.</summary>
        internal static IEnumerable<Finding> BuildFindings(IReadOnlyList<CollectedError> errors, bool compilationFailed)
        {
            errors = errors ?? Array.Empty<CollectedError>();

            // Blind-spot degradation: the editor says compilation failed but nothing was captured (first-ever
            // compile happened before any managed subscriber existed). Honest pointer instead of silence.
            if (errors.Count == 0)
            {
                if (compilationFailed)
                {
                    yield return new Finding(
                        ruleId: "MIG.CompileErrorPending",
                        domain: Domain.Migration,
                        severity: Severity.Critical,
                        title: L.Tr("Project fails to compile (details pending)", "项目编译失败（详情待捕获）"),
                        detail: L.Tr(
                            "Unity reports script compilation failed, but PerfLint hasn't captured the error details yet — " +
                            "that happens when the very first compile finished before PerfLint loaded. Trigger a recompile " +
                            "(save any script, or right-click a script → Reimport), then Scan again to get per-file findings.",
                            "Unity 报告脚本编译失败，但 PerfLint 尚未捕获错误详情——这发生在首次编译早于 PerfLint 加载完成时。" +
                            "触发一次重编译（保存任意脚本，或右键脚本 → Reimport），再重新扫描即可得到逐文件的诊断。"),
                        targetPath: null);
                }
                yield break;
            }

            // Assets/ files → one finding per file (the AI Migrate unit). Everything else → one summary.
            var byFile = new Dictionary<string, List<CollectedError>>(StringComparer.OrdinalIgnoreCase);
            var external = new List<CollectedError>();
            foreach (var e in errors)
            {
                if (e == null) continue;
                if (!string.IsNullOrEmpty(e.file) && e.file.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    && !ScannerUtil.IsPerfLintOwnAsset(e.file))
                {
                    if (!byFile.TryGetValue(e.file, out var list)) byFile[e.file] = list = new List<CollectedError>();
                    list.Add(e);
                }
                else external.Add(e);
            }

            int reported = 0;
            foreach (var kv in byFile.OrderByDescending(k => k.Value.Count))
            {
                if (reported++ >= MaxFilesReported)
                {
                    int remaining = byFile.Count - MaxFilesReported;
                    yield return new Finding(
                        ruleId: "MIG.CompileError",
                        domain: Domain.Migration,
                        severity: Severity.Critical,
                        title: L.Tr($"…and {remaining} more script(s) fail to compile", $"…另有 {remaining} 个脚本编译失败"),
                        groupTitle: GroupTitle(),
                        detail: L.Tr("Fix the files above first — the list refreshes as compilation progresses.",
                                     "先修复上面列出的文件——列表会随编译进展刷新。"),
                        targetPath: null);
                    break;
                }

                string file = kv.Key;
                var errs = kv.Value;
                var first = errs[0];
                string cap = file;
                int capLine = Math.Max(1, first.line);

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < errs.Count && i < MaxErrorsQuoted; i++)
                    sb.Append("• ").Append(L.Tr("line ", "第 ")).Append(errs[i].line).Append(L.Tr(": ", " 行：")).Append(errs[i].message).Append('\n');
                if (errs.Count > MaxErrorsQuoted)
                    sb.Append(L.Tr($"…and {errs.Count - MaxErrorsQuoted} more in this file.\n", $"…此文件还有 {errs.Count - MaxErrorsQuoted} 条。\n"));
                string quoted = sb.ToString();

                yield return new Finding(
                    ruleId: "MIG.CompileError",
                    domain: Domain.Migration,
                    severity: Severity.Critical,
                    title: L.Tr($"Script fails to compile: '{Path.GetFileName(file)}' ({errs.Count} error(s))",
                                $"脚本编译失败：'{Path.GetFileName(file)}'（{errs.Count} 个错误）"),
                    groupTitle: GroupTitle(),
                    detail: L.Tr(
                        quoted + "This blocks the whole project from compiling. Typical after a Unity upgrade: APIs removed or renamed " +
                        "between versions. Use AI Migrate to rewrite this file against the errors above, or fix it manually — " +
                        "fixing one file can surface follow-up errors in files that call into it; rescan after each fix.",
                        quoted + "它会阻塞整个项目的编译。Unity 升级后常见：API 在版本间被移除或改名。" +
                        "可用 AI Migrate 按上述错误整体重写此文件，或手动修复——修好一个文件后，调用它的文件可能暴露后续错误；每修一个建议重扫。"),
                    targetPath: $"{file}:{capLine}",
                    ping: () => OpenAt(cap, capLine));
            }

            if (external.Count > 0)
            {
                var sample = external[0];
                yield return new Finding(
                    ruleId: "MIG.CompileErrorPackage",
                    domain: Domain.Migration,
                    severity: Severity.Critical,
                    title: L.Tr($"Compile errors outside Assets/ ({external.Count})", $"Assets/ 之外的编译错误（{external.Count}）"),
                    groupTitle: GroupTitle(),
                    detail: L.Tr(
                        $"{external.Count} error(s) are in files you can't edit in place (packages / generated code), e.g.: {sample.message} ({sample.file}:{sample.line}). " +
                        "Package code failing to compile is usually a version mismatch — the package is too old or too new for this Unity. " +
                        "Change the package version in the Package Manager, or remove the package if it's no longer maintained.",
                        $"{external.Count} 条错误位于无法就地编辑的文件（包 / 生成代码），例如：{sample.message}（{sample.file}:{sample.line}）。" +
                        "包代码编译失败通常是版本不匹配——该包对当前 Unity 太旧或太新。" +
                        "在 Package Manager 里更换包版本；若包已停止维护，考虑移除。"),
                    targetPath: null);
            }
        }

        private static string GroupTitle() =>
            L.Tr("Scripts fail to compile (project blocked)", "脚本编译失败（项目被阻塞）");

        private static void OpenAt(string path, int line)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj == null) return;
            EditorGUIUtility.PingObject(obj);
            AssetDatabase.OpenAsset(obj, line);
        }
    }
}
