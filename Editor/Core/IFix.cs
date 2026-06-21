namespace PerfLint.Core
{
    /// <summary>
    /// A single executable automatic fix. Design notes (from the product spec's "controllable and reversible" principle):
    /// - Preview() must be callable before Apply(), so the user can clearly see what will be changed.
    /// - Apply() returns a result; the caller is responsible for enrolling the change into Undo (the fix executor
    ///   wraps Undo.RecordObject / AssetDatabase transactions uniformly), enabling one-click undo.
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
