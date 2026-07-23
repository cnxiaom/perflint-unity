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
    /// Assets domain (package size optimization): AssetBundle implicit-dependency duplicate packing detection.
    ///   ASSET.ABDUP000 — Summary (project-level): total number of shared assets packed multiple times, estimated total waste.
    ///   ASSET.ABDUP001 — Per-asset: a shared asset not explicitly assigned to any bundle is copied into each bundle that references it.
    ///   ASSET.ABDUP002 — Built-in resources (Resources/unity_builtin_extra, Library/unity default resources) implicitly
    ///     referenced by ≥2 bundles: the referenced built-in objects (Default-Particle, built-in shaders, default font…)
    ///     are copied into EVERY bundle that references them and can never be explicitly assigned to a bundle for
    ///     de-duplication — the only fix is replacing the built-in reference with a project-local copy. One project-level
    ///     finding per container (max 2), Info: nearly every AB project hits this, so per-reference findings would be noise,
    ///     but the duplication is real (course-verified common trap) and invisible without tooling.
    ///
    /// How it works: assets that are referenced by multiple bundles but not explicitly assigned to any bundle (implicit
    /// dependencies) are **copied into every bundle that references them** at build time — the classic package-size killer,
    /// virtually impossible to spot by eye. Results are only produced when the project uses AssetBundles (zero noise).
    /// Addressables redundancy is handled separately (requires an optional package — second pass).
    ///
    /// Report-only (no auto-fix): true de-duplication = explicit assignment / reference redirection, which is a human decision.
    /// **Sorted by estimated in-memory size** (textures ≈ the Inspector storage value; via `ScannerUtil.StorageMemoryBytes`),
    /// falling back to source file size when unavailable. The verifiable **source file size is also shown** ("~X in memory,
    /// source ~Y"). Sizes are approximate — not final compressed bundle sizes; no exact "wasted MB" claim (noted in detail).
    /// Mirrors the AA duplicate scanner (`AddressableDuplicateScanner`).
    /// </summary>
    public sealed class AssetBundleDuplicateScanner : IScanner
    {
        public string Name => "AssetBundle Duplicates";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var bundles = AssetDatabase.GetAllAssetBundleNames();
            if (bundles == null || bundles.Length == 0) yield break; // project does not use AssetBundles

            // 1) All assets explicitly assigned to a bundle — they have an owner and will not be packed redundantly into other bundles.
            var assigned = new HashSet<string>();
            foreach (var b in bundles)
                foreach (var p in AssetDatabase.GetAssetPathsFromAssetBundle(b))
                    assigned.Add(p);

            // 2) Count how many distinct bundles each "implicit dependency" asset is included in.
            var implicitToBundles = new Dictionary<string, HashSet<string>>();
            for (int i = 0; i < bundles.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / bundles.Length);

                var explicitAssets = AssetDatabase.GetAssetPathsFromAssetBundle(bundles[i]);
                if (explicitAssets.Length == 0) continue;

                foreach (var dep in AssetDatabase.GetDependencies(explicitAssets, true))
                {
                    if (assigned.Contains(dep)) continue;     // explicitly assigned → will not be duplicated
                    // Allow real assets under Assets/ and Packages/: assets inside Packages (URP/built-in shaders,
                    // TMP font materials, third-party package prefabs) are also copied into each bundle that implicitly
                    // depends on them, and the user can explicitly assign them to de-duplicate.
                    // Built-in virtual resources never show up here — path-level GetDependencies no longer reports the
                    // built-in containers at all (verified empirically on 2022.3) — so ABDUP002 detects them separately
                    // below via object-level CollectDependencies.
                    if (!dep.StartsWith("Assets/") && !dep.StartsWith("Packages/")) continue;
                    if (IsExcluded(dep)) continue;            // scripts / assemblies / shader includes do not enter a bundle on their own

                    if (!implicitToBundles.TryGetValue(dep, out var set))
                    {
                        set = new HashSet<string>();
                        implicitToBundles[dep] = set;
                    }
                    set.Add(bundles[i]);
                }
            }

            // ABDUP002 — built-in containers referenced by ≥2 bundles (emitted before the per-asset list, which
            // early-returns when there are no Assets/-level duplicates and must not swallow these).
            foreach (var f in BuildBuiltinFindings(bundles, context))
                yield return f;

            // 3) Included in ≥2 bundles = duplicate packing. **Sort by estimated in-memory size descending** (textures ≈
            //    the Inspector storage value) so the most memory-impactful duplicates surface first; fall back to source
            //    file size when the memory estimate is unavailable. We still display the verifiable source file size too.
            //    Do not report an exact "wasted MB" (real redundancy is runtime memory / packed size, hard to estimate).
            var dups = new List<DupEntry>();
            foreach (var kv in implicitToBundles)
            {
                int copies = kv.Value.Count;
                if (copies < 2) continue;
                long size = ScannerUtil.FileSizeBytes(kv.Key);
                if (size <= 0) continue;

                var list = kv.Value.ToList();
                list.Sort(StringComparer.Ordinal);
                dups.Add(new DupEntry
                {
                    Path = kv.Key,
                    Copies = copies,
                    Size = size,
                    Mem = ScannerUtil.StorageMemoryBytes(kv.Key),
                    Bundles = list
                });
            }
            if (dups.Count == 0) yield break;

            dups.Sort((a, b) =>
            {
                long am = a.Mem > 0 ? a.Mem : a.Size;   // fall back to file size when memory is unknown
                long bm = b.Mem > 0 ? b.Mem : b.Size;
                int byMem = bm.CompareTo(am);
                return byMem != 0 ? byMem : string.CompareOrdinal(a.Path, b.Path);
            });

            // No longer emitting the summary finding (ASSET.ABDUP000): once the "wasted MB" figure is removed,
            // only a count remains (the rule-group header already shows (N)) plus a top list that duplicates the
            // per-asset findings — pure redundancy. Per-asset ABDUP001 is sufficient. Kept consistent with AA
            // (AADUP000 also removed).
            // Per-asset findings (sorted by in-memory estimate, descending).
            foreach (var d in dups)
            {
                string sizeHuman = ScannerUtil.Human(d.Size);
                string memHuman = d.Mem > 0 ? ScannerUtil.Human(d.Mem) : null;
                // Dual size phrase: in-memory estimate (sort basis; textures ≈ Inspector) + verifiable source file size.
                string sizePhrase = memHuman != null
                    ? L.Tr($"~{memHuman} in memory, source ~{sizeHuman}", $"内存约 {memHuman}、源文件约 {sizeHuman}")
                    : L.Tr($"source ~{sizeHuman}", $"源文件约 {sizeHuman}");
                yield return new Finding(
                    ruleId: "ASSET.ABDUP001",
                    domain: Domain.Assets,
                    severity: Severity.Warning,
                    title: L.Tr($"Asset ({sizePhrase}) is duplicated across {d.Copies} AssetBundles", $"资源（{sizePhrase}）被 {d.Copies} 个 AssetBundle 重复打包"),
                    // Group header stays generic (no per-instance size/count), matching every other rule group.
                    groupTitle: L.Tr("Asset duplicated across AssetBundles", "资源被多个 AssetBundle 重复打包"),
                    detail: L.Tr($"{d.Path} ({sizePhrase}) is an implicit dependency not explicitly assigned to a bundle, so it is packed into {d.Copies} bundles:",
                                 $"{d.Path}（{sizePhrase}）是隐式依赖、未显式分配 bundle，被打进 {d.Copies} 个 bundle：") +
                            $"\n  {string.Join("\n  ", d.Bundles)}" +
                            L.Tr("\n(After de-duplication only one copy is kept. Sizes are approximate — the in-memory figure ≈ the Inspector value; the source file is the verifiable on-disk size. We don't claim an exact \"wasted MB\".)",
                                 "\n（去重后只保留一份。大小为约值——内存约等于 Inspector 显示值；源文件是可核对的磁盘大小。不写精确「浪费 MB」。）") +
                            L.Tr("\nExplicitly assign it to one shared bundle and have the other bundles reference it.",
                                 "\n建议把它显式分配到一个共享 bundle，其余 bundle 引用即可。"),
                    targetPath: d.Path,
                    // Duplication findings bypass the ignore-path filter (see Finding.IgnoreExempt): third-party
                    // duplication bloats the user's build, and the fix (explicit bundle assignment) doesn't edit the asset.
                    ignoreExempt: true,
                    ping: () => ScannerUtil.PingAsset(d.Path),
                    // Estimate only, kept OUT of the finding text (the "no exact wasted-MB claim" wording above
                    // stands — bundle compression makes the true delta inexact). Source bytes × extra copies feeds
                    // the panel's aggregate "up to ~X (est.)" line.
                    estimatedBuildSavingsBytes: d.Size * (d.Copies - 1));
            }
        }

        private sealed class DupEntry
        {
            public string Path;
            public int Copies;
            public long Size;   // raw on-disk source file bytes (verifiable in Explorer / Project view)
            public long Mem;    // estimated in-memory size (textures ≈ Inspector value); 0 if unavailable — the sort key
            public List<string> Bundles;
        }

        /// <summary>
        /// Built-in virtual resource containers whose referenced objects get copied into every referencing bundle:
        /// "Resources/unity_builtin_extra" (Standard/Sprites/Unlit shaders, Default-Material, Default-Particle…) and
        /// "Library/unity default resources" (Cube/Sphere meshes, default font, UI shaders). Matched by suffix to be
        /// robust against a leading path segment changing.
        /// </summary>
        internal static bool IsBuiltinContainer(string dep)
            => !string.IsNullOrEmpty(dep)
            && (dep.EndsWith("unity_builtin_extra", StringComparison.OrdinalIgnoreCase)
             || dep.EndsWith("unity default resources", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// ASSET.ABDUP002 findings: one per built-in container referenced by ≥2 bundles.
        /// Detection MUST go through object-level <see cref="EditorUtility.CollectDependencies"/>: path-level
        /// AssetDatabase.GetDependencies silently omits the built-in containers (verified on 2022.3 — a material on a
        /// built-in shader reports no dependency at all), which is precisely why this duplication is invisible to most
        /// tooling. CollectDependencies loads objects, so the probe is capped: enough for real layouts (a bundle's
        /// builtin usage almost always shows within its first assets) without turning into a full project load.
        /// </summary>
        private static IEnumerable<Finding> BuildBuiltinFindings(string[] bundles, ScanContext context)
        {
            const int maxExamples = 6;   // example lines shown per container
            const int maxProbes = 300;   // CollectDependencies calls total (each loads an asset + its object graph)

            var toBundles = new Dictionary<string, HashSet<string>>();  // container → referencing bundles
            var examples = new Dictionary<string, List<string>>();      // container → "asset ← bundle" lines
            int probes = 0;
            foreach (var b in bundles)
            {
                if (probes >= maxProbes) break;
                foreach (var asset in AssetDatabase.GetAssetPathsFromAssetBundle(b))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (++probes > maxProbes) break;

                    var main = AssetDatabase.LoadMainAssetAtPath(asset);
                    if (main == null || main is SceneAsset) continue; // scene contents aren't traversable here
                    foreach (var o in EditorUtility.CollectDependencies(new[] { main }))
                    {
                        if (o == null) continue;
                        string p = AssetDatabase.GetAssetPath(o);
                        if (!IsBuiltinContainer(p)) continue;

                        if (!toBundles.TryGetValue(p, out var bset))
                        {
                            bset = new HashSet<string>();
                            toBundles[p] = bset;
                            examples[p] = new List<string>();
                        }
                        bool newForBundle = bset.Add(b);
                        if (newForBundle && examples[p].Count < maxExamples)
                            examples[p].Add($"{asset}  ←  {b}"); // one example per (container, bundle): the first referencing root
                    }
                }
            }

            foreach (var kv in toBundles.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string container = kv.Key;
                if (kv.Value.Count < 2) continue;
                var bundleList = kv.Value.OrderBy(b => b, StringComparer.Ordinal).ToList();
                string shortName = container.Substring(container.LastIndexOf('/') + 1);
                var exampleLines = examples[container];
                string exampleBlock = exampleLines.Count > 0
                    ? L.Tr($"\nExample assets pulling it in (bundle roots):\n  {string.Join("\n  ", exampleLines)}",
                           $"\n引用示例（bundle 根资源）：\n  {string.Join("\n  ", exampleLines)}")
                    : "";
                yield return new Finding(
                    ruleId: "ASSET.ABDUP002",
                    domain: Domain.Assets,
                    severity: Severity.Info,
                    title: L.Tr($"Built-in resources ({shortName}) duplicated across {bundleList.Count} AssetBundles",
                                $"内置资源（{shortName}）被 {bundleList.Count} 个 AssetBundle 重复打包"),
                    groupTitle: L.Tr("Built-in resources duplicated across AssetBundles", "内置资源被多个 AssetBundle 重复打包"),
                    detail: L.Tr($"Content in {bundleList.Count} bundles references Unity's built-in resources ({container}) — default particle/material, " +
                                 "built-in shaders, default font, primitive meshes, etc. Built-in objects can never be assigned to a bundle, so each referencing " +
                                 "bundle packs its OWN copy of whatever it references (memory holds one copy per loaded bundle). Affected bundles:",
                                 $"{bundleList.Count} 个 bundle 的内容引用了 Unity 内置资源（{container}）——默认粒子/材质、内置 shader、默认字体、基础网格等。" +
                                 "内置对象无法分配到任何 bundle，每个引用它的 bundle 都会各自打包一份所引用的内置对象（同时加载时内存各占一份）。受影响的 bundle：") +
                            $"\n  {string.Join("\n  ", bundleList)}" +
                            exampleBlock +
                            L.Tr("\nFix: replace built-in references with project-local copies (e.g. import your own particle texture / shader / font and point the assets at it), " +
                                 "then assign those copies to a shared bundle. Classic trap: a default Particle System silently references the built-in Default-Particle texture.",
                                 "\n修法：把内置引用换成项目内的副本（如导入自己的粒子贴图/shader/字体并让资源改用它），再把副本分配进共享 bundle。" +
                                 "经典坑：默认粒子系统会静默引用内置 Default-Particle 贴图。"));
            }
        }

        private static bool IsExcluded(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            // Scripts / assemblies do not enter a bundle; shader includes (.hlsl/.cginc/...) are #include text
            // fragments compiled into the shaders that reference them and are never packed as standalone assets —
            // exclude them to avoid false-positive duplicate reports.
            return ext == ".cs" || ext == ".asmdef" || ext == ".asmref" || ext == ".dll" ||
                   ext == ".hlsl" || ext == ".hlslinc" || ext == ".cginc" || ext == ".glslinc";
        }
    }
}
