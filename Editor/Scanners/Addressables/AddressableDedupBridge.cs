#if PERFLINT_ADDRESSABLES
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Injects what the rest of the plugin needs to know about Addressables at domain load (same bridge pattern as
    /// <see cref="PerfLint.Core.MaterialUpgradeBridge"/>). Two probes, because addressable assets are loaded by
    /// address (a string) and so are invisible to every static reference walk:
    ///   • <see cref="DuplicateAssetMerger.AddressableEntryHook"/> — a GUID redirect can't fix an address, so the
    ///     DUP001 merge must never delete a copy that is an entry.
    ///   • <see cref="UnreferencedAssetScanner.AddressableRootsHook"/> — entries (and their dependencies) ship in
    ///     the build, so UNREF001 must not call them dead.
    /// Only compiled when the Addressables package is installed; otherwise both hooks stay null and there are no
    /// address-based loads to account for.
    /// </summary>
    [InitializeOnLoad]
    internal static class AddressableDedupBridge
    {
        static AddressableDedupBridge()
        {
            DuplicateAssetMerger.AddressableEntryHook = IsAddressableEntry;
            UnreferencedAssetScanner.AddressableRootsHook = AllEntryPaths;
        }

        /// <summary>
        /// Every asset path with an Addressables entry. Walks groups/entries rather than calling GetAllAssets,
        /// matching <see cref="AddressableCompressionScanner"/> — that shape is the one proven across the
        /// Addressables versions we support. Folder entries are returned as folders; the caller expands them.
        /// </summary>
        private static IEnumerable<string> AllEntryPaths()
        {
            var paths = new List<string>();
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null || settings.groups == null) return paths;

                foreach (var group in settings.groups)
                {
                    if (group == null || group.entries == null) continue;
                    foreach (var entry in group.entries)
                    {
                        if (entry == null) continue;
                        string p = entry.AssetPath;
                        if (!string.IsNullOrEmpty(p)) paths.Add(p);
                    }
                }
            }
            catch { /* a broken probe must not take the scan down — worst case UNREF001 stays as noisy as before */ }
            return paths;
        }

        private static bool IsAddressableEntry(string assetPath)
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null) return false;
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) return false;
                return settings.FindAssetEntry(guid) != null;
            }
            catch { return false; }
        }
    }
}
#endif
