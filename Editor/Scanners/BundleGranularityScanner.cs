using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Pure bundle-packing heuristics shared by the granularity scanner (below) and the optional Addressables module
    /// (AACOMP001 uses <see cref="IsRemoteLoadPath"/>). Kept in the main assembly so the decision logic is unit-testable
    /// in batchmode even though the Addressables package is absent from the test project.
    /// </summary>
    public static class BundlePacking
    {
        /// <summary>A load path that downloads (http/https/custom scheme) — anything with a URI scheme separator.</summary>
        public static bool IsRemoteLoadPath(string loadPath)
            => !string.IsNullOrEmpty(loadPath) && loadPath.Contains("://");

        /// <summary>Bundles below this are "tiny": header/metadata becomes a meaningful fraction and every loaded bundle costs a file handle.</summary>
        public const long TinyBytes = 512 * 1024;

        /// <summary>Mobile reference band upper end is 2–5MB; only flag bundles well beyond it to stay conservative.</summary>
        public const long OversizedBytes = 50L * 1024 * 1024;

        /// <summary>The tiny-bundle anti-pattern needs scale to matter — a handful of small bundles is normal.</summary>
        public const int MinBundlesForTinyPattern = 20;

        /// <summary>
        /// Whether a built bundle file belongs to PerfLint's own "PerfLint Shared" dedup group. Addressables names
        /// bundle files "&lt;groupname-lowercased-spaces-stripped&gt;_assets_…", so the group yields a "perflintshared"
        /// prefix. Used so AAGRAN001 can say "this fragmentation comes from the dedup extraction (Pack Separately)"
        /// instead of appearing to contradict PerfLint's own one-click action.
        /// </summary>
        public static bool IsPerfLintSharedBundle(string fileName)
            => !string.IsNullOrEmpty(fileName)
            && fileName.StartsWith("perflintshared", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parses one result row of the official Addressables "Check Duplicate Bundle Dependencies" rule.
        /// Known format (Addressables 1.16–1.22): "Check Duplicate Bundle Dependencies:&lt;Group&gt;:&lt;BundleFile&gt;:&lt;AssetPath&gt;"
        /// — rule name (no ':'), group, bundle file, then the asset path. Defensive: the asset path is found by scanning
        /// from the RIGHT for the first segment starting with Assets/ or Packages/ (tolerates paths containing ':' and
        /// minor format drift); bundleFile is the segment before it when present, else null (older 3-segment forms).
        /// Lives in the main assembly (pure string logic) so it is unit-testable in batchmode — the Addressables module
        /// that uses it has no test coverage there (test project carries no Addressables package).
        ///
        /// The bundleFile segment matters: aggregating duplicates by GROUP alone silently drops same-group
        /// cross-bundle duplication — e.g. a per-scene-bundle group baking one font copy into each of 74 scene bundles
        /// (a real 1.16GB case) has group-count 1 and would vanish from the report.
        /// </summary>
        /// <summary>
        /// Tolerant asset-path extraction for official Analyze rules whose row format we don't fully control
        /// (e.g. CheckResourcesDupeDependencies): split on ':', scan from the RIGHT for the first segment that starts
        /// with Assets/ or Packages/, and rejoin from there (paths may themselves contain ':'). Returns false for
        /// summary / no-issue rows. Main assembly for batchmode testability.
        /// </summary>
        public static bool TryExtractAssetPath(string resultName, out string assetPath)
        {
            assetPath = null;
            if (string.IsNullOrEmpty(resultName)) return false;
            var parts = resultName.Split(':');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (!parts[i].StartsWith("Assets/") && !parts[i].StartsWith("Packages/")) continue;
                assetPath = string.Join(":", parts.Skip(i));
                return true;
            }
            return false;
        }

        public static bool TryParseDupeResult(string resultName, out string group, out string bundleFile, out string assetPath)
        {
            group = null;
            bundleFile = null;
            assetPath = null;
            if (string.IsNullOrEmpty(resultName)) return false;

            var parts = resultName.Split(':');
            if (parts.Length < 3) return false; // need at least ruleName:group:...:assetPath

            for (int i = parts.Length - 1; i >= 2; i--)
            {
                if (!parts[i].StartsWith("Assets/") && !parts[i].StartsWith("Packages/")) continue;
                assetPath = string.Join(":", parts.Skip(i)); // in case the path itself contains ':'
                group = parts[1];
                bundleFile = i >= 3 ? parts[i - 1] : null;
                return !string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(assetPath);
            }
            return false;
        }

        public sealed class GranularityStats
        {
            public int Total;
            public int Tiny;                                  // < TinyBytes
            public long MedianBytes;
            public List<string> OversizedNames = new List<string>(); // > OversizedBytes, largest first, capped by caller display

            /// <summary>Fragmentation: at scale AND the majority of bundles are tiny (per-bundle header overhead + OS file-handle limits).</summary>
            public bool TinyAntiPattern => Total >= MinBundlesForTinyPattern && Tiny * 2 >= Total;

            public bool ShouldReport => TinyAntiPattern || OversizedNames.Count > 0;
        }

        /// <summary>Evaluates built-bundle size distribution. Pure — no filesystem access.</summary>
        public static GranularityStats Evaluate(IReadOnlyList<KeyValuePair<string, long>> bundles)
        {
            var stats = new GranularityStats();
            if (bundles == null || bundles.Count == 0) return stats;

            stats.Total = bundles.Count;
            var sizes = bundles.Select(b => b.Value).OrderBy(s => s).ToList();
            stats.MedianBytes = sizes[sizes.Count / 2];
            stats.Tiny = sizes.Count(s => s < TinyBytes);
            stats.OversizedNames = bundles.Where(b => b.Value > OversizedBytes)
                .OrderByDescending(b => b.Value)
                .Select(b => $"{b.Key} (~{ScannerUtil.Human(b.Value)})")
                .ToList();
            return stats;
        }
    }

    /// <summary>
    /// Assets domain: bundle granularity check against the LAST Addressables build output.
    ///   ASSET.AAGRAN001 — the built bundle size distribution shows fragmentation (≥20 bundles and most under 512KB:
    ///     header overhead, OS file-handle limits, handle churn battery cost) and/or oversized bundles (>50MB, far
    ///     beyond the 2–5MB mobile reference band: one asset touched → whole bundle loaded/downloaded).
    ///
    /// Reads real *.bundle files from the default Addressables output locations (Library/com.unity.addressables/aa for
    /// local, ServerData/ for remote) — actual built sizes, zero estimation, so zero false positives; silent when no
    /// build output exists. Custom build paths are not discovered (accepted gap, see ledger). Raw BuildPipeline
    /// AssetBundle output has no standard location, so it is out of scope here (ledger too).
    /// Report-only: re-slicing groups is a project-layout decision.
    /// </summary>
    public sealed class BundleGranularityScanner : IScanner
    {
        public string Name => "Bundle Granularity";
        public Domain Domain => Domain.Assets;

        private const int MaxOversizedShown = 5;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var bundles = CollectBuiltBundles();
            if (bundles.Count == 0) yield break; // no Addressables build output → nothing to judge

            context.ReportProgress(Name, 0.5f);
            var stats = BundlePacking.Evaluate(bundles);
            if (!stats.ShouldReport) yield break;

            var parts = new List<string>();
            if (stats.TinyAntiPattern)
            {
                parts.Add(L.Tr($"{stats.Tiny} of {stats.Total} bundles are under 512KB (median ~{ScannerUtil.Human(stats.MedianBytes)}). " +
                               "Per-bundle header/metadata becomes a meaningful fraction at this size, every loaded bundle holds an OS file handle " +
                               "(iOS caps open handles), and constant handle open/close churn costs battery.",
                               $"{stats.Total} 个 bundle 中有 {stats.Tiny} 个小于 512KB（中位数约 {ScannerUtil.Human(stats.MedianBytes)}）。" +
                               "这个体量下每个包的包头/元数据占比可观，且每个已加载 bundle 都占用一个系统文件句柄" +
                               "（iOS 有句柄上限），句柄反复开关还增加耗电。"));

                // Own-action awareness (cross-reference gating: only stated when actually detected): PerfLint's dedup
                // extraction uses Pack Separately, which trades bundle count for clean dedup. Without this line the
                // report looks like PerfLint contradicting its own one-click action.
                int tinyShared = bundles.Count(b => b.Value < BundlePacking.TinyBytes && BundlePacking.IsPerfLintSharedBundle(b.Key));
                if (tinyShared * 2 >= stats.Tiny)
                {
                    parts.Add(L.Tr($"{tinyShared} of the small bundles come from the \"PerfLint Shared\" dedup group (Pack Separately: one bundle per extracted asset — deduplication stays clean, bundle count grows). " +
                                   "This is a known trade-off of the extraction, not a new problem. If bundle count matters on your platform, switch that group's Bundle Mode to Pack Together (trade-off: touching one shared asset loads the whole shared bundle).",
                                   $"其中 {tinyShared} 个小包来自「PerfLint Shared」去重 group（Pack Separately：每个提取资源一个包——去重最干净、但包数增多）。" +
                                   "这是提取动作的已知取舍、不是新问题。若你的平台在意包数，可把该 group 的 Bundle Mode 改为 Pack Together（代价：用到其中一个共享资源会加载整个共享包）。"));
                }
            }
            if (stats.OversizedNames.Count > 0)
            {
                var shown = stats.OversizedNames.Take(MaxOversizedShown);
                string more = stats.OversizedNames.Count > MaxOversizedShown
                    ? L.Tr($"\n  …and {stats.OversizedNames.Count - MaxOversizedShown} more", $"\n  …另有 {stats.OversizedNames.Count - MaxOversizedShown} 个")
                    : "";
                parts.Add(L.Tr($"{stats.OversizedNames.Count} bundle(s) exceed 50MB:\n  {string.Join("\n  ", shown)}{more}\n" +
                               "Touching one asset in an oversized bundle loads (and for remote content, downloads) the whole thing.",
                               $"{stats.OversizedNames.Count} 个 bundle 超过 50MB：\n  {string.Join("\n  ", shown)}{more}\n" +
                               "超大 bundle 只要用到其中一个资源就得整包加载（远端内容还得整包下载）。"));
            }

            yield return new Finding(
                ruleId: "ASSET.AAGRAN001",
                domain: Domain.Assets,
                severity: Severity.Info,
                title: L.Tr("Addressables bundle granularity is off the mobile reference band", "Addressables bundle 粒度偏离移动端参考区间"),
                detail: L.Tr("Based on the last Addressables build output (rebuild to refresh):\n", "依据最近一次 Addressables 构建产物（重新构建后刷新）：\n") +
                        string.Join("\n", parts) +
                        L.Tr("\nA practical mobile reference is roughly 2–5MB per bundle — regroup content with matching lifecycles into shared bundles " +
                             "instead of one-bundle-per-asset, and split monolithic groups.",
                             "\n移动端单个 bundle 的实用参考区间约 2–5MB——把生命周期一致的内容归组共享 bundle（而非一资源一包），拆分巨型 group。"),
                targetPath: null);
        }

        /// <summary>*.bundle files from the default Addressables output roots. Missing directories are skipped silently.</summary>
        private static List<KeyValuePair<string, long>> CollectBuiltBundles()
        {
            var result = new List<KeyValuePair<string, long>>();
            foreach (var root in OutputRoots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.GetFiles(root, "*.bundle", SearchOption.AllDirectories))
                    {
                        var info = new FileInfo(f);
                        result.Add(new KeyValuePair<string, long>(info.Name, info.Length));
                    }
                }
                catch (Exception) { /* unreadable output dir must not break the scan */ }
            }
            return result;
        }

        private static IEnumerable<string> OutputRoots()
        {
            yield return "Library/com.unity.addressables/aa"; // local build target (Addressables.BuildPath default)
            yield return "ServerData";                        // remote build target (default profile RemoteBuildPath)
        }
    }
}
