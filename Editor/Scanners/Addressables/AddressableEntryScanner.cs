#if PERFLINT_ADDRESSABLES
using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Assets domain: Addressables group entries that cannot be packed.
    ///   ASSET.AAENTRY001 — an entry in a build-included group whose asset has no recognizable main type
    ///     (<c>AssetDatabase.GetMainAssetTypeAtPath</c> returns null: a failed or absent import), or whose address
    ///     contains '[' / ']'. Addressables throws on the first such entry, so the packed build fails outright.
    ///
    /// **Why this rule exists at all.** The same check
    /// (<c>BuildScriptPackedMode.ThrowExceptionIfInvalidFiletypeOrAddress</c>) sits on the path every official Analyze
    /// rule walks, so one bad entry also aborts the duplicate analyses — which used to make those rules produce zero
    /// findings and disappear from the panel with only a console warning to explain it. <see cref="AddressableAnalyzeGuard"/>
    /// keeps the analyses running; this rule names the entry that would otherwise stay invisible until build time.
    ///
    /// Mirrors Addressables' own gates so there are no false positives: groups without a
    /// <c>BundledAssetGroupSchema</c> or with <c>IncludeInBuild == false</c> are skipped (the build skips them too),
    /// folders are exempt (they legitimately have no main asset type), and entries are expanded with the same
    /// <c>GatherAllAssets</c> call the build uses so assets inside addressable folders are covered.
    ///
    /// Report-only. The fix is a judgement call PerfLint must not make for you: reimport the asset, replace it with a
    /// format the editor accepts, or drop the entry from the group. Decision logic lives in
    /// <c>BundlePacking.ClassifyEntry</c> (main assembly, unit-tested in batchmode).
    /// </summary>
    public sealed class AddressableEntryScanner : IScanner
    {
        public string Name => "Addressables Entries";
        public Domain Domain => Domain.Assets;

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) yield break;            // Addressables not initialized

            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(Name, 0.05f);

            var bad = new List<BadEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var groups = settings.groups;
            for (int gi = 0; groups != null && gi < groups.Count; gi++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var group = groups[gi];
                if (group == null) continue;

                // Same gate as CalculateInputDefinitions / ProcessGroup: no bundled schema or excluded from the
                // build → the entries are never packed, so a broken one there cannot fail anything.
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null || !schema.IncludeInBuild) continue;

                context.ReportProgress(Name, 0.05f + 0.9f * gi / Math.Max(1, groups.Count));
                Collect(group, bad, seen);
            }

            if (bad.Count == 0) yield break;

            foreach (var b in bad)
            {
                string path = b.Path;
                bool bracket = b.Problem == BundlePacking.EntryProblem.BracketInAddress;
                string cause = bracket
                    ? L.Tr($"its address \"{b.Address}\" contains '[' or ']', which Addressables rejects",
                           $"它的 address「{b.Address}」含有 '[' 或 ']'，Addressables 不接受")
                    : L.Tr("the editor has no importable asset at that path — AssetDatabase reports no main type, so Addressables sees a DefaultAsset (a failed or missing import; re-importing usually surfaces the real importer error)",
                           "编辑器在该路径上没有可用资产——AssetDatabase 取不到主类型，Addressables 因此把它当成 DefaultAsset（导入失败或未导入；重新导入通常会把真正的导入报错暴露出来）");
                string fix = bracket
                    ? L.Tr("\nFix: rename the entry's address so it has no square brackets.",
                           "\n修法：把该条目的 address 改掉，去掉方括号。")
                    : L.Tr("\nFix: reimport the asset (Assets > Reimport) and read the importer error it reports, replace it with a format the editor accepts, or remove the entry from the group. PerfLint offers no one-click here — which of the three is right is your call.",
                           "\n修法：重新导入该资源（Assets > Reimport）并看它报出的导入错误、换成编辑器能接受的格式、或把该条目移出 group。这里不提供一键——三选一是你的判断。");

                yield return new Finding(
                    ruleId: "ASSET.AAENTRY001",
                    domain: Domain.Assets,
                    severity: Severity.Critical,
                    title: L.Tr($"Addressable entry cannot be packed: {System.IO.Path.GetFileName(path)}", $"Addressable 条目无法打包：{System.IO.Path.GetFileName(path)}"),
                    groupTitle: L.Tr("Addressable entry cannot be packed (Addressables build fails)", "Addressable 条目无法打包（Addressables 构建会失败）"),
                    detail: L.Tr($"{path}\nis an entry of the group \"{b.Group}\", but {cause}.",
                                 $"{path}\n是 group「{b.Group}」的条目，但{cause}。") +
                            L.Tr("\nAddressables throws on the FIRST such entry it meets, so a packed Addressables build fails outright — and because the official Analyze rules walk the same code path, the duplicate analyses have to skip this entry until it is fixed.",
                                 "\nAddressables 遇到第一个这样的条目就抛异常，所以 Addressables 打包会直接失败——而官方 Analyze 规则走的是同一条代码路径，因此在修好之前，重复分析只能跳过这个条目。") +
                            fix,
                    targetPath: path,
                    // Third-party folders are commonly on the ignore list, but a broken entry there breaks YOUR build
                    // just the same — same reasoning as the duplication rules.
                    ignoreExempt: true,
                    ping: () => ScannerUtil.PingAsset(path));
            }
        }

        /// <summary>
        /// Expands one group the way the build does and records entries that would throw. Failures are swallowed per
        /// group: GatherAllAssets touches folder contents and asset types, and a scan-time exception here must not
        /// cost the whole rule — the very failure mode this rule exists to report.
        /// </summary>
        private static void Collect(AddressableAssetGroup group, List<BadEntry> bad, HashSet<string> seen)
        {
            var expanded = new List<AddressableAssetEntry>();
            try
            {
                foreach (var e in group.entries)
                {
                    if (e == null) continue;
                    e.GatherAllAssets(expanded, true, true, false);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr($"Could not expand Addressables group '{group.Name}' (entry check skipped for it): {ex.Message}",
                                                      $"无法展开 Addressables group「{group.Name}」（该 group 的条目检查已跳过）：{ex.Message}"));
                return;
            }

            foreach (var e in expanded)
            {
                if (e == null) continue;
                string path = e.AssetPath;
                if (string.IsNullOrEmpty(path)) continue;      // build skips empty paths after the throw check
                if (!seen.Add(path)) continue;                 // one row per asset, even if several groups carry it

                // DefaultAsset counts as "no usable type", not as a type: that is what Unity reports for a file it
                // cannot import, and it is exactly the value Addressables throws on. Testing only for null misses
                // every ordinary unimportable file — which is how this rule first stayed silent on one.
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                var problem = BundlePacking.ClassifyEntry(
                    e.address,
                    hasGuid: !string.IsNullOrEmpty(e.guid),
                    hasUsableMainType: mainType != null && mainType != typeof(DefaultAsset),
                    isFolder: AssetDatabase.IsValidFolder(path));
                if (problem == BundlePacking.EntryProblem.None) continue;

                bad.Add(new BadEntry { Path = path, Address = e.address, Group = group.Name, Problem = problem });
            }
        }

        private sealed class BadEntry
        {
            public string Path;
            public string Address;
            public string Group;
            public BundlePacking.EntryProblem Problem;
        }
    }
}
#endif
