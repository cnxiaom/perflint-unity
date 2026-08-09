using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.Scanners;

namespace PerfLint.Runtime
{
    /// <summary>
    /// What the "where am I / what should I do" screens read: the last scan, the last measurement that still
    /// describes the open scenes, the user's target, and the ranking that follows from those three.
    ///
    /// It is one type for the same reason <see cref="BenchmarkVerifyState"/> is: the Autopilot and the main panel
    /// must not be able to answer "what is the top thing to fix" differently. Assembling it was previously spread
    /// across the main panel — merge the session in, decide whether it applies to these scenes, derive the
    /// measurement, rank — and a second window doing that from scratch is a second place for the scene rule to be
    /// slightly wrong.
    ///
    /// Read-only and side-effect free, unlike <see cref="BenchmarkVerifyState"/>, so a window may load it as often
    /// as it likes.
    /// </summary>
    public sealed class CurrentDiagnosis
    {
        /// <summary>How many recommendations a round is allowed to be. Three, because a round that changes one axis is a round whose result can be attributed.</summary>
        public const int RoundSize = 3;

        /// <summary>The static scan, or null when the project has never been scanned.</summary>
        public ScanResult Scan { get; private set; }

        /// <summary>
        /// Whether acting on this finding is a SAFE one-click fix (reversible <see cref="IFix"/>), counting the ones
        /// whose delegate was lost to disk via <see cref="Finding.WasAutoFixable"/>. Delegates so the rule lives in one
        /// place. Actions — which delete files or need a dedicated flow — are deliberately NOT one-click; see
        /// <see cref="FindingActions.IsFixable"/>.
        /// </summary>
        public bool IsOneClickFixable(Finding f) => FindingActions.IsFixable(f);

        /// <summary>The last sampling session, whether or not it applies here. Kept even when it doesn't, so a screen can say so.</summary>
        public RuntimeSessionStore.Session Runtime { get; private set; }

        /// <summary>Whether that session describes the scenes currently open. When false it is mentioned but never allowed to move a number.</summary>
        public bool RuntimeApplies { get; private set; }

        public PerfGoal Goal { get; private set; }

        /// <summary>The measured facts a ranking may reason from — <see cref="PerfMeasurement.None"/> when no applicable session exists.</summary>
        public PerfMeasurement Measurement { get; private set; }

        /// <summary>What to do next, most worth doing first. Empty when there is nothing to rank.</summary>
        public IReadOnlyList<NextStep> Steps { get; private set; } = Array.Empty<NextStep>();

        /// <summary>
        /// Reversible one-click fixes that did NOT make the ranking's top slots — "while you are here" work, kept
        /// apart from <see cref="Steps"/> rather than mixed into it.
        ///
        /// This exists because of what a measured project actually looks like. On URP 3D Sample the ranking's top
        /// three are all runtime findings (GC per frame, GPU-bound, draw calls) and every one of them is report-only,
        /// while the 266 fixes PerfLint could apply by itself sit at ranks 7, 8, 9 and 14. That is not a mistake in
        /// the ranking — NextSteps.Relevance caps import settings BELOW a failing frame-time axis on
        /// purpose, because answering a frame-rate goal with an import setting is how goal-driven ranking quietly
        /// stops being goal-driven. But it did mean the one thing this tool can do without the user was invisible in
        /// exactly the state the tool is built for. So they are surfaced separately and labelled as secondary, which
        /// keeps both statements true: this is not what is limiting you, and this is what I can do for you now.
        /// </summary>
        public IReadOnlyList<NextStep> Applicable { get; private set; } = Array.Empty<NextStep>();

        /// <summary>
        /// The other half of "below the top slots": findings whose payoff needs a decision — an <see cref="Finding.Action"/>
        /// with a confirmation, a chooser, or a verifier — rather than a reversible one-click fix.
        ///
        /// <see cref="Applicable"/> was added to rescue the one-click fixes the ranking had pushed to ranks 7-14, and it
        /// filtered on <see cref="FindingActions.IsFixable"/> — which was right for what it collected and left the
        /// Action-shaped half with nowhere to go. Anything ranked below the round and needing a decision appeared in
        /// NEITHER list, so it was simply absent from the Autopilot.
        ///
        /// That gap has a name: ASSET.AADUP001. Addressables duplicate packing is the paid headline feature, it scores
        /// 0.4 (BuildSize) × 0.65 (static) × 0.85 (needs a decision) ≈ 0.22 against ~0.55 for a CPU-axis item whenever a
        /// frame-rate goal is anything but met — so it never takes a top-three slot on a project that is missing its
        /// target, and on one real project 366 duplicate-packed assets were invisible on the screen whose whole job is
        /// to say what to do next. Listed here, never run inline: the card routes to the full panel, which owns the
        /// chooser and the verifier. See <see cref="FindingActions.NeedsDecision"/>.
        /// </summary>
        public IReadOnlyList<NextStep> Decisions { get; private set; } = Array.Empty<NextStep>();

        /// <summary>
        /// The third tier: findings with NO button at all — no fix, no action — that nonetheless carry a savings
        /// estimate. Report-only does not mean "nothing to do here"; for these it means "nothing PerfLint can do FOR
        /// you", and the finding itself says what to do.
        ///
        /// Left out of the first cut on the reasoning that these screens offer work rather than a backlog. Measured on
        /// the project that started all this, that reasoning cost more than everything it saved:
        ///
        ///     4.5 GB  91.6%  ASSET.AARES001   report-only   &lt;- invisible
        ///   249.9 MB   4.9%  ASSET.DUP001     action
        ///   178.5 MB   3.5%  ASSET.AADUP001   action
        ///
        /// Adding the Action tier surfaced 8.4% of the recoverable build size and left the other 91.6% exactly as
        /// hidden as before. <see cref="OptimizePlan.ManualGroup"/> had already learned this — its own remarks record a
        /// user reporting "29.8 MB next to the button, 0.27 MB inside the dialog" — and this is the same lesson
        /// arriving at the other window.
        ///
        /// A savings estimate is the entry condition, because it is the only thing that makes such a row worth a line:
        /// there is no button to offer, so the number IS the argument. Findings without one stay out.
        /// </summary>
        public IReadOnlyList<NextStep> Manual { get; private set; } = Array.Empty<NextStep>();

        /// <summary>
        /// The ranking is searched to the BOTTOM for the three lists below the round — there is no depth cap.
        ///
        /// There was one: 40 rules, on the reasoning that a fix ranked below that is too far down to offer unprompted.
        /// That is true of a one-click import setting and false of the tier added last, and the reference project shows
        /// why. Its ranking is 32 rules deep, and the two lists this change exists to populate live at the bottom of it:
        ///
        ///   rank  1-9   0.359-0.553   CPU-axis rules, every one of them
        ///   rank 24     0.190         PERF.TEX005      manual    934.5 MB
        ///   rank 26     0.169         ASSET.AARES001   manual      4.5 GB
        ///   rank 32     0.127         PERF.TEXSTR001   decision
        ///
        /// A cap counted in ranks cuts from that end. Fourteen more rules scoring above 0.169 would have pushed the
        /// 4.5 GB rule out of every list — and while a frame-rate goal is unmet, ANY cpu/gpu-axis rule scores at least
        /// 0.42, so fourteen new performance rules would have done it silently. The screen would have gone back to
        /// exactly the state this whole change set out to fix, without a line of code being touched.
        ///
        /// Rank IS the wrong axis to cap on here: a rule worth 4.5 GB is worth saying at rank 26 or rank 60. Each list
        /// caps itself on the thing that actually makes its rows worth a line — the manual tier on bytes (1 MB) and
        /// row count, both of which report what they dropped. Cost of removing the cap is nil: Rank's take only feeds
        /// the closing Take(), while the grouping and scoring it does are unaffected.
        /// </summary>
        const int NoDepthCap = int.MaxValue;

        /// <summary>Where the measurement stands against the target, in one sentence — including its refusals (capped, Deep Profile, unmeasured).</summary>
        public string GoalLine { get; private set; }

        /// <summary>The scan merged with an applicable session: what a findings list or an export should show.</summary>
        public ScanResult Display { get; private set; }

        public bool HasScan => Scan != null && Scan.Findings.Count > 0;
        public bool HasSteps => Steps.Count > 0;

        /// <summary>
        /// How many places applying this rule would actually change.
        ///
        /// Counted from the scan, NOT from a ranked step's Finding: the ranking groups by rule and keeps one
        /// representative, whose own Group is usually null. Reading the representative's group as the scale said "1
        /// place" for a rule covering 239 models — and a confirmation that only asks when more than one place is
        /// involved therefore did not ask at all before re-importing all 239.
        ///
        /// Counts only the ones that are genuinely fixable, because that is what will change: a rule can hold
        /// findings it deliberately withholds the fix from (PERF.MSH002 does exactly this for lightmapped models,
        /// where compression would quantize UV2 and bake seams), and those stay untouched.
        /// </summary>
        public int FixablePlaces(string ruleId) => FindingActions.FixablePlacesFor(ruleId, Scan);

        /// <summary>Findings the scan produced, merged. Empty rather than null so callers need no guard.</summary>
        public IReadOnlyList<Finding> Findings =>
            Display?.Findings ?? (IReadOnlyList<Finding>)Array.Empty<Finding>();

        /// <summary>
        /// Splits a ranking into the three lists a screen shows: the round itself, the one-click work below it, and the
        /// work below it that needs a decision.
        ///
        /// Separate from <see cref="Load"/> so the rule can be tested without a scan on disk — the split is where a
        /// whole category of finding went missing once already, and "it reads correctly" was how that survived.
        /// Anything ranked below the round that is neither fixable nor actionable is report-only and lands in no list:
        /// these are offers of work, not a backlog.
        /// </summary>
        public static void Split(IReadOnlyList<NextStep> ranked, int take,
                                 out List<NextStep> steps, out List<NextStep> applicable,
                                 out List<NextStep> decisions, out List<NextStep> manual)
        {
            steps = new List<NextStep>();
            applicable = new List<NextStep>();
            decisions = new List<NextStep>();
            manual = new List<NextStep>();
            if (ranked == null) return;

            for (int i = 0; i < ranked.Count; i++)
            {
                var step = ranked[i];
                if (step?.Finding == null) continue;
                if (i < take) steps.Add(step);
                else if (FindingActions.IsFixable(step.Finding)) applicable.Add(step);
                // NeedsDecision already excludes everything fixable, so these three cannot double-count.
                else if (FindingActions.NeedsDecision(step.Finding)) decisions.Add(step);
                // No button of any kind. It earns a line only by carrying a number — see Manual's remarks for the
                // project where the un-buttoned rule was 91.6% of the recoverable build size.
                else if (step.HasEstimate) manual.Add(step);
            }
        }

        public static CurrentDiagnosis Load(int take = RoundSize)
        {
            var restored = ScanResultStore.Load();
            var d = new CurrentDiagnosis
            {
                Scan = restored?.Result,
                Runtime = RuntimeSessionStore.Load(),
                Goal = PerfGoalPrefs.Current
            };

            var scenes = RuntimeSessionStore.ScenesInScope();
            d.RuntimeApplies = RuntimeSessionStore.Applies(d.Runtime, scenes);
            d.Display = RuntimeSessionStore.Merge(d.Scan, d.Runtime, scenes);
            d.Measurement = d.RuntimeApplies ? d.Runtime.ToMeasurement() : PerfMeasurement.None;
            d.GoalLine = NextSteps.Headline(d.Goal, d.Measurement);

            if (d.Display != null && d.Display.Findings.Count > 0)
            {
                // Ranked ONCE, then sliced: Rank sorts and takes, so the first N of a deeper ranking are exactly
                // what ranking to N would have produced — and the tail is then free to search for applicable work.
                var ranked = NextSteps.Rank(d.Display.Findings, d.Goal, d.Measurement, NoDepthCap);
                Split(ranked, take, out var steps, out var applicable, out var decisions, out var manual);
                d.Steps = steps;
                d.Applicable = applicable;
                d.Decisions = decisions;
                d.Manual = manual;
            }

            return d;
        }

        /// <summary>
        /// A short signature of what a screen drawn from this would show, so a window can skip a rebuild.
        /// Includes the goal, because changing the target changes every verdict on screen without touching anything
        /// else here — a bug this project has already shipped once.
        /// </summary>
        public string Signature
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(Scan?.CompletedAtUtc.Ticks ?? 0).Append('|')
                  .Append(Runtime?.CapturedAtUtc.Ticks ?? 0).Append('|')
                  .Append(RuntimeApplies ? '1' : '0').Append('|')
                  .Append(Goal.TargetFps).Append('|')
                  .Append(Steps.Count).Append('|').Append(Applicable.Count).Append('|')
                  .Append(Decisions.Count).Append('|').Append(Manual.Count);
                foreach (var s in Steps) sb.Append('|').Append(s.Finding?.RuleId);
                foreach (var s in Applicable) sb.Append('|').Append(s.Finding?.RuleId);
                foreach (var s in Decisions) sb.Append('|').Append(s.Finding?.RuleId);
                foreach (var s in Manual) sb.Append('|').Append(s.Finding?.RuleId);
                return sb.ToString();
            }
        }
    }
}
