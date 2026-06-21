#if PERFLINT_ADDRESSABLES
using System;
using UnityEditor;
using UnityEditor.AddressableAssets;
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

                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) return FixResult.Fail(L.Tr($"Asset GUID not found: {assetPath}", $"找不到资源 GUID：{assetPath}"));

                var group = settings.FindGroup(SharedGroupName);
                if (group == null)
                {
                    // 1.18+/2.x signature: schemasToCopy + params Type[]. BundledAssetGroupSchema is required for the group to participate in builds.
                    // postEvent=true: lets Addressables mark dirty and notify the window to refresh (otherwise new/changed data is not persisted and the window does not update).
                    group = settings.CreateGroup(SharedGroupName, false, false, true, null, typeof(BundledAssetGroupSchema));

                    // Set the shared-dedup group to Pack Separately: each shared asset gets its own bundle,
                    // so callers only load the specific one they need, avoiding the runtime waste of
                    // Pack Together where referencing one asset pulls in the entire shared bundle.
                    // Measured: setting Pack Separately manually reduced duplicate assets 29→2 and bundle size 9.20→8.82 MB,
                    // so we apply it automatically at group creation time.
                    var schema = group != null ? group.GetSchema<BundledAssetGroupSchema>() : null;
                    if (schema != null)
                    {
                        schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
                        EditorUtility.SetDirty(schema);
                    }
                }
                if (group == null) return FixResult.Fail(L.Tr($"Could not create/find group \"{SharedGroupName}\".", $"无法创建/找到 group「{SharedGroupName}」。"));

                // postEvent=true is critical: when false the entry does not trigger group persistence and the window does not refresh (previously caused the group to appear empty).
                var entry = settings.CreateOrMoveEntry(guid, group, false, true);
                if (entry == null)
                    return FixResult.Fail(L.Tr($"Could not make Addressable: {assetPath} (usually a read-only package asset, e.g. URP/built-in shaders under Packages/, which Unity does not allow marking; deduplicate these another way).", $"无法设为 Addressable：{assetPath}（多为只读包资源，如 Packages/ 下的 URP/内置 shader，Unity 不允许标记；这类需用别的方式去重）。"));

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
    }
}
#endif
