using PerfLint.L10n;

namespace PerfLint.Core
{
    /// <summary>
    /// Shared, localized copy for confirm dialogs. Centralized so every destructive / non-Edit&gt;Undo-able operation
    /// (duplicate merge, Addressables extraction and its rollback, …) shows the **same** "back up first" warning,
    /// instead of each site wording it slightly differently.
    /// </summary>
    public static class PerfLintWarnings
    {
        /// <summary>The standard "this can't be undone — back up first" line. Append to an operation-specific message.</summary>
        public static string Irreversible => L.Tr(
            "This CANNOT be undone with Edit > Undo. Make a backup or commit to version control first.",
            "此操作无法用 Edit > Undo 撤销，请先备份或提交版本控制。");
    }
}
