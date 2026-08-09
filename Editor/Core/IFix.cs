namespace PerfLint.Core
{
    /// <summary>
    /// A single executable automatic fix. Design notes (from the product spec's "controllable and reversible" principle):
    /// - Preview() must be callable before Apply(), so the user can clearly see what will be changed.
    /// - Apply() returns a result. NOTE: the "fix executor wraps Undo.RecordObject uniformly" this used to describe
    ///   was never written — there is no Undo.RecordObject anywhere in the package, and an editor probe confirms the
    ///   undo group is unchanged across an Apply(). Every implementation mutates an AssetImporter and calls
    ///   SaveAndReimport, which Unity does not record. Import settings are still recoverable (version control, or
    ///   re-editing the setting), so user-facing copy says THAT rather than promising Ctrl+Z.
    ///   Implementing real undo means restoring the previous importer state AND forcing a reimport on
    ///   Undo.undoRedoPerformed; until that exists, do not reintroduce the promise.
    /// </summary>
    public interface IFix
    {
        /// <summary>A one-line description of the change to be applied, e.g. "Change texture compression format to ASTC 6x6".</summary>
        string Description { get; }

        /// <summary>A human-readable preview (diff summary) of what will change before Apply() is called. Must have no side effects.</summary>
        string Preview();

        /// <summary>Executes the fix. Implementations must not show any UI; side effects should be capturable by Unity Undo.</summary>
        FixResult Apply();
    }

    public readonly struct FixResult
    {
        public bool Success { get; }
        public string Message { get; }

        private FixResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public static FixResult Ok(string message = null) => new FixResult(true, message);
        public static FixResult Fail(string message) => new FixResult(false, message);
    }
}
