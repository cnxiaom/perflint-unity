#if PERFLINT_ADDRESSABLES
using UnityEditor;
using UnityEditor.AddressableAssets;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Injects an "is this asset an Addressables entry?" probe into <see cref="DuplicateAssetMerger.AddressableEntryHook"/>
    /// at domain load (same bridge pattern as <see cref="PerfLint.Core.MaterialUpgradeBridge"/>). Addressable assets are
    /// loaded by address (a string), which a GUID redirect can't fix — so the DUP001 merge must never delete a copy
    /// that is an Addressables entry. Only compiled when the Addressables package is installed; otherwise the hook
    /// stays null and there are no address-based loads to protect against.
    /// </summary>
    [InitializeOnLoad]
    internal static class AddressableDedupBridge
    {
        static AddressableDedupBridge()
        {
            DuplicateAssetMerger.AddressableEntryHook = IsAddressableEntry;
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
