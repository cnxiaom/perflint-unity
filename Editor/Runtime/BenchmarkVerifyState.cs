using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Runtime
{
    /// <summary>The user's target, as they set it. Shared so two windows cannot disagree about what is being aimed at.</summary>
    public static class PerfGoalPrefs
    {
        const string KeyFps = "PerfLint.Goal.Fps";

        public static PerfGoal Current => new PerfGoal(EditorPrefs.GetInt(KeyFps, 60));

        public static void SetFps(int fps) => EditorPrefs.SetInt(KeyFps, fps);

        /// <summary>
        /// The rates offered as targets. 30/60 cover most of mobile and console, 72 and 90 are Quest, and 120 is
        /// both high-refresh mobile and the upper VR rate. 144 and 240 are ordinary PC monitors — without them a
        /// desktop developer could not state their actual target at all, and the budget every number on the
        /// Autopilot is judged against would be wrong by design rather than by mistake.
        ///
        /// Lives here so the pickers cannot drift apart: nothing keys off a specific value, so a list that differs
        /// between two windows would show up as one of them silently offering less.
        /// </summary>
        public static readonly int[] FpsChoices = { 30, 60, 72, 90, 120, 144, 240 };
    }

    /// <summary>
    /// Everything the verify loop needs, read once: the pinned baseline, the newest measurement taken after it, how far
    /// the numbers drift on this machine, and what was recorded as changed in between.
    ///
    /// Shared rather than duplicated because loading it has a SIDE EFFECT — a comparison with no recorded fixes is
    /// filed as a drift reading — and two windows each doing that independently is two windows racing to record the
    /// same observation. <see cref="BenchmarkDrift"/> is idempotent by session id so the race is harmless, but one
    /// implementation is the only way the two can't diverge on which comparison counts.
    /// </summary>
    public sealed class BenchmarkVerifyState
    {
        public BenchmarkBaseline.Pinned Baseline { get; private set; }
        /// <summary>Newest measurement recorded after the baseline was pinned, or null when there is none.</summary>
        public BenchmarkSession After { get; private set; }
        public BenchmarkDrift.Band Drift { get; private set; } = BenchmarkDrift.Band.None;
        /// <summary>What was recorded as changed since the baseline, or null when nothing was.</summary>
        public string ChangesSummary { get; private set; }
        /// <summary>Fixes PerfLint recorded applying — the only changes a comparison may point at as a cause.</summary>
        public int RecordedFixes { get; private set; }

        /// <summary>
        /// Changes attributable to the user since the baseline was MEASURED. Cannot be named, but proves something was
        /// done — so a comparison spanning them is a verdict rather than a reading of drift, and must never be banked
        /// as one.
        ///
        /// Counts the open scene having unsaved edits as one, because it is one. Keeping that separate meant the
        /// state object knew and the REPORT did not: BenchmarkComparison.Build decides "was anything recorded" from
        /// the number it is handed, so a 20.6% triangle drop from a tree hidden in the Hierarchy still came back
        /// titled "That's this machine drifting, not a result".
        /// </summary>
        public int UserEdits { get; private set; }

        IReadOnlyList<string> FixedRules { get; set; } = Array.Empty<string>();

        /// <summary>
        /// The open scene has edits that have not been written to disk.
        ///
        /// The journal watches FILES, so an object disabled in the Hierarchy and never saved is invisible to it —
        /// and that is the ordinary way anyone tests "would hiding this help". Demonstrated end to end: hide one
        /// tree, measure, and a real 20.6% drop in triangles came back titled "That's this machine drifting, not a
        /// result", then went into the drift store as an observation of what the machine does on its own, which
        /// permanently raises the bar every later verdict has to clear.
        /// </summary>
        public bool SceneHasUnsavedChanges { get; private set; }

        /// <summary>
        /// Whether anything at all was recorded as changed. False is what makes a comparison a null comparison.
        ///
        /// An unsaved scene counts. It cannot say WHAT changed — nothing here can — but a null comparison is a claim
        /// that nothing did, and that claim is exactly what an unsaved edit falsifies.
        /// </summary>
        public bool AnythingRecorded => RecordedFixes > 0 || UserEdits > 0;

        public bool HasBaseline => Baseline != null;
        public bool HasComparison => Baseline != null && After != null;

        /// <summary>
        /// Where the most recent measurement was actually taken, versus where it says it was.
        ///
        /// Read from the newest measurement on record — the after when there is one, else the baseline — because
        /// this is about the run the user just took, not about the pair. Its ordinary value says nothing: the two
        /// agree on any project that measures one scene, and on any project whose scene plan is set, since the
        /// runner waits for the target before sampling starts.
        /// </summary>
        public BenchmarkSceneTruth.Reading SceneTruth { get; private set; }

        /// <summary>
        /// A short signature of what a screen drawn from this state would show, so a window can tell whether a redraw
        /// is needed without rebuilding it. Includes scene identity, because opening a different scene changes what may
        /// be offered without touching anything else here.
        /// </summary>
        public string Signature { get; private set; }

        public static BenchmarkVerifyState Load()
        {
            var s = new BenchmarkVerifyState { Baseline = BenchmarkBaseline.Load() };
            if (s.Baseline != null)
            {
                // Only sessions recorded AFTER the baseline was pinned: an older one would be compared against a
                // "before" that came later, which is not a before/after at all.
                //
                // What CHANGED, though, is counted from when the baseline was MEASURED, not from when it was pinned. Those are the same
                // moment when you press the button, and they are not when a finished run is promoted later — and
                // every edit in between is an edit the baseline's numbers do not describe.
                //
                // Found from the drift store rather than from the code. Two "nothing changed" comparisons agreed
                // with each other to 0.2% and were both 21% above the baseline, which is not what noise looks like;
                // the baseline's own two repetitions agreed to 0.4%, so the scene is perfectly repeatable. The
                // baseline session had been recorded at 06:53, promoted at 07:06, and the scene was edited and saved
                // in between — invisible, because the count started at 07:06. Both comparisons were then banked as
                // observations of how far this machine drifts on its own, permanently widening the bar every later
                // verdict has to clear, using a measurement of somebody's actual work.
                var describes = s.BaselineMeasuredAtUtc;
                s.After = BenchmarkRunner.LoadLatestSessionAfter(s.Baseline.PinnedAtUtc);
                // Once an after measurement exists, freeze its causes at the moment that measurement was captured.
                // Reading "since baseline" all the way to NOW made a later package refresh appear in an older result
                // and could turn a null comparison into a claimed outcome retroactively.
                var changes = s.After != null
                    ? ProjectEditJournal.Between(describes, s.AfterMeasuredAtUtc)
                    : ProjectEditJournal.Since(describes);
                s.ChangesSummary = ProjectEditJournal.Summarize(changes);
                s.RecordedFixes = ProjectEditJournal.FixCount(changes);
                s.FixedRules = ProjectEditJournal.FixedRules(changes);

                bool currentSceneDirty = UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;
                s.SceneHasUnsavedChanges = s.After != null
                    && s.After.TryGetSceneDirtyAtStart(out bool measuredDirty)
                        ? measuredDirty
                        : currentSceneDirty;
                s.UserEdits = ProjectEditJournal.UserEditCount(changes)
                              + (s.SceneHasUnsavedChanges ? 1 : 0);
                // Said on screen too, or the report shows "changed: (nothing)" above a verdict that exists because
                // something did.
                if (s.SceneHasUnsavedChanges)
                {
                    string unsaved = L.Tr("unsaved changes in the open scene", "当前场景有未保存的改动");
                    s.ChangesSummary = string.IsNullOrEmpty(s.ChangesSummary)
                        ? unsaved : s.ChangesSummary + ", " + unsaved;
                }
                s.LearnDriftFromNullComparison();

                // Scoped to THIS baseline, and with the comparison on screen excluded. Scoped because a delta is
                // meaningless without its reference point — readings taken against a replaced baseline measure the
                // distance between ITS conditions and now, which is a different quantity. Excluded because a null
                // comparison is filed as a drift reading moments earlier, so including it would make "inside the band"
                // true by construction.
                s.Drift = BenchmarkDrift.Load(s.SceneGuid, s.BaselineTicks, s.After?.Id);
            }

            // The newest measurement, whichever it is. A session refused as a baseline never gets here at all — it is
            // not pinned and it is not an after — so the Unusable case this can report is a comparison run, which is
            // exactly the one still on screen with numbers in it.
            s.SceneTruth = BenchmarkSceneTruth.Read(s.After ?? s.Baseline?.Session);

            s.Signature = string.Join("|",
                s.SceneTruth.Verdict.ToString(),
                s.Baseline?.PinnedAtUtc.Ticks.ToString() ?? "-",
                s.After?.Id ?? "-",
                (s.After?.Runs.Count ?? 0).ToString(),
                s.ChangesSummary ?? "-",
                s.RecordedFixes.ToString(),
                s.UserEdits.ToString(),
                s.SceneHasUnsavedChanges ? "dirty" : "clean",
                s.Drift.SampleCount.ToString(),
                s.BaselineDescribesSceneToMeasure() ? "1" : "0",
                // The plan is part of what a screen drawn from this state shows, and changing it changes what may be
                // offered without touching anything else here.
                PlannedSceneGuid());
            return s;
        }

        public string SceneGuid => Baseline?.Session?.Fingerprint?.sceneGuid;

        /// <summary>Identity of the baseline drift readings must be relative to. 0 when there is no baseline.</summary>
        public long BaselineTicks => Baseline?.PinnedAtUtc.Ticks ?? 0;

        /// <summary>
        /// When the baseline's numbers were actually recorded — the moment the project state it describes was true.
        ///
        /// Falls back to the pin time for a baseline with no runs, which cannot happen for a usable one but must not
        /// throw. Everything about "what changed since the baseline" is measured from here, because a session
        /// promoted twenty minutes after it was taken does not describe the twenty minutes.
        /// </summary>
        public DateTime BaselineMeasuredAtUtc
        {
            get
            {
                var runs = Baseline?.Session?.Runs;
                if (runs == null || runs.Count == 0) return Baseline?.PinnedAtUtc ?? DateTime.UtcNow;
                long earliest = long.MaxValue;
                foreach (var r in runs) if (r != null && r.startedAtUtcTicks < earliest) earliest = r.startedAtUtcTicks;
                return earliest == long.MaxValue
                    ? (Baseline?.PinnedAtUtc ?? DateTime.UtcNow)
                    : new DateTime(earliest, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// When the after state was captured. This is the upper bound for cause attribution: edits made later belong
        /// to a future comparison, never to the one already measured.
        /// </summary>
        public DateTime AfterMeasuredAtUtc
        {
            get
            {
                var runs = After?.Runs;
                if (runs == null || runs.Count == 0) return DateTime.UtcNow;
                long earliest = long.MaxValue;
                foreach (var r in runs) if (r != null && r.startedAtUtcTicks < earliest) earliest = r.startedAtUtcTicks;
                return earliest == long.MaxValue
                    ? DateTime.UtcNow
                    : new DateTime(earliest, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Whether the baseline describes the scene a measurement started now would be about. By GUID, like the
        /// fingerprint: renaming a scene file must not silently invalidate a baseline that is still about that scene.
        ///
        /// Which scene that IS depends on whether a plan is set. With one, the editor's open scene is beside the
        /// point — that is the entire reason plans exist: the game boots through Init and the numbers are about the
        /// level it loads, so what the editor happens to have open answers a different question. Without one, the
        /// open scene is the scene, exactly as before.
        /// </summary>
        public bool BaselineDescribesSceneToMeasure()
        {
            string guid = SceneGuid;
            if (string.IsNullOrEmpty(guid)) return true; // nothing to contradict — let the comparison speak

            string planned = PlannedSceneGuid();
            if (!string.IsNullOrEmpty(planned))
                return string.Equals(planned, guid, StringComparison.Ordinal);

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return false;
            return string.Equals(AssetDatabase.AssetPathToGUID(scene.path), guid, StringComparison.Ordinal);
        }

        /// <summary>
        /// GUID of the scene the plan says a run would be about, or empty when no plan is set.
        ///
        /// The target when there is one, else the start scene: a plan that names only where to boot from is a plan to
        /// measure that scene, since nothing is waited for.
        /// </summary>
        public static string PlannedSceneGuid()
        {
            var plan = BenchmarkScenePlan.Current;
            if (plan.HasTarget) return plan.TargetGuid;
            return plan.HasStart ? plan.StartGuid : "";
        }

        /// <summary>
        /// Display name of the scene a run started now would be about — the planned target, or the open scene when
        /// there is no plan. Empty when a plan names a scene that no longer exists.
        ///
        /// The empty case is load-bearing, not defensive. Falling back to the open scene there produced a screen
        /// reading "baseline is Main, but measuring Main" and offering to fix it: this method answered "the open
        /// scene" while <see cref="BaselineDescribesSceneToMeasure"/> answered "they do not match", because only one
        /// of the two fell back. A plan pointing at a deleted asset has no scene to name, and saying so is what keeps
        /// the two answers consistent.
        /// </summary>
        public static string SceneToMeasureName()
        {
            var plan = BenchmarkScenePlan.Current;
            if (plan.AnyMissing) return "";

            string planned = PlannedSceneGuid();
            if (!string.IsNullOrEmpty(planned))
            {
                string p = BenchmarkScenePlan.PathOf(planned);
                if (!string.IsNullOrEmpty(p)) return BenchmarkScenePlan.NameOf(p);
            }
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.IsValid() && !string.IsNullOrEmpty(scene.name) ? scene.name : "";
        }

        public BenchmarkComparison.Report BuildReport(PerfGoal goal) =>
            HasComparison
                ? BenchmarkComparison.Build(Baseline.Session, After, goal, ChangesSummary, RecordedFixes, Drift,
                                            UserEdits, FixedRules)
                : null;

        /// <summary>
        /// Files a comparison in which nothing was recorded as changed as an observation of drift.
        ///
        /// That is the only way to learn the quantity the verdicts need. A baseline's repetitions are a minute apart,
        /// so their spread is short-term precision; a real before/after spans however long the work took, and over that
        /// span these numbers move on their own.
        ///
        /// The gate used to be "no fixes recorded", on the reasoning that a hand edit is invisible to us. It is not:
        /// <see cref="ProjectEditJournal"/> counts the user's own file changes, and the panel was already printing
        /// them ("changed: 1 other file changes") on the very screen that called the same comparison a reading of
        /// drift. So a hand-edited script was being banked as "this is how far these numbers move when nobody touches
        /// them" — permanently widening the bar using a measurement of somebody's actual work. Anything recorded at
        /// all now disqualifies the sample.
        /// </summary>
        void LearnDriftFromNullComparison()
        {
            if (After == null || AnythingRecorded) return;
            string guid = SceneGuid;
            if (string.IsNullOrEmpty(guid)) return;

            // Built without a drift band on purpose: the raw deltas ARE the sample, and feeding the existing band in
            // would let an already-wide band suppress the rows we are trying to record.
            var probe = BenchmarkComparison.Build(Baseline.Session, After, PerfGoalPrefs.Current);
            if (!probe.HasComparison) return;

            // Content moving is proof that something happened, even when nothing was recorded as happening.
            if (ContentMoved(probe.Deltas())) return;

            BenchmarkDrift.RecordIfNew(guid, BaselineTicks, After.Id, probe.GapMinutes, probe.Deltas());
        }

        /// <summary>
        /// Whether the scene was not in the same state twice — in which case this pair is not a reading of drift.
        ///
        /// Delegates rather than deciding: the same question is asked again when the stored samples are read back,
        /// and a rule about what a drift sample IS belongs with the store that keeps them. Two copies of it would be
        /// two things to update, which is how the first version of this check ended up applying to new readings only
        /// while the poisoned ones already on disk went on widening the band.
        /// </summary>
        internal static bool ContentMoved(IReadOnlyList<KeyValuePair<string, double>> deltas) =>
            BenchmarkDrift.ContentMoved(deltas);
    }
}
