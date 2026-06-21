#if PERFLINT_ROSLYN
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using PerfLint.Core;
using UnityEditor;

namespace PerfLint.Scripting
{
    /// <summary>
    /// P0 script GC / per-frame allocation diagnostics (Roslyn syntax analysis). Compiled and
    /// auto-discovered by ScanRunner only when PERFLINT_ROSLYN is defined and the project contains
    /// the Microsoft.CodeAnalysis(.CSharp) DLLs.
    /// Scans runtime scripts only, skipping the Editor directory (not part of the build). See
    /// PerFrameAllocationWalker for rule details.
    /// No automatic fix is provided for now (rewriting code is high-risk; only locating and advice are given).
    /// </summary>
    public sealed class ScriptGcScanner : IScanner, IFileScanner
    {
        public string Name => "Script GC / Per-Frame Allocations (Roslyn)";
        public Domain Domain => Domain.Performance;

        public bool Handles(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".cs")) return false;
            return !assetPath.Replace('\\', '/').Contains("/Editor/"); // runtime scripts only, skip the Editor directory
        }

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var guids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                foreach (var f in ScanFile(path, context)) yield return f;
            }
        }

        public IEnumerable<Finding> ScanFile(string assetPath, ScanContext context)
        {
            if (!Handles(assetPath)) yield break;

            string source = ReadAll(assetPath);
            if (string.IsNullOrEmpty(source)) yield break;

            bool hasLinq = source.Contains("using System.Linq");
            var tree = CSharpSyntaxTree.ParseText(source);
            var walker = new PerFrameAllocationWalker(hasLinq);
            walker.Visit(tree.GetRoot());

            foreach (var issue in walker.Issues)
            {
                string capturedPath = assetPath;
                int capturedLine = issue.Line;
                // Report-only rules (AllowAiFix=false, e.g. GC004 memory leak) carry no code location → AiFixable=false, no AI Fix button shown.
                // Locate uses a standalone ping (independent of CodeFile/CodeLine), so navigation is unaffected.
                yield return new Finding(
                    ruleId: issue.RuleId,
                    domain: Domain.Performance,
                    severity: issue.Severity,
                    title: issue.Title,
                    detail: issue.Detail,
                    targetPath: $"{assetPath}:{issue.Line}",
                    ping: () => OpenAt(capturedPath, capturedLine),
                    codeFile: issue.AllowAiFix ? capturedPath : null,
                    codeLine: issue.AllowAiFix ? capturedLine : 0);
            }
        }

        private static string ReadAll(string assetPath)
        {
            try
            {
                var full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void OpenAt(string path, int line)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj == null) return;
            EditorGUIUtility.PingObject(obj);
            AssetDatabase.OpenAsset(obj, line); // open in the external editor/IDE and jump to the line
        }
    }
}
#endif
