using System;
using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// What an action wants said BEFORE its confirmation — and, when the warning is "go deal with that other thing
    /// first", where to send the user so they can. A warning that names a better next step but leaves them to find it
    /// is most of the way to being ignored.
    /// Returned by <see cref="FindingAction.Preflight"/>; the UI owns how it is presented.
    /// </summary>
    public sealed class PreflightWarning
    {
        /// <summary>The warning body. Required — a warning with no text is not a warning.</summary>
        public string Message { get; }

        /// <summary>
        /// Optional rule to open the report on when the user decides to handle that first. Only ever set it for a rule
        /// the CURRENT report actually holds; in practice the warning is derived from that rule's findings, which is
        /// what keeps the jump from dangling.
        /// </summary>
        public string JumpRuleId { get; }

        /// <summary>Optional filter text so the jump lands on the ONE finding this is about rather than all of them.</summary>
        public string JumpQuery { get; }

        /// <summary>Button label for the jump, e.g. "Go to the duplicate group".</summary>
        public string JumpLabel { get; }

        public bool HasJump => !string.IsNullOrEmpty(JumpRuleId) && !string.IsNullOrEmpty(JumpLabel);

        public PreflightWarning(string message, string jumpRuleId = null, string jumpQuery = null, string jumpLabel = null)
        {
            Message = message;
            JumpRuleId = jumpRuleId;
            JumpQuery = jumpQuery;
            JumpLabel = jumpLabel;
        }
    }

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

        /// <summary>
        /// Optional pre-flight question, asked by the UI **before** <see cref="ConfirmMessage"/>. Returns the warning
        /// to put in front of the user, or null when there is nothing to say.
        ///
        /// Exists because <see cref="ConfirmMessage"/> is written when the finding is created and cannot know the
        /// project's state at click time — least of all state owned by a DIFFERENT rule. AADUP001's extract needs to
        /// say "this asset still has a byte-identical twin, merge them first or this pair can never be merged again",
        /// which is ASSET.DUP001's business and only knowable from the last scan. Putting that inside
        /// <see cref="Run"/> was tried first and was wrong twice over: it broke the "no UI in Run" contract, and it
        /// surfaced the warning AFTER the user had read "low risk, no references modified" and pressed Run — the one
        /// piece of information that should have come first arrived after the decision.
        ///
        /// Single-item path only. A batch has no user to ask per item, so the equivalent protection belongs inside
        /// <see cref="BatchRun"/> — which is exactly what ExtractMany does by deduplicating its work list by content.
        /// Like <see cref="Run"/>, must not show UI itself.
        /// </summary>
        public Func<PreflightWarning> Preflight { get; }

        public FindingAction(string label, string confirmMessage, Func<FixResult> run, bool requiresPro = true,
            Func<string, FixResult> runWithChoice = null, Func<IReadOnlyList<string>, FixResult> batchRun = null,
            string batchConfirmMessage = null, bool allowRuleBatch = true, Func<PreflightWarning> preflight = null)
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
            Preflight = preflight;
        }
    }
}
