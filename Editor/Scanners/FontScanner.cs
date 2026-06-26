using System.Collections.Generic;
using System.IO;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Font asset diagnostics. Rules:
    ///   PERF.FONT001 — A very large source font file (&gt;= 10 MB). Each referenced dynamic font ships its whole file in
    ///                  the build and stays resident; oversized fonts are usually full CJK/Unicode sets that can be
    ///                  subset to the glyphs actually used. Info / report-only (subsetting is an external-tooling step,
    ///                  not a safe in-editor auto-fix), and Info severity because large CJK fonts can be legitimate.
    /// Deterministic (on-disk file size) and low-cost — no asset is loaded.
    /// </summary>
    public sealed class FontScanner : IScanner
    {
        public string Name => "Font Assets";
        public Domain Domain => Domain.Performance;

        // 10 MB matches the mobile-memory checklist's "a single font > 10MB is large". Conservative: a full CJK font is
        // typically 5-20 MB, so this only flags the genuinely heavy ones rather than every CJK font.
        private const long LargeFontBytes = 10L * 1024 * 1024;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var guids = AssetDatabase.FindAssets("t:Font", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                long bytes = ScannerUtil.FileSizeBytes(path);
                if (!IsOversized(bytes)) continue;

                string file = Path.GetFileName(path);
                string size = ScannerUtil.Human(bytes);
                yield return new Finding(
                    ruleId: "PERF.FONT001",
                    domain: Domain.Performance,
                    severity: Severity.Info,
                    title: L.Tr($"Large font file ({size})", $"超大字体文件（{size}）"),
                    detail: L.Tr($"'{file}' is {size}. A referenced dynamic font ships its entire file in the build and stays resident in memory; " +
                            "an oversized font is almost always a full CJK/Unicode set. Subset it to the glyphs your game actually uses " +
                            "(font-subsetting tools, or TextMeshPro's static font asset with a fixed character set) to cut both build size and memory. " +
                            "If you genuinely need the full character set (e.g. user-generated text in many languages), this is expected.",
                            $"'{file}' 有 {size}。被引用的动态字体会把整个文件打进包、并常驻内存；" +
                            "字体过大几乎都是因为是完整的 CJK/Unicode 字符集。建议按游戏实际用到的字形做子集化" +
                            "（字体瘦身工具，或用 TextMeshPro 固定字符集的静态字体资源），可同时减小包体与内存。" +
                            "若确实需要完整字符集（如多语言的用户生成文本），则属正常。"),
                    targetPath: path,
                    ping: () => ScannerUtil.PingAsset(path));
            }
        }

        /// <summary>The PERF.FONT001 threshold decision as a pure predicate, so it is deterministically unit-testable
        /// without needing a real &gt;10 MB font asset on disk (generating a valid large font is impractical in tests).</summary>
        internal static bool IsOversized(long bytes) => bytes >= LargeFontBytes;
    }
}
