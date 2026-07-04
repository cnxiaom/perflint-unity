#if PERFLINT_ADDRESSABLES
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using PerfLint.Core;
using PerfLint.L10n;

namespace PerfLint.Scanners
{
    /// <summary>
    /// "Extracts" implicitly duplicated assets into a shared Addressable group, eliminating
    /// cross-group duplicate packing — this is the executable fix for ASSET.AADUP
    /// (as opposed to the official Build Report which is read-only).
    ///
    /// **Only adds the Addressable tag; does NOT touch any references / does NOT redirect GUIDs.**
    /// This is the lowest-risk subset of the paid "one-click dedup" feature:
    /// once an asset is marked Addressable it has a unique group home, and other groups
    /// reference it rather than each bundling their own copy, eliminating the duplication.
    ///
    /// Note: Addressables config changes **do NOT go through Unity Undo**. One-click rollback is provided by
    /// <see cref="RevertAll"/> (removes the whole "PerfLint Shared" group, returning its assets to plain
    /// implicit dependencies) — surfaced via the Tools/PerfLint menu.
    /// </summary>
    internal static class AddressableSharedGroup
    {
        public const string SharedGroupName = "PerfLint Shared";

        /// <summary>True if the shared group currently exists (drives the rollback menu's enabled state).</summary>
        public static bool SharedGroupExists()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            return settings != null && settings.FindGroup(SharedGroupName) != null;
        }

        /// <summary>
        /// One-click rollback: removes the entire "PerfLint Shared" group. Its entries lose their Addressable mark and
        /// revert to plain (implicit) dependencies — exactly the pre-extraction state. Other groups are untouched.
        /// </summary>
        public static FixResult RevertAll()
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null) return FixResult.Fail(L.Tr("Addressables settings are not initialized.", "Addressables 设置未初始化。"));

                var group = settings.FindGroup(SharedGroupName);
                if (group == null) return FixResult.Fail(L.Tr($"Group \"{SharedGroupName}\" does not exist (nothing to revert).", $"group「{SharedGroupName}」不存在（无需回退）。"));

                int count = group.entries != null ? group.entries.Count : 0;
                // postEvent=true so Addressables persists the removal and refreshes its window.
                settings.RemoveGroup(group);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                return FixResult.Ok(L.Tr($"Reverted: removed \"{SharedGroupName}\" ({count} asset(s) back to implicit dependencies).", $"已回退：移除「{SharedGroupName}」（{count} 个资源恢复为隐式依赖）。"));
            }
            catch (Exception e)
            {
                return FixResult.Fail(L.Tr($"Rollback failed: {e.Message}", $"回退失败：{e.Message}"));
            }
        }

        /// <summary>Moves a single asset into the shared group (creates it if absent; must carry BundledAssetGroupSchema to participate in builds).</summary>
        public static FixResult Extract(string assetPath)
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null) return FixResult.Fail(L.Tr("Addressables settings are not initialized.", "Addressables 设置未初始化。"));

                var group = EnsureSharedGroup(settings, out string groupErr);
                if (group == null) return FixResult.Fail(groupErr);

                var outcome = TryExtractOne(settings, group, assetPath, out string detail);
                if (outcome != ExtractOutcome.Extracted) return FixResult.Fail(detail);

                // Entry data lives inside the group asset; the group must also be marked dirty, otherwise SaveAssets does not persist the entry.
                EditorUtility.SetDirty(group);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                return FixResult.Ok(L.Tr($"Extracted into \"{SharedGroupName}\": {assetPath}", $"已提取到「{SharedGroupName}」：{assetPath}"));
            }
            catch (Exception e)
            {
                return FixResult.Fail(L.Tr($"Extraction failed: {e.Message}", $"提取失败：{e.Message}"));
            }
        }

        /// <summary>
        /// Batch entry point for the rule-level "Extract all". Does every CreateOrMoveEntry then a SINGLE SaveAssets
        /// (the per-item Extract's one-save-per-asset was O(N) full-project saves — pathologically slow at 300+ items),
        /// categorizes results (extracted / skipped read-only package assets / failed), and — critically — RE-RUNS the
        /// official "Check Duplicate Bundle Dependencies" before and after so the summary reports whether the extraction
        /// actually reduced the duplicate count. Marking assets Addressable is the documented dedup fix, but for some
        /// project structures (scene-dominated duplication, read-only package deps) it may not reduce duplicates — the
        /// self-check makes that an observed fact in the result dialog instead of a silent non-effect.
        /// </summary>
        public static FixResult ExtractMany(IReadOnlyList<string> assetPaths)
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null) return FixResult.Fail(L.Tr("Addressables settings are not initialized.", "Addressables 设置未初始化。"));
                if (assetPaths == null || assetPaths.Count == 0) return FixResult.Fail(L.Tr("Nothing to extract.", "没有可提取的资源。"));

                // RefreshAnalysis is a blocking build-dependency simulation (seconds to tens of seconds on big projects);
                // show a non-cancelable bar so the before/after checks don't look like a freeze.
                EditorUtility.DisplayProgressBar(L.Tr("Extract to shared group", "提取到公共 group"),
                    L.Tr("Checking current duplicate count (Addressables Analyze)…", "正在复检当前重复数（Addressables Analyze）…"), 0.05f);
                int before = CountOfficialDuplicates(settings);

                var group = EnsureSharedGroup(settings, out string groupErr);
                if (group == null) return FixResult.Fail(groupErr);

                int extracted = 0, skippedReadOnly = 0, failed = 0;
                string firstError = null;
                var paths = assetPaths.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
                for (int i = 0; i < paths.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L.Tr("Extract to shared group", "提取到公共 group"),
                            $"{i + 1}/{paths.Count}  {paths[i]}", (float)i / paths.Count))
                        break; // user cancelled — persist what we have and report
                    switch (TryExtractOne(settings, group, paths[i], out string detail))
                    {
                        case ExtractOutcome.Extracted: extracted++; break;
                        case ExtractOutcome.SkippedReadOnly: skippedReadOnly++; break;
                        default: failed++; if (firstError == null) firstError = detail; break;
                    }
                }

                EditorUtility.SetDirty(group);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();

                EditorUtility.DisplayProgressBar(L.Tr("Extract to shared group", "提取到公共 group"),
                    L.Tr("Re-checking duplicate count to measure the effect…", "正在重新复检重复数以衡量效果…"), 0.95f);
                int after = CountOfficialDuplicates(settings);
                EditorUtility.ClearProgressBar();

                // Compose an honest, categorized summary. The before/after is the load-bearing part: it tells the user
                // whether marking-addressable actually reduced duplicates for THIS project (it often won't for
                // scene-dominated or read-only-package-dominated duplication).
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(L.Tr($"Extracted {extracted} asset(s) into \"{SharedGroupName}\".", $"已提取 {extracted} 个资源到「{SharedGroupName}」。"));
                if (skippedReadOnly > 0)
                    sb.AppendLine(L.Tr($"Skipped {skippedReadOnly} read-only package asset(s) (Packages/… — Unity won't mark these Addressable; replace with a project-local copy to dedup).",
                                       $"跳过 {skippedReadOnly} 个只读包资源（Packages/… Unity 不允许标记为 Addressable；需换成项目内副本才能去重）。"));
                if (failed > 0)
                    sb.AppendLine(L.Tr($"Failed {failed}. First error: {firstError}", $"失败 {failed} 个。首个错误：{firstError}"));
                if (before >= 0 && after >= 0)
                {
                    sb.AppendLine();
                    if (after < before)
                        sb.AppendLine(L.Tr($"Duplicate dependencies dropped {before} → {after} (official Analyze). Rebuild to realize the size savings.",
                                           $"重复依赖 {before} → {after}（官方 Analyze 复检）。重新构建即可兑现体积收益。"));
                    else
                        sb.AppendLine(L.Tr($"Duplicate dependencies did NOT drop ({before} → {after}, official Analyze). In this project, marking assets Addressable didn't remove them — this is typical when the duplication is dominated by scene bundles (each scene bakes its own copy) or read-only package assets. Deduplicating these needs a different strategy (e.g. make the scenes reference the assets through Addressables, or replace shared package/demo assets with project-local Addressable copies), not this one-click action.",
                                           $"重复依赖没有下降（{before} → {after}，官方 Analyze 复检）。本项目里「设为 Addressable」并没能去掉它们——当重复大头是场景 bundle（每个场景各自烘焙一份）或只读包资源时，这很典型。这类需换策略（如让场景通过 Addressables 引用资源、或把共享的包/Demo 资源换成项目内的 Addressable 副本），不是这个一键动作能解决的。"));
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine(L.Tr("Verify the effect via Window > Asset Management > Addressables > Analyze > Check Duplicate Bundle Dependencies.",
                                       "请用 Window > Asset Management > Addressables > Analyze > Check Duplicate Bundle Dependencies 复检效果。"));
                }

                bool anyDone = extracted > 0;
                return anyDone ? FixResult.Ok(sb.ToString().TrimEnd()) : FixResult.Fail(sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                return FixResult.Fail(L.Tr($"Batch extraction failed: {e.Message}", $"批量提取失败：{e.Message}"));
            }
        }

        private enum ExtractOutcome { Extracted, SkippedReadOnly, Failed }

        /// <summary>Creates or moves one asset into the shared group. Categorizes read-only package assets (CreateOrMoveEntry returns null and the path is under Packages/) as skipped, not failed.</summary>
        private static ExtractOutcome TryExtractOne(AddressableAssetSettings settings, AddressableAssetGroup group, string assetPath, out string detail)
        {
            detail = null;
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                detail = L.Tr($"Asset GUID not found: {assetPath}", $"找不到资源 GUID：{assetPath}");
                return ExtractOutcome.Failed;
            }
            // postEvent=true is critical: when false the entry does not trigger group persistence and the window does not refresh.
            var entry = settings.CreateOrMoveEntry(guid, group, false, true);
            if (entry != null) return ExtractOutcome.Extracted;

            // Read-only package assets (URP/built-in shaders under Packages/) can't be marked Addressable — that's a skip, not a failure.
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                detail = L.Tr($"Read-only package asset: {assetPath}", $"只读包资源：{assetPath}");
                return ExtractOutcome.SkippedReadOnly;
            }
            detail = L.Tr($"Could not make Addressable: {assetPath}", $"无法设为 Addressable：{assetPath}");
            return ExtractOutcome.Failed;
        }

        /// <summary>Finds or creates the shared group with a build-participating BundledAssetGroupSchema set to Pack Separately.</summary>
        private static AddressableAssetGroup EnsureSharedGroup(AddressableAssetSettings settings, out string error)
        {
            error = null;
            var group = settings.FindGroup(SharedGroupName);
            if (group != null) return group;

            // 1.18+/2.x signature: schemasToCopy + params Type[]. BundledAssetGroupSchema is required for the group to participate in builds.
            // postEvent=true: lets Addressables mark dirty and notify the window to refresh.
            group = settings.CreateGroup(SharedGroupName, false, false, true, null, typeof(BundledAssetGroupSchema));
            if (group == null)
            {
                error = L.Tr($"Could not create/find group \"{SharedGroupName}\".", $"无法创建/找到 group「{SharedGroupName}」。");
                return null;
            }

            // Pack Separately: each shared asset gets its own bundle so callers only load the specific one they need,
            // avoiding the runtime waste of Pack Together (referencing one asset pulls in the whole shared bundle).
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema != null)
            {
                schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
                EditorUtility.SetDirty(schema);
            }
            return group;
        }

        /// <summary>
        /// Runs the official "Check Duplicate Bundle Dependencies" and returns the number of duplicate result rows,
        /// or -1 if the analysis can't run. Used only to show a before/after count in ExtractMany's summary — it is a
        /// real build-dependency simulation (seconds to tens of seconds), so it runs at most twice per batch.
        /// </summary>
        private static int CountOfficialDuplicates(AddressableAssetSettings settings)
        {
            var rule = new CheckBundleDupeDependencies();
            try
            {
                var results = rule.RefreshAnalysis(settings);
                if (results == null) return -1;
                // Only rows that name a real duplicated asset count (the rule also emits summary/no-issue rows).
                return results.Count(r => r != null && !string.IsNullOrEmpty(r.resultName)
                                          && (r.resultName.Contains("Assets/") || r.resultName.Contains("Packages/")));
            }
            catch { return -1; }
            finally { try { rule.ClearAnalysis(); } catch { /* cleanup best-effort */ } }
        }
    }
}
#endif
