using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Pure footprint arithmetic for the Resources folder, split out of the scanner so it is unit-testable in
    /// batchmode (no AssetDatabase, no project layout). Everything the scanner learns from Unity — the asset list,
    /// folder test, dependency closure, file sizes — is injected.
    /// </summary>
    public static class ResourcesFootprint
    {
        /// <summary>
        /// Closure weight at/above which the footprint is a Warning rather than an Info. Weight is the LARGER of the
        /// memory and on-disk totals — the two disagree by more than a factor either way (a font asset measured
        /// 10.4 MB on disk and 4.0 MB in memory; a .jpg measured 273 KB on disk and 1.3 MB in memory, since it ships
        /// decoded to BC7 with mips), so taking the max is what keeps either kind of heavy asset from being missed.
        /// </summary>
        public const long WarnBytes = 32L * 1024 * 1024;
        /// <summary>Closure weight at/above which the footprint is worth mentioning at all. See <see cref="WarnBytes"/> for what weight means.</summary>
        public const long InfoBytes = 8L * 1024 * 1024;

        /// <summary>
        /// Closure asset counts for the same two bands. Count matters independently of size because the runtime
        /// builds a name→asset lookup for every Resources entry at startup; thousands of tiny files cost startup
        /// time while weighing almost nothing.
        /// </summary>
        public const int WarnCount = 1000;
        public const int InfoCount = 300;

        /// <summary>
        /// Weight at/above which a closure asset is named individually in the finding, with its own Locate row.
        ///
        /// 1 MB rather than something smaller because of what the tail looks like: on one project this cut 204
        /// closure assets down to 8 rows that still accounted for 83% of the memory, where dropping to 256 KB would
        /// have produced 29 rows for another 9 percentage points — and 21 of those 29 were near-identical entries
        /// for one model's texture set.
        /// </summary>
        public const long HeavyBytes = 1024L * 1024;

        /// <summary>
        /// One asset that LIVES under a Resources folder, weighed by everything it drags into the build.
        ///
        /// The entry point is the unit the list is built from, because it is the unit anyone can act on: you can
        /// move, delete or stop referencing a prefab that sits in Resources. You usually cannot touch the 41.6 MB
        /// model it pulls in — that belongs to an art folder or somebody else's package. Measured on one project,
        /// the heaviest entry weighed 73.5 KB by itself and 66.5 MB with its 152 dependencies.
        /// </summary>
        public sealed class EntryPoint
        {
            /// <summary>The Resources asset itself — what Locate reveals.</summary>
            public string Path;

            /// <summary>The entry's own bytes, ignoring what it references.</summary>
            public long OwnBytes;
            public long OwnMemBytes;

            /// <summary>The entry plus its whole dependency closure. Assets shared with another entry are counted in full here, so the column can total more than the project figure (measured overlap on one project: 5.2%).</summary>
            public long ClosureBytes;
            public long ClosureMemBytes;

            /// <summary>The part of the closure NO other entry references — what actually stops shipping if this entry goes away.</summary>
            public long ExclusiveBytes;
            public long ExclusiveMemBytes;

            /// <summary>How many assets the closure covers, including the entry itself.</summary>
            public int ClosureCount;

            /// <summary>The single heaviest asset in the closure, when that is not the entry itself — the "why is this prefab 66 MB" answer. Null otherwise.</summary>
            public string HeaviestDependency;
            public long HeaviestDependencyMemBytes;
            public long HeaviestDependencyBytes;

            /// <summary>What the list is sorted by: the whole weight this entry is responsible for.</summary>
            public long ClosureWeight => Math.Max(ClosureBytes, ClosureMemBytes);

            /// <summary>
            /// What decides whether the entry is listed at all: its exclusive weight, or its own if that is larger.
            ///
            /// Not the closure weight — that lists the same shared payload once per entry. On one project three TMP
            /// material variants each showed a 1.5 MB closure that was one shared font atlas; by exclusive weight
            /// they measure a few KB each and correctly drop out, while the font asset itself stays on its own bytes.
            /// </summary>
            public long ListingWeight => Math.Max(Math.Max(ExclusiveBytes, ExclusiveMemBytes),
                                                  Math.Max(OwnBytes, OwnMemBytes));
        }

        /// <summary>Per-Resources-folder tally, so a report can say WHICH folder carries the weight.</summary>
        public sealed class RootStat
        {
            public string Root;
            public int Count;
            public long Bytes;
            public long MemBytes;
        }

        public sealed class Result
        {
            /// <summary>Distinct Resources folders found, heaviest first.</summary>
            public List<RootStat> Roots = new List<RootStat>();

            /// <summary>Assets that literally live under a Resources folder.</summary>
            public int DirectCount;
            public long DirectBytes;
            public long DirectMemBytes;

            /// <summary>Direct assets plus everything they depend on — what actually ships because of Resources.</summary>
            public int ClosureCount;
            public long ClosureBytes;
            public long ClosureMemBytes;

            /// <summary>The part of the closure that does NOT live under Resources: assets dragged into the build by a Resources asset referencing them.</summary>
            public int IndirectCount => Math.Max(0, ClosureCount - DirectCount);
            public long IndirectBytes => Math.Max(0, ClosureBytes - DirectBytes);
            public long IndirectMemBytes => Math.Max(0, ClosureMemBytes - DirectMemBytes);

            /// <summary>True when memory was actually measured, so the report can stay silent about it rather than print a zero.</summary>
            public bool HasMemory => ClosureMemBytes > 0;

            /// <summary>Every Resources entry, heaviest closure first. The listing filter has not been applied.</summary>
            public List<EntryPoint> Entries = new List<EntryPoint>();

            /// <summary>The entries worth naming: <see cref="EntryPoint.ListingWeight"/> at or above <see cref="HeavyBytes"/>, heaviest closure first.</summary>
            public List<EntryPoint> HeavyEntries = new List<EntryPoint>();

            /// <summary>The larger of the two totals — see <see cref="WarnBytes"/> for why both are consulted.</summary>
            public long Weight => Math.Max(ClosureBytes, ClosureMemBytes);

            public Severity Severity =>
                Weight >= WarnBytes || ClosureCount >= WarnCount ? Core.Severity.Warning : Core.Severity.Info;

            /// <summary>
            /// Below both Info bands there is nothing worth a row. Almost every project has *some* Resources folder
            /// (importing TMP Essential Resources creates one), so an unconditional finding would be noise in every
            /// report and unactionable in most of them.
            /// </summary>
            public bool ShouldReport =>
                Weight >= InfoBytes || ClosureCount >= InfoCount;
        }

        /// <summary>
        /// Whether a path is a Resources asset that reaches a player build. Path logic only — the caller still has to
        /// exclude folders (an AssetDatabase question) and PerfLint's own install tree.
        ///
        /// Editor-only locations are excluded because they never ship: a Resources folder nested under any Editor/
        /// folder is editor-only by Unity's own rule, and so is Editor Default Resources. Counting those would inflate
        /// the number with bytes no player ever carries.
        /// </summary>
        public static bool IsShippingResourcesPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string norm = assetPath.Replace('\\', '/');
            if (!norm.StartsWith("Assets/", StringComparison.Ordinal)) return false;
            if (!norm.Contains("/Resources/")) return false;
            return !ScannerUtil.IsEditorOnlyPath(norm);
        }

        /// <summary>
        /// "Assets/TextMesh Pro/Resources" for anything beneath it. Uses the FIRST "/Resources/" segment, so a nested
        /// Resources folder is attributed to its outermost one — which is the folder a user would actually act on.
        /// Null when the path holds no Resources segment.
        /// </summary>
        public static string RootOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string norm = assetPath.Replace('\\', '/');
            int i = norm.IndexOf("/Resources/", StringComparison.Ordinal);
            return i < 0 ? null : norm.Substring(0, i + "/Resources".Length);
        }

        /// <summary>
        /// Whether a dependency counts toward the shipped footprint.
        ///
        /// Scripts are dropped: a prefab depends on its MonoScripts, but a .cs file's disk size says nothing about
        /// what it costs in a build (it ends up in a compiled assembly with everything else), so summing them would
        /// make the total mean something other than what the finding claims. Packages/ paths are dropped for a
        /// different reason — they do ship, but the user cannot move a package's internals out of Resources, so
        /// counting them would inflate a number nobody can act on. Both exclusions make the total conservative.
        /// </summary>
        public static bool CountsTowardFootprint(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string norm = assetPath.Replace('\\', '/');
            if (!norm.StartsWith("Assets/", StringComparison.Ordinal)) return false;
            if (ScannerUtil.IsEditorOnlyPath(norm)) return false;
            switch (Path.GetExtension(norm).ToLowerInvariant())
            {
                case ".cs":
                case ".asmdef":
                case ".asmref":
                case ".meta":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Tallies the footprint. <paramref name="resourcesAssets"/> is the already-filtered set of direct Resources
        /// assets; <paramref name="closure"/> is that set plus its recursive dependencies (Unity's GetDependencies
        /// includes the inputs, and duplicates/order are tolerated here).
        /// </summary>
        /// <param name="closureByEntry">
        /// Each Resources asset mapped to its own recursive dependency closure. This is what makes the per-entry
        /// column possible — the batched closure alone cannot say which entry is responsible for what. Null skips
        /// the entry breakdown; the project totals do not depend on it.
        /// </param>
        public static Result Evaluate(
            IReadOnlyList<string> resourcesAssets,
            IEnumerable<string> closure,
            Func<string, long> sizeOf,
            Func<string, long> memoryOf = null,
            IReadOnlyDictionary<string, List<string>> closureByEntry = null)
        {
            var result = new Result();
            if (resourcesAssets == null || resourcesAssets.Count == 0) return result;
            sizeOf ??= _ => 0;
            memoryOf ??= _ => 0;

            var direct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in resourcesAssets)
                if (CountsTowardFootprint(p)) direct.Add(p.Replace('\\', '/'));

            var all = new HashSet<string>(direct, StringComparer.Ordinal);
            if (closure != null)
                foreach (var p in closure)
                {
                    if (!CountsTowardFootprint(p)) continue;
                    all.Add(p.Replace('\\', '/'));
                }

            // Measure every asset once — the per-entry pass below reads shared assets repeatedly, and each read
            // would otherwise reload the file.
            var byteOf = new Dictionary<string, long>(StringComparer.Ordinal);
            var memOf = new Dictionary<string, long>(StringComparer.Ordinal);
            var roots = new Dictionary<string, RootStat>(StringComparer.Ordinal);

            foreach (var p in all)
            {
                long bytes = Math.Max(0, sizeOf(p));
                long mem = Math.Max(0, memoryOf(p));
                byteOf[p] = bytes;
                memOf[p] = mem;

                result.ClosureCount++;
                result.ClosureBytes += bytes;
                result.ClosureMemBytes += mem;

                if (!direct.Contains(p)) continue;
                result.DirectCount++;
                result.DirectBytes += bytes;
                result.DirectMemBytes += mem;

                string root = RootOf(p);
                if (root == null) continue;
                if (!roots.TryGetValue(root, out var stat))
                {
                    stat = new RootStat { Root = root };
                    roots[root] = stat;
                }
                stat.Count++;
                stat.Bytes += bytes;
                stat.MemBytes += mem;
            }

            result.Roots = roots.Values
                .OrderByDescending(r => Math.Max(r.Bytes, r.MemBytes))
                .ThenBy(r => r.Root, StringComparer.Ordinal)
                .ToList();

            if (closureByEntry == null) return result;

            // How many entries reach each asset — the input to "exclusive", which is what a single entry is really
            // on the hook for.
            var reachedBy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in direct)
            {
                if (!closureByEntry.TryGetValue(entry, out var deps) || deps == null) continue;
                foreach (var dep in Normalized(deps, byteOf))
                {
                    reachedBy.TryGetValue(dep, out int n);
                    reachedBy[dep] = n + 1;
                }
            }

            var points = new List<EntryPoint>(direct.Count);
            foreach (var entry in direct)
            {
                var point = new EntryPoint
                {
                    Path = entry,
                    OwnBytes = byteOf.TryGetValue(entry, out var ob) ? ob : 0,
                    OwnMemBytes = memOf.TryGetValue(entry, out var om) ? om : 0,
                };

                if (closureByEntry.TryGetValue(entry, out var deps) && deps != null)
                {
                    string heaviest = null;
                    long heaviestWeight = 0;
                    foreach (var dep in Normalized(deps, byteOf))
                    {
                        long b = byteOf[dep];
                        long m = memOf.TryGetValue(dep, out var mv) ? mv : 0;
                        point.ClosureCount++;
                        point.ClosureBytes += b;
                        point.ClosureMemBytes += m;

                        // The entry's own bytes are always its own, however many other entries also reference it:
                        // it sits in a Resources folder, so it ships regardless, and moving it is what reclaims it.
                        // Without this an asset like a shared font reported "only ~0 B is exclusive to it" while
                        // being the very thing that put it on the list.
                        bool ownedHere = dep == entry || (reachedBy.TryGetValue(dep, out int n) && n == 1);
                        if (ownedHere)
                        {
                            point.ExclusiveBytes += b;
                            point.ExclusiveMemBytes += m;
                        }
                        long w = Math.Max(b, m);
                        if (dep != entry && w > heaviestWeight) { heaviestWeight = w; heaviest = dep; }
                    }
                    // Only worth naming when it actually explains the weight — an entry that IS its own heaviest
                    // part needs no "because of" line.
                    if (heaviest != null && heaviestWeight > Math.Max(point.OwnBytes, point.OwnMemBytes))
                    {
                        point.HeaviestDependency = heaviest;
                        point.HeaviestDependencyBytes = byteOf[heaviest];
                        point.HeaviestDependencyMemBytes = memOf.TryGetValue(heaviest, out var hm) ? hm : 0;
                    }
                }

                points.Add(point);
            }

            result.Entries = points
                .Where(e => e.ClosureWeight > 0)
                .OrderByDescending(e => e.ClosureWeight)
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .ToList();
            result.HeavyEntries = result.Entries.Where(e => e.ListingWeight >= HeavyBytes).ToList();

            return result;
        }

        /// <summary>
        /// Normalises a dependency list to the same path form and membership the totals were built from, so a
        /// per-entry closure can never count something the project total does not. Deduplicates: Unity's
        /// GetDependencies can repeat a path, and counting it twice would inflate one entry's column.
        /// </summary>
        private static IEnumerable<string> Normalized(IEnumerable<string> deps, Dictionary<string, long> known)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in deps)
            {
                if (string.IsNullOrEmpty(d)) continue;
                string p = d.Replace('\\', '/');
                if (!known.ContainsKey(p)) continue;   // scripts, package internals, editor-only — already excluded
                if (seen.Add(p)) yield return p;
            }
        }
    }

    /// <summary>
    /// Assets domain: what the Resources folders cost this build.
    ///   ASSET.RES001 — the Resources dependency closure is large enough to matter. Everything under a Resources
    ///     folder ships in the player whether or not anything references it, drags its whole dependency closure in
    ///     with it, and adds an entry to the lookup table the runtime builds at startup. Unity's own guidance is to
    ///     avoid Resources for exactly these reasons.
    ///
    /// Why the existing rules leave a hole here: ASSET.UNREF001 deliberately EXCLUDES /Resources/ (those assets are
    /// always reachable, so "unreferenced" is meaningless for them) and treats them as roots instead, and the
    /// Addressables rules only speak when Addressables is installed and its content overlaps Resources. A project
    /// that loads everything through Resources.Load and never touched Addressables therefore got no asset-domain
    /// signal at all — the case this rule exists for.
    ///
    /// Every asset over <see cref="ResourcesFootprint.HeavyBytes"/> is named in the finding, each with its own Locate
    /// row and each tagged with whether it is IN a Resources folder or was dragged in by one. That split is the whole
    /// point of the list: a briefly-shipped variant put one finding per asset instead, and with identical titles down
    /// the column it was impossible to tell which assets were the ones actually sitting in Resources. The list is
    /// written into Detail as well, because Detail is what the HTML export and a restored session carry.
    ///
    /// Report-only by design: moving an asset out of Resources means rewriting its Resources.Load call sites, which
    /// is a refactor decision, not a setting to flip. For most listed assets the fix does not touch the asset at all
    /// — it changes the reference that drags it in, which is why each row carries its attribution.
    ///
    /// No savings estimate is attached, on purpose: an asset moved out of Resources still ships if a build scene
    /// references it, so any "you would save X" figure would be a guess. The finding reports measured footprint and
    /// says plainly what each gauge does and does not mean.
    /// </summary>
    public sealed class ResourcesFootprintScanner : IScanner
    {
        public string Name => "Resources Footprint";
        public Domain Domain => Domain.Assets;

        /// <summary>
        /// Above this many Resources entry points, per-entry attribution is skipped. Working out who dragged an asset
        /// in costs one recursive GetDependencies per ENTRY (35 entries measured at 185 ms); a project with thousands
        /// would pay seconds for a line of prose. The footprint totals do not depend on it.
        /// </summary>
        private const int MaxRootsForAttribution = 500;

        /// <summary>
        /// Above this many closure assets, memory is not measured. Measuring loads every sub-object of every asset
        /// (204 assets measured at 263 ms); the guard keeps a pathological project from paying seconds and holding
        /// that much in graphics memory. The on-disk figures still stand on their own.
        /// </summary>
        private const int MaxClosureForMemory = 20000;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var direct = new List<string>();
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!ResourcesFootprint.IsShippingResourcesPath(p)) continue;
                if (AssetDatabase.IsValidFolder(p)) continue;
                if (ScannerUtil.IsPerfLintOwnAsset(p)) continue;
                direct.Add(p);
            }
            if (direct.Count == 0) yield break;

            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(Name, 0.15f);

            // One batched call: GetDependencies over the whole root set is markedly cheaper than per-asset calls,
            // and the union is what the totals are built from.
            var closure = AssetDatabase.GetDependencies(direct.ToArray(), true);

            // Per-entry closures — the batched call above throws away WHICH entry reached what, and that is exactly
            // what the list is built from: the actionable unit is the Resources asset, weighed by everything it
            // drags in. One recursive call per entry (35 entries measured at 185 ms).
            Dictionary<string, List<string>> closureByEntry = null;
            if (direct.Count <= MaxRootsForAttribution)
            {
                closureByEntry = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                for (int i = 0; i < direct.Count; i++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.ReportProgress(Name, 0.15f + 0.35f * i / Math.Max(1, direct.Count));
                    closureByEntry[direct[i]] = AssetDatabase.GetDependencies(direct[i], true).ToList();
                }
            }

            context.ReportProgress(Name, 0.5f);

            // Memory is measured per asset and each load is throttled — a scan never yields a frame, so without the
            // reclaim the accumulated sub-objects sit in graphics memory until the whole scan returns.
            Func<string, long> memoryOf = null;
            int closureCount = closure != null ? closure.Length : 0;
            if (closureCount > 0 && closureCount <= MaxClosureForMemory)
            {
                int loads = 0;
                memoryOf = path =>
                {
                    long bytes = ScannerUtil.AssetMemoryBytes(path);
                    loads = ScannerUtil.ThrottleReclaim(loads);
                    return bytes;
                };
            }

            context.ReportProgress(Name, 0.6f);
            var r = ResourcesFootprint.Evaluate(direct, closure, ScannerUtil.FileSizeBytes, memoryOf, closureByEntry);

            // The project-wide bands still gate everything: a project whose Resources folders are small hears
            // nothing, however its individual files rank among themselves.
            if (!r.ShouldReport) yield break;

            foreach (var e in r.HeavyEntries.Take(MaxListed))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                string path = e.Path;

                string headline = r.HasMemory
                    ? L.Tr($"Ships ~{ScannerUtil.Human(e.ClosureMemBytes)} in memory", $"带走内存约 {ScannerUtil.Human(e.ClosureMemBytes)}")
                    : L.Tr($"Ships ~{ScannerUtil.Human(e.ClosureBytes)}", $"带走约 {ScannerUtil.Human(e.ClosureBytes)}");
                int deps = Math.Max(0, e.ClosureCount - 1);
                string withDeps = deps == 0 ? ""
                    : L.Tr(deps == 1 ? " · 1 dependency" : $" · {deps} dependencies", $" · 含 {deps} 项依赖");

                yield return new Finding(
                    ruleId: "ASSET.RES001",
                    domain: Domain.Assets,
                    severity: e.ClosureWeight >= ResourcesFootprint.WarnBytes ? Severity.Warning : Severity.Info,
                    // The weight goes in the title because it is what distinguishes one row from the next — a column
                    // of identical titles over differing paths is unreadable (reported from the field).
                    title: headline + withDeps,
                    // The project total lives in the GROUP title, which is the one line visible while the rule is
                    // still collapsed — and it is exactly what a group title is for (rule-level, no per-finding
                    // quantity). Dropping the summary finding would otherwise have lost this number entirely, and
                    // it is the number that reframes everything below it: the folders hold 3.4 MB, the build carries
                    // 77 MB because of them.
                    groupTitle: GroupHeadline(r),
                    detail: EntryDetailLine(e, r.HasMemory) + WhyAndFix() + ProjectContext(r),
                    targetPath: path,
                    // Same reasoning as ASSET.AARES001: the heaviest offenders sit in third-party folders people
                    // ignore to silence import-settings advice, and the fix here never edits a third-party asset.
                    ignoreExempt: true,
                    ping: () => ScannerUtil.PingAsset(path));
            }
        }

        /// <summary>
        /// The rule group's heading — the project total. Read by the panel and the HTML export from the first
        /// finding in the group (<c>GroupTitleOrTitle</c>), so it is on screen before anything is expanded.
        /// </summary>
        internal static string GroupHeadline(ResourcesFootprint.Result r)
        {
            string total = r.HasMemory
                ? L.Tr($"~{ScannerUtil.Human(r.ClosureMemBytes)} in memory", $"内存约 {ScannerUtil.Human(r.ClosureMemBytes)}")
                : L.Tr($"~{ScannerUtil.Human(r.ClosureBytes)}", $"约 {ScannerUtil.Human(r.ClosureBytes)}");
            string folders = r.Roots.Count == 1
                ? L.Tr("1 folder", "1 个目录")
                : L.Tr($"{r.Roots.Count} folders", $"{r.Roots.Count} 个目录");
            return L.Tr($"Resources ships {total} — {r.ClosureCount} assets across {folders}",
                        $"Resources 共带走{total}——{folders}、{r.ClosureCount} 个资源");
        }

        /// <summary>
        /// The project-wide breakdown, appended to every finding: which Resources folders carry the weight, and how
        /// much of it is not in Resources at all. Repeated per finding because each one is independently
        /// collapsible, ignorable and exportable — and because the split is what makes a single row make sense
        /// (a 73 KB file "shipping 66.4 MB" reads as an error until you know 169 of the 204 assets are dragged in).
        /// </summary>
        private static string ProjectContext(ResourcesFootprint.Result r)
        {
            Func<long, long, string> both = (mem, disk) => r.HasMemory
                ? L.Tr($"~{ScannerUtil.Human(mem)} in memory / ~{ScannerUtil.Human(disk)} on disk",
                       $"内存约 {ScannerUtil.Human(mem)} / 磁盘约 {ScannerUtil.Human(disk)}")
                : L.Tr($"~{ScannerUtil.Human(disk)} on disk", $"磁盘约 {ScannerUtil.Human(disk)}");

            // "- " rather than leading spaces: UI Toolkit collapses leading whitespace in a wrapping Label, so a
            // space-indented list renders flush against the paragraph above it and stops reading as a list at all.
            string roots = string.Join("\n", r.Roots.Select(root =>
                $"- {root.Root} — {root.Count} " + L.Tr("assets", "个资源") + ", " + both(root.MemBytes, root.Bytes)));

            string indirect = r.IndirectCount > 0
                ? L.Tr($" {r.IndirectCount} of them ({both(r.IndirectMemBytes, r.IndirectBytes)}) do not live under Resources at all — they ship only because a Resources file references them.",
                       $" 其中 {r.IndirectCount} 个（{both(r.IndirectMemBytes, r.IndirectBytes)}）根本不在 Resources 下——它们只因被 Resources 里的文件引用才进包。")
                : "";

            return L.Tr($"\n\nAcross the project: {r.DirectCount} files live under a Resources folder; with their dependencies that is {r.ClosureCount} assets, {both(r.ClosureMemBytes, r.ClosureBytes)}.",
                        $"\n\n全项目范围：{r.DirectCount} 个文件位于 Resources 目录，连同依赖共 {r.ClosureCount} 个、{both(r.ClosureMemBytes, r.ClosureBytes)}。")
                 + indirect + "\n" + roots;
        }

        /// <summary>
        /// The rule-level explanation appended to every finding: why a Resources file costs more than it looks, and
        /// what to do. Repeated per finding on purpose — each row is independently collapsible, ignorable and
        /// exportable, so a reader who opens only one still gets the whole story.
        /// </summary>
        private static string WhyAndFix()
            => L.Tr("\n\nWhy this matters: everything under Resources enters the player build whether or not anything references it — and so does everything it references, which is usually where the weight is. The unreferenced-asset check cannot help here, because Resources assets are reachable by definition. The runtime also builds a lookup entry for each one at startup, and content loaded this way is awkward to release (only a global UnloadUnusedAssets reclaims it).",
                    "\n\n为什么要在意：Resources 下的一切不论是否被引用都会进入玩家包——它引用的东西同样如此，而重量通常正在那里。「未引用资源」检测在这里帮不上忙，因为 Resources 资源按定义就是可达的。运行时还会在启动阶段为每一条建立查找表项，而且这样加载的内容不好释放（只能靠全局 UnloadUnusedAssets 回收）。")
             + L.Tr("\n\nFix (a refactor decision, so no one-click here): move this file out of Resources and load it through Addressables, or reference it directly from the scene/prefab that needs it; pure data can go to StreamingAssets. Where the weight comes from something it references, changing that one reference is enough — the referenced asset itself does not have to move, and often should not (it may be third-party).",
                    "\n\n修法（属重构决策，故不提供一键）：把这个文件移出 Resources 改用 Addressables 加载，或由需要它的场景/预制体直接引用；纯数据可放 StreamingAssets。若重量来自它引用的东西，改掉那一处引用就够了——被引用的资产本身不必移动，而且往往不该动（可能是第三方的）。")
             + L.Tr("\n\nNote on the numbers: on-disk figures are source file sizes, not build bytes — textures are re-encoded to the platform format and the archive is compressed, so the shipped figure differs. Memory figures are estimates: textures use the size the Inspector reports (platform format + mips) and meshes are computed from the vertex layout they declare, doubled where Read/Write is on; other asset types are read from the profiler and are less exact. Scripts and package-internal dependencies are excluded from both, which keeps these figures conservative.",
                    "\n\n关于这些数字：磁盘口径是源文件大小、不等于包体字节——贴图会重新编码为平台格式、归档还会压缩，实际进包的数值不同。内存口径是估算：贴图取 Inspector 显示的大小（平台格式 + mip），网格按其自身声明的顶点布局计算、开了 Read/Write 则翻倍；其余类型读自 profiler，精度较低。脚本与包内依赖两个口径都已排除，因此数字是保守估计。");

        /// <summary>How many Resources files are reported, before the rest are left out. A cap rather than a full list because the tail is long and each row costs a card in the panel.</summary>
        private const int MaxListed = 12;

        /// <summary>
        /// One entry's weight, said in a way that survives the two things that would otherwise mislead: a file whose
        /// weight is almost entirely something it references (name that something — "66.5 MB, mostly V4L.fbx" is the
        /// answer to "why is this prefab 66 MB"), and a file sharing payload with other entries (say so, otherwise
        /// the column appears to add up to more than the project total).
        /// </summary>
        internal static string EntryDetailLine(ResourcesFootprint.EntryPoint e, bool hasMemory)
        {
            var parts = new List<string>();

            // Its own size, next to what it drags in — the gap between the two IS the finding. A 73 KB prefab
            // reported as "66.4 MB" reads like a mistake until you can see both numbers.
            long own = hasMemory ? e.OwnMemBytes : e.OwnBytes;
            parts.Add(L.Tr($"the file itself is ~{ScannerUtil.Human(own)}", $"文件自身约 {ScannerUtil.Human(own)}"));

            if (hasMemory)
                parts.Add(L.Tr($"~{ScannerUtil.Human(e.ClosureBytes)} on disk", $"磁盘约 {ScannerUtil.Human(e.ClosureBytes)}"));

            // The dependency count is in the title; repeating it here put the same number twice on one card.

            if (!string.IsNullOrEmpty(e.HeaviestDependency))
            {
                string depSize = hasMemory
                    ? ScannerUtil.Human(e.HeaviestDependencyMemBytes)
                    : ScannerUtil.Human(e.HeaviestDependencyBytes);
                parts.Add(L.Tr($"mostly {e.HeaviestDependency} (~{depSize})",
                               $"主要来自 {e.HeaviestDependency}（约 {depSize}）"));
            }

            // Only mention sharing when a meaningful share of the weight is shared — otherwise every row would
            // carry a caveat about a rounding error.
            long exclusive = Math.Max(e.ExclusiveBytes, e.ExclusiveMemBytes);
            if (e.ClosureWeight > 0 && exclusive * 4 < e.ClosureWeight * 3)   // exclusive < 75% of the closure
                parts.Add(L.Tr($"only ~{ScannerUtil.Human(exclusive)} exclusive to it, the rest shared with other Resources files",
                               $"仅约 {ScannerUtil.Human(exclusive)} 为其独占，其余与别的 Resources 文件共享"));

            return string.Join(L.Tr(" · ", " · "), parts);
        }

    }
}
