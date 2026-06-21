#if PERFLINT_ADDRESSABLES
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Licensing;
using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Surfaces the one-click rollback for the AA "Extract to shared group" action (<see cref="AddressableSharedGroup"/>):
    /// since extraction has no Edit&gt;Undo, this menu removes the whole "PerfLint Shared" group, returning its assets
    /// to plain implicit dependencies. Pro-gated (it's the inverse of a Pro execution action); the item is disabled
    /// when the group doesn't exist. Lives in the Addressables sub-assembly (the menu only makes sense with the package).
    /// </summary>
    internal static class AddressableSharedGroupMenu
    {
        private const string MenuPath = "Tools/PerfLint/Revert \"PerfLint Shared\" Extraction";

        [MenuItem(MenuPath, priority = 100)]
        private static void Revert()
        {
            if (!Entitlements.RequirePro(L.Tr("Revert extraction", "回退提取"))) return;

            bool confirm = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Revert Extraction", "PerfLint — 回退提取"),
                L.Tr(
                    $"Remove the \"{AddressableSharedGroup.SharedGroupName}\" group? Its assets lose the Addressable mark and revert to plain implicit dependencies (pre-extraction state). Other groups are untouched.\n\n",
                    $"移除「{AddressableSharedGroup.SharedGroupName}」group？其中资源将失去 Addressable 标记、恢复为普通隐式依赖（提取前的状态）。其它 group 不受影响。\n\n") + PerfLintWarnings.Irreversible,
                L.Tr("Revert", "回退"), L.Tr("Cancel", "取消"));
            if (!confirm) return;

            FixResult r = AddressableSharedGroup.RevertAll();
            if (r.Success) EditorUtility.DisplayDialog(L.Tr("Reverted", "已回退"), r.Message, "OK");
            else EditorUtility.DisplayDialog(L.Tr("Rollback failed", "回退失败"), r.Message, "OK");
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool RevertValidate() => AddressableSharedGroup.SharedGroupExists();
    }
}
#endif
