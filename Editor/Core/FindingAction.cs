using System;

namespace PerfLint.Core
{
    /// <summary>
    /// A rule-level / finding-level "executable action", distinct from <see cref="IFix"/> — **not included in Fix All batch runs**; rendered by the UI as a standalone button.
    ///
    /// Intended for configuration-changing operations (e.g. extracting an asset into a shared Addressables group): such operations cannot be Unity Undone, should not be swept up in a one-click bulk fix,
    /// and require explicit confirmation. <see cref="Run"/> is compiled in the sub-assembly that owns the dependency (e.g. the Addressables module references Unity.Addressables)
    /// and is invoked by the main module UI via delegate — preserving the asmdef dependency direction (the main module does not reference optional packages).
    /// </summary>
    public sealed class FindingAction
    {
        /// <summary>Button label, e.g. "Extract to shared group".</summary>
        public string Label { get; }

        /// <summary>Confirmation dialog body text (must accurately describe how to undo — configuration-changing actions are typically not reversible via Edit &gt; Undo).</summary>
        public string ConfirmMessage { get; }

        /// <summary>Whether a Pro subscription is required to execute this action.</summary>
        public bool RequiresPro { get; }

        /// <summary>The execution delegate. Implementations must not show any UI; return the result and let the UI handle the prompt.</summary>
        public Func<FixResult> Run { get; }

        /// <summary>
        /// Optional variant that takes a user-chosen target asset path (e.g. "which duplicate copy to keep"). When set
        /// **and** the finding has a <see cref="Finding.Group"/>, the UI opens a chooser (defaulting to <see cref="Run"/>'s
        /// implicit pick) instead of a plain confirm; the selected path is passed here. Batch ("run all") still uses
        /// <see cref="Run"/> with its default pick. Like <see cref="Run"/>, must not show any UI.
        /// </summary>
        public Func<string, FixResult> RunWithChoice { get; }

        public bool SupportsTargetChoice => RunWithChoice != null;

        public FindingAction(string label, string confirmMessage, Func<FixResult> run, bool requiresPro = true,
            Func<string, FixResult> runWithChoice = null)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required", nameof(label));
            Label = label;
            ConfirmMessage = confirmMessage;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            RequiresPro = requiresPro;
            RunWithChoice = runWithChoice;
        }
    }
}
