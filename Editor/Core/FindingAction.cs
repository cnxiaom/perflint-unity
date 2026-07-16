using System;
using System.Collections.Generic;

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

        /// <summary>
        /// Whether the UI may offer a rule-level "run all" button that fires this action across every finding of the
        /// same rule. True for actions that are homogeneous and independent (e.g. "Extract to shared group" — each
        /// finding does the same thing to its own asset). Set FALSE when the findings' actions differ per row or can't
        /// run in a loop: e.g. PKG001's disable targets a DIFFERENT module per finding (so a shared "Disable X all"
        /// label would be a lie) and each disable triggers a package re-resolve + domain reload + one-at-a-time compile
        /// verification — batching them would break mid-loop. When false, only the per-row button is shown.
        /// </summary>
        public bool AllowRuleBatch { get; }

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

        /// <summary>
        /// Optional whole-batch entry point. When set, a rule-level "run all" hands the FULL list of target asset paths
        /// to this delegate in ONE call instead of invoking <see cref="Run"/> per finding — so an implementation can
        /// batch expensive tail work (e.g. a single AssetDatabase.SaveAssets for hundreds of Addressables entries
        /// instead of one save per item) and return a categorized summary (extracted / skipped / failed). Must not
        /// show any UI. Single-item execution still uses <see cref="Run"/>.
        /// </summary>
        public Func<IReadOnlyList<string>, FixResult> BatchRun { get; }

        public bool SupportsBatchRun => BatchRun != null;

        /// <summary>
        /// Optional confirmation body for the rule-level "run all". <see cref="ConfirmMessage"/> is written for ONE
        /// finding and often names its specific asset — reusing it for a 331-item batch both misleads (the dialog
        /// appears to be about a single asset) and overflows Unity's dialog length limit (which then truncates
        /// mid-sentence and appends "see the editor log file"). When null, the UI falls back to ConfirmMessage.
        /// </summary>
        public string BatchConfirmMessage { get; }

        public FindingAction(string label, string confirmMessage, Func<FixResult> run, bool requiresPro = true,
            Func<string, FixResult> runWithChoice = null, Func<IReadOnlyList<string>, FixResult> batchRun = null,
            string batchConfirmMessage = null, bool allowRuleBatch = true)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required", nameof(label));
            Label = label;
            ConfirmMessage = confirmMessage;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            RequiresPro = requiresPro;
            RunWithChoice = runWithChoice;
            BatchRun = batchRun;
            BatchConfirmMessage = batchConfirmMessage;
            AllowRuleBatch = allowRuleBatch;
        }
    }
}
