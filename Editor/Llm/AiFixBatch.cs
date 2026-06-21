using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;

namespace PerfLint.Llm
{
    /// <summary>
    /// Applicability classification for AI batch fixes — pure logic with zero Unity dependencies, designed for unit-test coverage
    /// (the LLM itself cannot be tested, but the deterministic gate that decides which proposals should be checked by default /
    /// grayed out must have assertions guarding it, otherwise the review window will let through proposals that should not be applied).
    ///
    /// Batch flow (since [0.21.x]): Propose each item individually to collect all proposals → the review window splits them into
    /// "applicable (checked by default) / skipped (grayed out)" according to this classification → after user confirmation only
    /// the checked items are written to disk. Unlike the old approach of "generate one, auto-write immediately", this hands the
    /// risk of "breaking something" back to the user to confirm on the diff.
    /// </summary>
    public static class AiFixBatch
    {
        /// <summary>Reason a proposal is excluded from being applied; None means applicable.</summary>
        public enum Skip
        {
            None,           // Applicable
            GenFailed,      // Generation failed / response not in expected format (!Ok)
            NoChange,       // AI determined no change is needed (usually a false positive from the rule)
            NotLocatable,   // Original snippet cannot be precisely located in the file (unsafe to replace)
        }

        /// <summary>Classifies a proposal as "applicable" or gives a specific skip reason. Evaluation order: generation failed &gt; no change &gt; not locatable.</summary>
        public static Skip Classify(ScriptFixProposal p)
        {
            if (p == null || !p.Ok) return Skip.GenFailed;
            if (p.NoChange) return Skip.NoChange;
            if (!p.Locatable) return Skip.NotLocatable;
            return Skip.None;
        }

        /// <summary>Whether the proposal can be safely applied (locatable, has actual changes, generation succeeded). The review window **enables** the checkbox based on this.</summary>
        public static bool IsApplicable(ScriptFixProposal p) => Classify(p) == Skip.None;

        /// <summary>
        /// Whether the proposal should be **checked by default**: applicable AND the semantic self-check did not flag it as "may change behavior".
        /// Proposals with semantic risk (BehaviorRisk) are still applicable and the checkbox is enabled, but they are unchecked by default —
        /// forcing the user to actively read the ⚠ risk note before deciding. This is the last human gate against "compiles but is semantically wrong".
        /// </summary>
        public static bool ShouldDefaultCheck(ScriptFixProposal p) => IsApplicable(p) && !(p != null && p.BehaviorRisk);

        /// <summary>
        /// Deduplicates by (file, line number), keeping the first entry per line and preserving input order. Use this before batch generation:
        /// the same line may have multiple findings for the same rule (e.g. two Camera.main on one line → two UPD003 findings), and the AI
        /// fixes the entire line in one pass — after the first proposal is applied the line has already changed, so the second proposal's
        /// Original no longer exists in the file → location fails (a spurious failure caused by redundancy). Generate/apply only once per line,
        /// saving one LLM call.
        /// </summary>
        public static List<Finding> DedupeByLine(IEnumerable<Finding> findings)
        {
            var seen = new HashSet<(string, int)>();
            var result = new List<Finding>();
            foreach (var f in findings ?? Enumerable.Empty<Finding>())
            {
                if (f == null) continue;
                if (seen.Add((f.CodeFile, f.CodeLine))) result.Add(f);
            }
            return result;
        }
    }
}
