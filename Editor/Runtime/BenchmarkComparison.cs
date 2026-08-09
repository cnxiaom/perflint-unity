using System;
using System.Collections.Generic;
using System.Globalization;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Scanners;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Turns two measurements into the sentence the user asked for: "did what I just did actually help?"
    ///
    /// Everything that decides WHAT MAY BE CLAIMED already lives in <see cref="BenchmarkStats"/> and
    /// <see cref="BenchmarkFingerprint"/>; this type does not re-decide any of it. Its job is to apply those verdicts
    /// to every metric, name the ones that cannot travel to a device, and put the result into words that do not
    /// promise more than the measurement supports.
    ///
    /// Three things it must never do, each of which would be the failure that discredits the whole feature:
    /// - present a difference between two runs taken under different conditions as an improvement we caused;
    /// - hide a <see cref="DeltaVerdict.WithinNoise"/> result. "I could not measure a change" is a real answer and is
    ///   displayed as one — a tool that only ever reports wins is a tool nobody should believe;
    /// - let an editor-side frame time read as a device frame rate. See docs/goal-benchmark-loop-plan.md §3.2.
    /// </summary>
    public static class BenchmarkComparison
    {
        /// <summary>Metrics shown in a before/after, in display order. Includes the two derived figures.</summary>
        public static readonly string[] ComparedKeys =
        {
            BenchmarkMetricKeys.GameFrameTimeMs,
            BenchmarkMetricKeys.FrameTimeMs,
            BenchmarkMetricKeys.FrameTimeP95Ms,
            BenchmarkMetricKeys.GpuFrameTimeMs,
            BenchmarkMetricKeys.GameGcPerFrameBytes,
            BenchmarkMetricKeys.GcPerFrameBytes,
            BenchmarkMetricKeys.GcPerSecondBytes,
            BenchmarkMetricKeys.TotalMemoryBytes,
            BenchmarkMetricKeys.GcUsedBytes,
            BenchmarkMetricKeys.GfxUsedBytes,
            BenchmarkMetricKeys.TotalReservedBytes,
            BenchmarkMetricKeys.DrawCalls,
            BenchmarkMetricKeys.SetPassCalls,
            BenchmarkMetricKeys.Batches,
            BenchmarkMetricKeys.Triangles,
            BenchmarkMetricKeys.Vertices
        };

        /// <summary>
        /// The three figures a before/after leads with, in order of preference.
        ///
        /// Three rather than thirteen because a reader who does not already know which counters matter cannot pick the
        /// signal out of a table — and a table where ten of thirteen rows read "no measurable change" looks like the
        /// tool failed rather than like an honest answer. The rest stays one click away.
        /// </summary>
        static readonly string[] HeadlineKeys =
        {
            // The game's own frame time leads; the whole-frame figure stays behind it so a baseline recorded
            // before the split existed still has a frame-time row to show.
            BenchmarkMetricKeys.GameFrameTimeMs,
            BenchmarkMetricKeys.FrameTimeMs,
            BenchmarkMetricKeys.FrameTimeP95Ms,
            // The game's allocation leads; the process counter stays behind it so a baseline recorded before the
            // game-side figure existed still has a GC row to show rather than none.
            BenchmarkMetricKeys.GameGcPerFrameBytes,
            BenchmarkMetricKeys.GcPerFrameBytes,
            BenchmarkMetricKeys.DrawCalls,
            BenchmarkMetricKeys.SetPassCalls,
            BenchmarkMetricKeys.GpuFrameTimeMs
        };

        /// <summary>How much the frame rate must move before per-frame figures stop being directly comparable.</summary>
        const double FrameRateShiftPercent = 5.0;

        /// <summary>What the whole comparison amounts to — the one thing the panel leads with.</summary>
        public enum Outcome
        {
            /// <summary>Conditions differed, or there is nothing to compare. No number may be shown.</summary>
            Blocked,
            /// <summary>No fix was recorded in between, so the movement is a reading of drift and not a verdict on anyone's work.</summary>
            DriftReading,
            /// <summary>Work was recorded and something measurably improved.</summary>
            Proved,
            /// <summary>Work was recorded and something measurably got worse.</summary>
            Worse,
            /// <summary>Work was recorded and nothing cleared the bar. The honest outcome, and the one that must not look like an error.</summary>
            Unproven,
            /// <summary>
            /// Nothing was recorded as changed and nothing moved — which is exactly what should happen, and is a
            /// successful calibration rather than a failure to prove anything. Kept distinct from
            /// <see cref="Unproven"/> because telling somebody who changed nothing that "this measurement can't settle
            /// it, try a heavier scene" is advice about a question they never asked.
            /// </summary>
            Calibrated
        }

        public sealed class MetricRow
        {
            public string Key { get; }
            public string Label { get; }
            public BenchmarkStats.Comparison Cmp { get; }
            /// <summary>Whether a reduction here is expected to hold on the target device.</summary>
            public bool TransfersToDevice { get; }
            public string BeforeText { get; }
            public string AfterText { get; }
            /// <summary>Signed change, already rounded for display ("−32.4%"). Empty when there is no comparable number.</summary>
            public string DeltaText { get; }
            public string VerdictText { get; }
            /// <summary>A caveat that applies to this row only, or null.</summary>
            public string Note { get; }

            /// <summary>Short label plus the value pair with the unit stated once: "5.31 -> 5.22 ms".</summary>
            public string ShortLabel { get; }
            public string PairText { get; }
            /// <summary>What this figure is a property of. Drives the grouping that replaced the per-row caveat.</summary>
            public MetricScope Scope { get; }
            /// <summary>Bar length for the "before" side, 0-1, scaled so the longer of the two fills the track.</summary>
            public double BeforeFraction { get; }
            public double AfterFraction { get; }

            public MetricRow(string key, string label, BenchmarkStats.Comparison cmp, bool transfers,
                string beforeText, string afterText, string deltaText, string verdictText, string note,
                string shortLabel, string pairText, MetricScope scope,
                double beforeFraction, double afterFraction)
            {
                Key = key; Label = label; Cmp = cmp; TransfersToDevice = transfers;
                BeforeText = beforeText; AfterText = afterText; DeltaText = deltaText;
                VerdictText = verdictText; Note = note;
                ShortLabel = shortLabel; PairText = pairText; Scope = scope;
                BeforeFraction = beforeFraction; AfterFraction = afterFraction;
            }

            /// <summary>
            /// Set when this row went up for arithmetic reasons rather than because the project got worse.
            ///
            /// The GC pair is one figure times another: allocation per second = allocation per frame x frames per
            /// second. So whenever the frame rate moves, one of the two rises with nothing in the code having
            /// changed, and WHICH one depends on the project — a per-frame allocation in Update keeps bytes-per-frame
            /// flat and pushes bytes-per-second up when the frame gets faster; time-driven work does the reverse.
            /// The partner row is what tells them apart, which is why this is decided over the pair rather than per
            /// row.
            ///
            /// Measured: frame time -4.4%, GC per frame -0.2% (flat), GC per second +4.4%. The screen led with
            /// "Something moved the wrong way", explained by that +4.4%, above a 25.4% cut in triangles and an 8.6%
            /// cut in the worst 5% of frames. The +4.4% WAS the improvement, restated as a division.
            /// </summary>
            public bool ExplainedByFrameRate { get; internal set; }

            public bool Improved => Cmp.Verdict == DeltaVerdict.Improved;

            /// <summary>
            /// A measured move in the wrong direction. False for <see cref="ExplainedByFrameRate"/>, so every reader
            /// of this — the outcome, the heading, the sentence, the colour — is told the same thing once rather
            /// than each having to know about the trap.
            /// </summary>
            public bool Regressed => Cmp.Verdict == DeltaVerdict.Regressed && !ExplainedByFrameRate;

            /// <summary>True when the verdict is a measured direction either way. WithinNoise is not a change.</summary>
            public bool Moved => Improved || Regressed;

            /// <summary>
            /// A useful reading that is too editor-inclusive to decide whether the user's work helped.
            ///
            /// Total reserved memory is the whole Unity Editor process: package compilation, imports and the
            /// editor's own caches can move it independently of the scene. Its before/after remains visible, but it
            /// is deliberately neutral and cannot produce either <see cref="Outcome.Proved"/> or
            /// <see cref="Outcome.Worse"/>.
            /// </summary>
            public bool ObservationOnly => IsObservationOnly(Key);

            /// <summary>
            /// Whether this row may be shown in the colour of a good or bad result.
            ///
            /// False for a drift reading: the number moved in the better direction, but nobody made it happen, and
            /// painting that green tells the user something good just occurred. Also false for an
            /// <see cref="ObservationOnly"/> row, whose direction is context rather than a verdict.
            /// </summary>
            public bool CountsAsResult { get; internal set; }

            /// <summary>
            /// Whether the difference is large enough for a bar to communicate it.
            ///
            /// Bars are scaled against the larger side, so a 1.7% difference draws two bars that differ by 1.7% of
            /// their width — indistinguishable, and three such pairs in a row carry no information at all while
            /// looking like they should. Below this the figures are shown as text instead.
            /// </summary>
            public bool BarIsLegible => Math.Abs(Cmp.DeltaPercent) >= 8.0;
        }

        public sealed class Report
        {
            /// <summary>Rows for every metric present on both sides. Empty when <see cref="Blocker"/> is set.</summary>
            public IReadOnlyList<MetricRow> Rows { get; }
            /// <summary>Why no comparison is possible at all (fingerprint mismatch, a missing side). Null when there is one.</summary>
            public string Blocker { get; }
            /// <summary>The frame-time row, or null when frame time was not measured on both sides.</summary>
            public MetricRow Frame { get; }
            /// <summary>One sentence stating the outcome, with its reasoning. Always safe to show on its own.</summary>
            public string Headline { get; }

            /// <summary>
            /// The outcome in a few words, for use as an actual heading.
            ///
            /// <see cref="Headline"/> is a full explanatory sentence, which is right at body size next to a table and
            /// wrong at heading size: set in 19px bold it became a forty-word wall in the largest type on screen — worse
            /// to read than the table it replaced. A heading says what happened; the sentence below says why.
            /// </summary>
            public string Title { get; }
            /// <summary>Where the "after" measurement stands against the user's target, with the editor-vs-device caveat attached. Null when it can't be judged.</summary>
            public string GoalLine { get; }
            /// <summary>What changed between the two measurements, as far as we recorded it. Null when nothing was.</summary>
            public string ChangesLine { get; }
            /// <summary>How far apart the two measurements were taken. Never null when a comparison exists.</summary>
            public string GapLine { get; }
            /// <summary>What the drift band rests on — how many null comparisons have been observed, or that none have. Never null when a comparison exists.</summary>
            public string CalibrationLine { get; }
            /// <summary>Minutes between the last baseline run and the first "after" run. Used to stamp a drift sample.</summary>
            public double GapMinutes { get; }
            /// <summary>False when no drift has been observed for this scene yet, so the band is repetition spread only.</summary>
            public bool DriftCalibrated { get; }
            /// <summary>False when the baseline has a single run, so every verdict here is <see cref="DeltaVerdict.NoNoiseBand"/>.</summary>
            public bool HasNoiseBand { get; }

            /// <summary>
            /// The same two measurements compared call path by call path.
            ///
            /// Kept beside the counter rows rather than replacing them because the two answer different questions:
            /// the counters say whether the frame got cheaper, the hotspots say WHICH CODE stopped costing. The
            /// second is the one that survives this machine — see <see cref="HotspotComparison"/>.
            /// </summary>
            public HotspotComparison.Result HotspotResult { get; }

            /// <summary>
            /// Set when Deep Profile was on for both measurements: the wall-clock rows have been dropped, and this
            /// says so. Null otherwise. Shown in place of the timing figures, never alongside them — the whole point
            /// is that there are no timing figures here to caveat.
            /// </summary>
            public string TimingsInflatedNote { get; }
            public bool TimingsInflated => !string.IsNullOrEmpty(TimingsInflatedNote);

            /// <summary>
            /// Set when a repetition was interrupted by the editor: the per-frame rows have been dropped and this
            /// says so. Same shape as <see cref="TimingsInflatedNote"/>, and for the same reason — the figures it
            /// would caveat are not present, so it appears in their place rather than beside them.
            /// </summary>
            public string SampleDisturbedNote { get; }
            public bool SampleDisturbed => !string.IsNullOrEmpty(SampleDisturbedNote);

            /// <summary>
            /// Set when everything recorded between the two measurements was work a Play Mode sample structurally
            /// cannot see — build size, decided by a build rather than by a running scene.
            ///
            /// The round screen says this before the work (<see cref="RoundVisibility"/>), which is where it does the
            /// most good. This is the other end of the same wire: the button is still there, it can still be clicked,
            /// and the result it produces will be "no measurable change" for work that was never going to move a
            /// runtime figure. Without this line that verdict reads as "it didn't help".
            /// </summary>
            public string BlindRoundNote { get; }
            public bool RoundWasInvisible => !string.IsNullOrEmpty(BlindRoundNote);

            /// <summary>
            /// Set when the drift band was measured across a much shorter span than this round's, so the
            /// editor-inclusive memory rows are being shown but not judged. Same shape as the notes above.
            ///
            /// Separate from <see cref="CalibrationLine"/>, which appends it, because the Autopilot's verify screen
            /// does not show that line at all — it prints the gap and what changed, and nothing else. Folding this
            /// into the calibration prose would have left the panel silently applying a rule it never states.
            /// </summary>
            public string DriftSpanNote { get; }
            public bool DriftSpanTooShort => !string.IsNullOrEmpty(DriftSpanNote);

            /// <summary>What the whole thing amounts to. The panel picks its wording and its tone from this, not from thirteen rows.</summary>
            public Outcome Result { get; }
            /// <summary>Up to three rows to show as before/after bars. Never empty when a comparison exists.</summary>
            public IReadOnlyList<MetricRow> Highlights { get; }
            /// <summary>
            /// One sentence under the headline: what may be believed and what to do about what may not. Never null when
            /// a comparison exists — the case that needs it most is the one where nothing was proved.
            /// </summary>
            public string Advice { get; }

            public Report(IReadOnlyList<MetricRow> rows, string blocker, MetricRow frame,
                string headline, string goalLine, string changesLine, string gapLine, string calibrationLine,
                double gapMinutes, bool driftCalibrated, bool hasNoiseBand,
                Outcome result, IReadOnlyList<MetricRow> highlights, string advice, string title,
                HotspotComparison.Result hotspots = null, string timingsInflatedNote = null,
                string sampleDisturbedNote = null, string blindRoundNote = null, string driftSpanNote = null)
            {
                TimingsInflatedNote = timingsInflatedNote;
                SampleDisturbedNote = sampleDisturbedNote;
                BlindRoundNote = blindRoundNote;
                DriftSpanNote = driftSpanNote;
                Title = title;
                Rows = rows ?? Array.Empty<MetricRow>();
                Blocker = blocker; Frame = frame; Headline = headline;
                GoalLine = goalLine; ChangesLine = changesLine; GapLine = gapLine;
                CalibrationLine = calibrationLine; GapMinutes = gapMinutes;
                DriftCalibrated = driftCalibrated; HasNoiseBand = hasNoiseBand;
                Result = result; Highlights = highlights ?? Array.Empty<MetricRow>(); Advice = advice;
                HotspotResult = hotspots ?? new HotspotComparison.Result(null, null);
            }

            /// <summary>The hotspot to lead with, or null when there is no hotspot data on both sides.</summary>
            public HotspotComparison.Row LeadHotspot => HotspotResult?.Lead;

            /// <summary>Rows grouped by what they are a property of, content first — the group a reader can act on.</summary>
            public IEnumerable<KeyValuePair<MetricScope, List<MetricRow>>> ByScope()
            {
                foreach (var s in new[] { MetricScope.Content, MetricScope.Machine, MetricScope.ContentPlusEditor })
                {
                    var mine = new List<MetricRow>();
                    foreach (var r in Rows) if (r.Scope == s) mine.Add(r);
                    if (mine.Count > 0) yield return new KeyValuePair<MetricScope, List<MetricRow>>(s, mine);
                }
            }

            public int MovedCount
            {
                get { int n = 0; foreach (var r in Rows) if (r.Moved) n++; return n; }
            }

            /// <summary>
            /// Per-figure deltas, for recording this comparison as a drift sample when nothing was changed.
            ///
            /// Hotspot self-times are in here alongside the counters, on purpose. The premise this whole redirection
            /// rests on — that a hotspot's cost drifts far less than whole-frame time does — is an inference, and the
            /// only way it stops being one is if every null comparison measures it on the user's own machine, the way
            /// frame time already is.
            /// </summary>
            public List<KeyValuePair<string, double>> Deltas()
            {
                var list = new List<KeyValuePair<string, double>>(Rows.Count);
                foreach (var r in Rows)
                    if (!double.IsNaN(r.Cmp.DeltaPercent))
                        list.Add(new KeyValuePair<string, double>(r.Key, r.Cmp.DeltaPercent));
                if (HotspotResult != null) list.AddRange(HotspotResult.Deltas());
                return list;
            }

            public bool HasComparison => Blocker == null && Rows.Count > 0;

            /// <summary>Rows that moved in a measured direction, improvements first. What a summary should lead with.</summary>
            public IEnumerable<MetricRow> MovedRows
            {
                get
                {
                    foreach (var r in Rows) if (r.Improved) yield return r;
                    foreach (var r in Rows) if (r.Regressed) yield return r;
                }
            }
        }

        /// <summary>
        /// Builds the full before/after report.
        /// </summary>
        /// <param name="before">The pinned baseline.</param>
        /// <param name="after">The measurement taken after the changes.</param>
        /// <param name="goal">Used only for the goal line; it never affects a verdict.</param>
        /// <param name="changesLine">What changed in between, from <see cref="ProjectEditJournal"/>. Optional.</param>
        /// <param name="recordedFixes">
        /// How many fixes PerfLint recorded applying between the two measurements. Zero means we cannot name a cause,
        /// and the report must not imply one — a null comparison (change nothing, measure again) proved why: it
        /// reported "something in those changes cost more than it saved" when there were no changes at all.
        /// </param>
        /// <param name="drift">
        /// How far each figure has been seen to move with nothing changed on this machine, from
        /// <see cref="BenchmarkDrift"/>. Null or empty means uncalibrated, which the report states plainly rather than
        /// letting the repetition spread stand in for a quantity it does not measure.
        /// </param>
        /// <param name="userEdits">
        /// Changes to the user's own files recorded in between, which we cannot name. Zero fixes AND zero user edits
        /// is what makes a comparison a null comparison; a hand-edited script counts here, and used to count nowhere —
        /// which had this report telling somebody who had just edited a script that the movement was the machine
        /// drifting, on a screen whose own footer listed their edit.
        /// </param>
        public static Report Build(BenchmarkSession before, BenchmarkSession after, PerfGoal goal,
            string changesLine = null, int recordedFixes = 0, BenchmarkDrift.Band drift = null, int userEdits = 0,
            IReadOnlyList<string> fixedRules = null)
        {
            // Anything recorded means this is a verdict rather than an observation of drift. Naming a CAUSE still
            // needs recordedFixes — we can only point at work we did ourselves — so the two stay separate.
            bool anythingRecorded = recordedFixes > 0 || userEdits > 0;

            if (before == null || !before.HasRuns)
                return Blocked(L.Tr("there is no baseline to compare against", "还没有可对比的基线"));
            if (after == null || !after.HasRuns)
                return Blocked(L.Tr("there is no measurement to compare with the baseline", "还没有可与基线对比的新测量"));

            // Checked once rather than per metric: when the conditions differ, EVERY number differs for the same
            // reason, and thirteen identical refusals read as thirteen problems instead of one.
            if (!BenchmarkFingerprint.Comparable(before.Fingerprint, after.Fingerprint, out string reason))
                return Blocked(reason, BenchmarkFingerprint.RemedyFor(before.Fingerprint, after.Fingerprint));

            string usability = after.Fingerprint?.UsabilityWarning();
            if (!string.IsNullOrEmpty(usability))
                return Blocked(usability);

            // Deep Profile (the same on both sides — Comparable has already checked that) does not block the report.
            // It invalidates one thing precisely: wall-clock time. So every Machine-scoped row is dropped, once, with
            // one sentence saying why — rather than four rows each refusing separately — and what remains is the part
            // Deep Profile is FOR: which call paths ran, how often, and how much they allocated.
            bool timingsInflated = after.Fingerprint?.TimingsInflated ?? false;

            bool hasNoiseBand = before.Runs.Count >= BenchmarkBaseline.MinRunsForNoiseBand
                                || after.Runs.Count >= BenchmarkBaseline.MinRunsForNoiseBand;

            // Whether per-frame figures are still directly comparable depends on whether the frame rate itself moved.
            var frameCmp = BenchmarkStats.CompareValues(BenchmarkMetricKeys.FrameTimeMs,
                BenchmarkStats.ValuesOf(before.Runs, BenchmarkMetricKeys.FrameTimeMs),
                BenchmarkStats.ValuesOf(after.Runs, BenchmarkMetricKeys.FrameTimeMs),
                drift?.Percent(BenchmarkMetricKeys.FrameTimeMs) ?? 0,
                Unsteady(before, after, BenchmarkMetricKeys.FrameTimeMs));
            bool frameRateShifted = frameCmp.Verdict != DeltaVerdict.Incomparable
                                    && Math.Abs(frameCmp.DeltaPercent) > FrameRateShiftPercent;

            // The GC pair, compared up front, because neither row can be read without the other: allocation per
            // second IS allocation per frame times frames per second. When one rises and the other did not, the rise
            // is the frame rate, not the code — see MetricRow.ExplainedByFrameRate.
            var gcPerFrameCmp = BenchmarkStats.CompareValues(BenchmarkMetricKeys.GcPerFrameBytes,
                BenchmarkStats.ValuesOf(before.Runs, BenchmarkMetricKeys.GcPerFrameBytes),
                BenchmarkStats.ValuesOf(after.Runs, BenchmarkMetricKeys.GcPerFrameBytes),
                drift?.Percent(BenchmarkMetricKeys.GcPerFrameBytes) ?? 0,
                Unsteady(before, after, BenchmarkMetricKeys.GcPerFrameBytes));
            var gcPerSecondCmp = BenchmarkStats.CompareValues(BenchmarkMetricKeys.GcPerSecondBytes,
                BenchmarkStats.ValuesOf(before.Runs, BenchmarkMetricKeys.GcPerSecondBytes),
                BenchmarkStats.ValuesOf(after.Runs, BenchmarkMetricKeys.GcPerSecondBytes),
                drift?.Percent(BenchmarkMetricKeys.GcPerSecondBytes) ?? 0,
                Unsteady(before, after, BenchmarkMetricKeys.GcPerSecondBytes));

            var rows = new List<MetricRow>();
            MetricRow frameRow = null;

            foreach (var key in ComparedKeys)
            {
                // Under Deep Profile these are the profiler's milliseconds, not the game's — and so is anything
                // divided by a frame that the profiler made three times longer. See SurvivesDeepProfile.
                if (timingsInflated && !BenchmarkMetricKeys.SurvivesDeepProfile(key)) continue;

                var b = ValuesOf(before.Runs, key);
                var a = ValuesOf(after.Runs, key);
                if (b.Count == 0 || a.Count == 0) continue; // absent counter: omit the row rather than print a zero

                var cmp = BenchmarkStats.CompareValues(key, b, a, drift?.Percent(key) ?? 0,
                                                       Unsteady(before, after, key));
                if (cmp.Verdict == DeltaVerdict.Incomparable) continue;

                // Bars are scaled against the larger of the two, so a shorter bar always means a smaller number and
                // the pair can be read without checking the axis.
                double scale = Math.Max(Math.Abs(cmp.Before), Math.Abs(cmp.After));
                double bf = scale > 0 ? Math.Abs(cmp.Before) / scale : 0;
                double af = scale > 0 ? Math.Abs(cmp.After) / scale : 0;

                bool artifact = IsFrameRateArtifact(key, cmp, gcPerFrameCmp, gcPerSecondCmp);

                var row = new MetricRow(
                    key,
                    BenchmarkMetricKeys.Label(key),
                    cmp,
                    BenchmarkMetricKeys.TransfersToDevice(key),
                    Format(key, cmp.Before),
                    Format(key, cmp.After),
                    DeltaText(cmp),
                    artifact ? ArtifactVerdictText()
                             : IsObservationOnly(key) ? ObservationVerdictText(cmp, !anythingRecorded)
                             : VerdictText(cmp, !anythingRecorded),
                    artifact ? ArtifactNote(key, gcPerFrameCmp, gcPerSecondCmp, frameCmp)
                             : NoteFor(key, frameRateShifted, frameCmp, cmp),
                    BenchmarkMetricKeys.ShortLabel(key),
                    PairText(key, cmp.Before, cmp.After),
                    BenchmarkMetricKeys.Scope(key),
                    bf, af);

                row.ExplainedByFrameRate = artifact;

                // A movement is only a "result" when something was done that could have caused it. Total reserved
                // stays neutral even then: it reads the editor process, not just the user's scene.
                row.CountsAsResult = anythingRecorded && !row.ObservationOnly;

                rows.Add(row);
                if (key == BenchmarkMetricKeys.FrameTimeMs) frameRow = row;
            }

            var hotspots = HotspotComparison.Build(before, after, drift, timingsInflated);

            // With Deep Profile on, every wall-clock row has just been dropped — so an empty row list is only fatal
            // when the call-path comparison is empty too. Blocking here on "no common counter" would refuse exactly
            // the measurement the user turned Deep Profile on to get.
            if (rows.Count == 0 && !hotspots.HasRows)
                return Blocked(L.Tr("neither measurement recorded a counter the other one has",
                                    "两次测量没有任何一个共同的计数器"));

            // A repetition interrupted by the editor cannot carry a per-frame figure, and the editor interrupting is
            // exactly what applying a batch of import fixes provokes: re-imports and shader work continue after the
            // asset database says it is idle. Observed on URP 3D Sample immediately after applying 27 fixes — one
            // repetition collected 254 frames against the baseline's 997, with a single 2,012 ms frame in it. The
            // per-frame averages built from that read as a 6% triangle increase and a tripled frame time; the
            // residency figures were rock steady and held the one real result (graphics memory down 67 MB).
            //
            // So the disturbed figures are dropped rather than caveated, the same way Deep Profile drops the timings:
            // a number that cannot mean anything is worse company for a verdict than no number.
            string disturbedNote = DisturbedNote(before, after);
            if (disturbedNote != null)
            {
                var kept = new List<MetricRow>();
                foreach (var r in rows)
                    if (BenchmarkMetricKeys.SurvivesADisturbedSample(r.Key)) kept.Add(r);
                rows = kept;
                frameRow = null;
                // Hotspot self-time is milliseconds per frame and its hit rate is a fraction OF the frames sampled —
                // both are per-frame figures, so neither survives either.
                hotspots = new HotspotComparison.Result(null, null);
            }

            bool calibrated = drift != null && drift.HasData;
            double gapMinutes = GapMinutesOf(before, after);

            // A band is a statement about a duration. Whether it covers THIS round is a separate question from
            // whether it exists, and only the editor-inclusive memory rows turn on the answer — geometry and timing
            // do not drift with the editor's allocator.
            bool memoryCalibrated = calibrated && DriftCoversGap(drift.WidestGapMinutes, gapMinutes);
            var outcome = OutcomeOf(rows, hotspots, anythingRecorded, calibrated, memoryCalibrated, frameRow);
            string blindRoundNote = BlindRoundNoteFor(fixedRules, userEdits);

            // The heading, for a round this measurement could not have seen.
            //
            // Photographed with an injected model: the note below said "a Play Mode sample cannot see build size at
            // all", and above it, in the largest type on screen, sat "I couldn't prove this change helped" — which
            // is the exact false conclusion the note exists to prevent, printed bigger than the correction.
            //
            // Only for the flavour of Unproven that means "nothing moved". A regression still leads with the
            // regression and a proved win still leads with the win: something DID move, the reader needs to know,
            // and it is no less true for being unrelated to the work. Note that an UNCALIBRATED regression is also
            // Outcome.Unproven — its heading is "Something moved the wrong way, but the bar isn't calibrated yet" —
            // so the outcome alone is not a fine enough gate. A test written at the same time as this override
            // caught it replacing exactly that heading.
            string title = Title(outcome, frameRow, rows, hotspots, calibrated, out MetricRow namedByTitle);
            if (blindRoundNote != null && outcome == Outcome.Unproven
                && !AnyRegressedWorthNaming(rows, memoryCalibrated) && !AnyHotspotRegressed(hotspots))
                title = L.Tr("This round is not something a measurement can see",
                             "这一轮的改动，本来就不是测量能看到的");

            // A win is allowed to be a win, but not silently: something that went the other way is named here rather
            // than left for the reader to spot among the rows.
            string advice = Advice(outcome, rows, hotspots, frameRow, calibrated, memoryCalibrated, out MetricRow namedByAdvice);
            if (outcome == Outcome.Proved)
            {
                var cost = WorstRegressedToName(rows, memoryCalibrated);
                if (cost != null)
                {
                    namedByAdvice = cost;
                    advice += L.Tr($" One figure went the other way: {cost.ShortLabel} {cost.DeltaText}. It is in the rows below — worth a look to confirm it is a trade you meant to make.",
                                   $" 有一项走了反方向：{cost.ShortLabel} {cost.DeltaText}。它就在下面的行里——确认一下这是不是你有意接受的取舍。");
                }

                // Keep the editor-inclusive reading visible without letting it contradict the result. GardenScene
                // cut triangles by 31.4% while Total reserved rose 2.1% after package compilation; the old report
                // discarded the direct content evidence and told the user to undo the work. This sentence makes the
                // observation explicit and pins its row, but equally explicitly says it did not decide the outcome.
                var observation = StrongestObservation(rows);
                if (observation != null)
                {
                    if (namedByAdvice == null) namedByAdvice = observation;
                    advice += L.Tr($" {observation.ShortLabel} moved {observation.DeltaText}, but it is observation-only because it includes the Unity Editor's own memory; it did not change this result.",
                                   $" {observation.ShortLabel} 变化了 {observation.DeltaText}，但它包含 Unity 编辑器自身内存，只作为观察项展示，不影响本次结论。");
                }
            }

            // The row the prose leads with is pinned into the three that get shown. Without this the guarantee was
            // only "any row that moved gets a slot", which holds until there are more movers than slots — and then
            // the sentence names the one that lost the draw. Photographed: a screen headed "Something moved the
            // wrong way", explained by "Batches moved +11.0%", above five rows not one of which was Batches. The
            // Proved branch above has claimed since it was written that the cost is "in the rows below"; it is now
            // actually true rather than usually true.
            var highlights = HighlightsOf(rows, fixedRules, namedByTitle, namedByAdvice);

            return new Report(rows, null, frameRow,
                Headline(frameRow, rows, hotspots, hasNoiseBand, anythingRecorded, recordedFixes),
                GoalLine(frameRow, goal),
                changesLine,
                GapLine(gapMinutes),
                CalibrationLine(drift, gapMinutes),
                gapMinutes,
                calibrated,
                hasNoiseBand,
                outcome,
                highlights,
                advice,
                title,
                hotspots,
                timingsInflated ? after.Fingerprint.TimingsInflatedWarning() : null,
                disturbedNote,
                blindRoundNote,
                DriftSpanNote(drift, gapMinutes));
        }

        /// <summary>
        /// Says so when every fix recorded between the two measurements was work this measurement cannot see.
        ///
        /// Three gates, and each one is a way the sentence could be a lie:
        ///   * a hand edit disqualifies it outright — we do not know what the user's own change touched, and claiming
        ///     "nothing here could have moved" over an edited script is the same overreach the drift recorder had;
        ///   * an unrecognised rule id disqualifies it — <see cref="NextSteps.AxesOfRule"/> answers None both for
        ///     "moves nothing" and for "never heard of it", so an unknown rule would otherwise be described as
        ///     build-size work on the strength of a fallback;
        ///   * nothing recorded at all disqualifies it — that is a drift reading, which has its own wording.
        /// </summary>
        static string BlindRoundNoteFor(IReadOnlyList<string> fixedRules, int userEdits)
        {
            if (userEdits > 0 || fixedRules == null || fixedRules.Count == 0) return null;

            bool aboutBuildSize = false;
            foreach (var rule in fixedRules)
            {
                if (!NextSteps.IsKnownRuleId(rule)) return null;
                var axes = NextSteps.AxesOfRule(rule);
                if (PerfAxisInfo.AnyMeasurableInPlayMode(axes)) return null;
                foreach (var a in axes) if (a == PerfAxis.BuildSize) aboutBuildSize = true;
            }

            // "what it can tell you is THAT nothing else broke" was a claim, not a scope. Photographed on the first
            // real run of this: it sat under the heading "Something moved the wrong way" and above a row reading
            // "Worst 5% of frames +6.3% worse". The sentence is about which QUESTION this comparison can answer, so
            // it asks it rather than answering it — the rows below are where the answer is.
            return aboutBuildSize
                ? L.Tr("Everything applied since the baseline was build-size work, and a Play Mode sample cannot see build size at all — it is decided by a build. So this comparison is not evidence about that work in either direction; what it can still tell you is whether anything else broke.",
                       "自基线以来应用的全部是包体类改动，而 Play Mode 采样根本看不到包体——包体由一次构建决定。所以这次对比无法为那些改动作证（无论正反）；它仍能回答的是：别的东西有没有被改坏。")
                : L.Tr("Everything applied since the baseline moves correctness rather than a figure this measurement records. So this comparison is not evidence about that work in either direction; what it can still tell you is whether anything else broke.",
                       "自基线以来应用的全部改动只影响正确性，不影响这次测量记录的任何数字。所以这次对比无法为那些改动作证（无论正反）；它仍能回答的是：别的东西有没有被改坏。");
        }

        /// <summary>The outcome in a few words. Sized to be a heading; the reasoning lives in <see cref="Report.Headline"/>.</summary>
        static string Title(Outcome outcome, MetricRow frame, IReadOnlyList<MetricRow> rows,
            HotspotComparison.Result hotspots, bool calibrated, out MetricRow named)
        {
            named = null;
            switch (outcome)
            {
                case Outcome.Proved:
                    if (frame != null && frame.Improved)
                    {
                        named = frame;
                        return L.Tr($"You cut {frame.Cmp.Before - frame.Cmp.After:0.0} ms off every frame",
                                    $"每帧省下了 {frame.Cmp.Before - frame.Cmp.After:0.0} ms");
                    }
                    foreach (var r in rows)
                        if (r.Improved && !r.ObservationOnly)
                        {
                            named = r;
                            return L.Tr($"{r.ShortLabel} is down {r.DeltaText}", $"{r.ShortLabel} 降了 {r.DeltaText}");
                        }
                    // Nothing global cleared its band but a call path did. Naming the code is a better heading than
                    // "something improved" — and it is the half of the result that describes the user's project.
                    // A heading has to be able to state its own figure. The row verdicts are already held to the
                    // milliseconds they print, but this formats to ONE decimal, so a call path that legitimately
                    // improved by 0.01 ms produced the heading "ExecuteRenderQueueJob costs 0.0 ms less per frame" --
                    // a win of zero, announced. Below what this sentence can express, it falls through to the next
                    // candidate rather than printing a number that argues against it.
                    var won = BestHotspot(hotspots, improved: true);
                    if (won != null && Math.Round(won.SelfMs.Before - won.SelfMs.After, 1) < 0.05) won = null;
                    if (won != null)
                        return L.Tr($"{won.Marker} costs {won.SelfMs.Before - won.SelfMs.After:0.0} ms less per frame",
                                    $"{won.Marker} 每帧少花了 {won.SelfMs.Before - won.SelfMs.After:0.0} ms");
                    return L.Tr("Something measurably improved", "有指标确实改善了");

                case Outcome.Worse:
                    if (frame != null && frame.Regressed)
                    {
                        named = frame;
                        return L.Tr("That made every frame slower", "这让每一帧都变慢了");
                    }
                    var lost = BestHotspot(hotspots, improved: false);
                    if (lost != null && !AnyRegressed(rows))
                        return L.Tr($"{lost.Marker} costs {lost.SelfMs.After - lost.SelfMs.Before:0.0} ms more per frame",
                                    $"{lost.Marker} 每帧多花了 {lost.SelfMs.After - lost.SelfMs.Before:0.0} ms");
                    return L.Tr("Something went the wrong way", "有指标走反了");

                case Outcome.Unproven:
                    // An uncalibrated regression lands here, and "I couldn't prove this change helped" would read as
                    // "nothing moved" when something did. Say what was seen without turning it into a verdict.
                    // Held to the same rule OutcomeOf uses, or the heading contradicts the outcome: a process-wide
                    // memory row alone is not evidence of a regression while drift is unmeasured, and this branch
                    // scanned the rows itself rather than asking. Seen after a recorded fix with everything else
                    // flat -- Total reserved +3.4% against a +-3.2% band, headed "Something moved the wrong way".
                    if (AnyRegressedWorthNaming(rows, calibrated) || AnyHotspotRegressed(hotspots))
                        return L.Tr("Something moved the wrong way — but the bar isn't calibrated yet",
                                    "有指标往反方向动了——但判定用的尺子还没校准");
                    return L.Tr("I couldn't prove this change helped", "我没能证明这次改动有效");

                case Outcome.Calibrated:
                    // The good null comparison: nothing was changed and nothing moved. Titled by what it ESTABLISHED
                    // rather than by what did not happen — "nothing changed, as it should be" is an accurate sentence
                    // that reads as a result, and a reader who did nothing and waited 70 seconds for it is entitled to
                    // ask what they got. What they got is a calibrated noise band: the bar a real fix has to clear.
                    return L.Tr("Noise band calibrated — no fix was recorded, so nothing here judges one",
                                "已校准噪声带——期间没有记录到修复，所以这次判定不了任何改动");

                case Outcome.DriftReading:
                    return L.Tr("That's this machine drifting, not a result", "这是机器自己在漂，不是结果");

                default:
                    return L.Tr("These two measurements can't be compared", "这两次测量无法对比");
            }
        }

        /// <summary>
        /// What the comparison amounts to, over the counters AND the call paths.
        ///
        /// Hotspots count toward the outcome rather than sitting in a table below it, and that is the point of having
        /// them: a change can make the method you edited measurably cheaper while whole-frame time stays inside a band
        /// widened by this machine's own wander. Judging on the counters alone would answer "I couldn't prove this
        /// change helped" to somebody whose code demonstrably got cheaper — the wrong answer, from the noisier of the
        /// two measurements.
        /// </summary>
        /// <param name="memoryCalibrated">
        /// Whether a process-wide memory counter may decide anything: a drift band exists AND it was measured across
        /// a span like this round's. See <see cref="DriftCoversGap"/> — a band is a statement about a duration, and
        /// treating it as a statement about all durations is how the editor's own allocator gets a vote.
        /// </param>
        static Outcome OutcomeOf(IReadOnlyList<MetricRow> rows, HotspotComparison.Result hotspots,
                                 bool anythingRecorded, bool driftCalibrated, bool memoryCalibrated, MetricRow frame)
        {
            bool anyMoved = false, anyDecisiveMove = false, anyWorse = false;
            foreach (var r in rows)
            {
                if (r.Moved)
                {
                    anyMoved = true;
                    if (!r.ObservationOnly) anyDecisiveMove = true;
                }
                // A process-wide memory counter reads the whole editor, whose heap grows and is collected on its own
                // schedule, so without a drift reading nothing separates that from the user's work. That was already
                // true of the improvement side; the regression side was left able to set the headline on its own.
                // Seen right after a recorded one-click fix: everything flat, Total reserved +3.4% against a +-3.2%
                // band, and the screen read "Something moved the wrong way" off a 0.2 point margin on the editor's
                // own allocator. The honest heading for that run is "I couldn't prove this change helped".
                //
                // Total reserved is weaker still: even a null-comparison band cannot isolate package compilation,
                // imports and editor caches from the scene. It is observation-only at all times, so it can never
                // veto a clear geometry, timing or call-path improvement.
                if (r.Regressed && !r.ObservationOnly
                    && !(IsEditorInclusive(r.Key) && !memoryCalibrated)) anyWorse = true;
            }

            // Call paths are all in milliseconds, so they can be netted off against each other — and must be, or one
            // trivial regression outvotes every improvement in the round. Tim hid a tree: triangles -17.5%, the
            // busiest path 0.075 ms/frame cheaper, and two paths up by 0.003 and 0.005 ms. Summed, the round is
            // 0.067 ms/frame better; the verdict read "Something moved the wrong way".
            //
            // Netting is only honest within one unit. Metric rows above are triangles, bytes and milliseconds at
            // once — there is no sum to take — so a regression there still counts on its own.
            double gained = 0, lost = 0;
            if (hotspots != null)
                foreach (var h in hotspots.Actionable)
                {
                    if (!h.Moved) continue;
                    anyMoved = true;
                    anyDecisiveMove = true;
                    double d = h.SelfMs.After - h.SelfMs.Before;
                    if (d > 0) lost += d; else gained += -d;
                }
            if (lost > gained) anyWorse = true;

            // With nothing recorded — no fix of ours AND no edit of theirs — there is nothing to pass judgement on,
            // whichever way the numbers went; and when nothing moved either, that is the calibration working rather
            // than a failure to prove something. The test used to be "no fix of ours", which called a hand-edited
            // script drift.
            if (!anythingRecorded) return anyMoved ? Outcome.DriftReading : Outcome.Calibrated;

            // A REGRESSION verdict is not earned without a measured drift band, and this is the one place the two
            // directions are deliberately not symmetric.
            //
            // Without a drift reading the band is only the spread of the baseline's own repetitions, taken about a
            // minute apart — which says how repeatable the measurement is, not how far these numbers wander over the
            // half hour it takes to actually do the work. The report already tells the user exactly that in its
            // calibration line. It then used to contradict itself in the headline: observed on URP 3D Sample, with
            // no drift reading on the machine, compressing 24 textures and clearing 3 Read/Write flags came back as
            // "Something went the wrong way — Managed heap went the wrong way. Undo the changes one at a time." The
            // managed heap is an editor-inclusive figure that had grown 81 MB over 31 minutes; triangles were up 6%
            // and draw calls 3%, which no import setting can cause. Nothing in it was attributable to the change.
            //
            // The asymmetry is about what being wrong costs. A false "improved" leaves a good change in place and
            // wastes nothing but confidence. A false "went the wrong way" instructs someone to UNDO work that was
            // correct — and the advice literally says to undo it one at a time. So without calibration the regression
            // is reported as unproven, whose advice already asks for the one run that would settle it.
            // A regression somewhere does not overrule a proven improvement in the figure the user set a goal
            // against. anyWorse was tested first and won over everything, so a run that cut 4.3 ms off every frame
            // — 41% down, far beyond its band — came back titled "Something moved the wrong way", because GC alloc
            // per SECOND had risen 70%. Which is what happens when the frame rate improves 41% and the same
            // per-frame allocation is divided by a shorter frame: a real rise in allocation rate, worth seeing, and
            // not the answer to "did it work". The report then printed a headline saying the change helped directly
            // above advice saying it might be a regression.
            //
            // Only the frame row gets this. Any other improvement outranking any regression would be the reverse
            // mistake, and a false "you fixed it" is how a real regression ships.
            if (frame != null && frame.Improved) return Outcome.Proved;

            if (anyWorse) return driftCalibrated ? Outcome.Worse : Outcome.Unproven;

            // The same lesson as the asymmetry above, which it only ever applied to regressions. A process-wide
            // memory counter reads the whole editor, and the editor's heap grows and is collected on a schedule of
            // its own; without a drift reading there is nothing to tell that apart from the user's work. Tim restored
            // a tree he had hidden — a change that cannot improve anything — and the only figure that moved was
            // Managed heap, −2.4% over the 19 minutes since the baseline. Calling that Proved credits the editor's
            // garbage collector to the user.
            //
            // Only when it is the ONLY evidence, and only while uncalibrated. Compressing textures really does drop
            // this figure, and once drift is measured the band covers the gap and the verdict is earned.
            if (anyMoved)
            {
                bool somethingElse = false;
                foreach (var r in rows) if (r.Moved && !IsEditorInclusive(r.Key)) somethingElse = true;
                if (hotspots != null) foreach (var h in hotspots.Actionable) if (h.Moved) somethingElse = true;
                if (!somethingElse && !memoryCalibrated) return Outcome.Unproven;
            }

            // An observation can say that the editor process moved, but not that the user's work succeeded. This
            // also prevents a calibrated Total-reserved decrease from being promoted to a win on its own.
            return anyDecisiveMove ? Outcome.Proved : Outcome.Unproven;
        }

        /// <summary>
        /// A regression this report is willing to name. Excludes a process-wide memory counter unless drift was
        /// measured across a span like this round's, for the same reason OutcomeOf does: the figure includes the
        /// editor's own heap, which moves on its own schedule, and a band from a shorter gap does not cover it.
        /// </summary>
        static bool AnyRegressedWorthNaming(IReadOnlyList<MetricRow> rows, bool memoryCalibrated)
        {
            if (rows == null) return false;
            foreach (var r in rows)
                if (r.Regressed && !r.ObservationOnly
                    && !(IsEditorInclusive(r.Key) && !memoryCalibrated)) return true;
            return false;
        }

        static bool AnyHotspotRegressed(HotspotComparison.Result hotspots)
        {
            if (hotspots == null) return false;
            foreach (var h in hotspots.Actionable) if (h.Regressed) return true;
            return false;
        }

        /// <summary>
        /// Says which measurement was interrupted and by how much, or null when both are clean.
        ///
        /// Names the frame count as well as the stall, because the frame count is what a reader can check against
        /// their own expectation — "254 frames where the baseline collected 997" is a fact anyone can act on, while
        /// "a 96x stall factor" is a statistic about a statistic.
        /// </summary>
        static string DisturbedNote(BenchmarkSession before, BenchmarkSession after)
        {
            bool b = before != null && before.Disturbed, a = after != null && after.Disturbed;
            if (!b && !a) return null;

            string which = b && a ? L.Tr("Both measurements were", "两次测量都")
                         : a ? L.Tr("The later measurement was", "后一次测量")
                             : L.Tr("The baseline was", "基线那次");
            var worst = a && (!b || after.WorstStallFactor > before.WorstStallFactor) ? after : before;
            string frames = before != null && after != null && before.FewestFrames > 0
                ? L.Tr($" It collected {after.FewestFrames} frames where the other collected {before.FewestFrames}.",
                       $"它只采到 {after.FewestFrames} 帧，另一次采到 {before.FewestFrames} 帧。")
                : "";

            return L.Tr(
                $"{which} interrupted by the editor — a single frame took {worst.WorstStallFactor:0} times the usual.{frames} Everything measured per frame has been dropped: frame time, allocation, draw calls and triangles are averages over the frames that happened, and these were not the same frames. What is left are the residency figures, which a pause does not change. Measure again once the editor is quiet.",
                $"{which}被编辑器打断了——某一帧耗时是平常的 {worst.WorstStallFactor:0} 倍。{frames}所有按帧统计的数字已被丢弃：帧时间、内存分配、draw call、三角形都是对「实际发生的那些帧」求平均，而这两次不是同一批帧。剩下的是常驻类数字，暂停不会改变它们。等编辑器安静下来再测一次。");
        }

        /// <summary>The hotspot that moved most in the given direction, or null.</summary>
        static HotspotComparison.Row BestHotspot(HotspotComparison.Result hotspots, bool improved)
        {
            HotspotComparison.Row best = null;
            if (hotspots == null) return null;
            foreach (var h in hotspots.Actionable)
            {
                if (improved ? !h.Improved : !h.Regressed) continue;
                double moved = Math.Abs(h.SelfMs.Before - h.SelfMs.After);
                if (best == null || moved > Math.Abs(best.SelfMs.Before - best.SelfMs.After)) best = h;
            }
            return best;
        }

        static bool AnyRegressed(IReadOnlyList<MetricRow> rows)
        {
            foreach (var r in rows) if (r.Regressed) return true;
            return false;
        }

        /// <summary>
        /// Three rows to lead with: whatever moved comes first, then the preferred order, so a result is never
        /// represented by three rows that all say "no measurable change" while a real win sits below the fold.
        /// </summary>
        /// <summary>Metric keys, by the axis they belong to, so a round can be shown the figures it was aimed at.</summary>
        static readonly (PerfAxis Axis, string[] Keys)[] KeysByAxis =
        {
            (PerfAxis.Memory, new[] { BenchmarkMetricKeys.TotalMemoryBytes, BenchmarkMetricKeys.GfxUsedBytes,
                                      BenchmarkMetricKeys.GcUsedBytes, BenchmarkMetricKeys.TotalReservedBytes }),
            (PerfAxis.Stutter, new[] { BenchmarkMetricKeys.FrameTimeP95Ms, BenchmarkMetricKeys.GameGcPerFrameBytes,
                                       BenchmarkMetricKeys.GcPerFrameBytes }),
            (PerfAxis.GpuFrameTime, new[] { BenchmarkMetricKeys.GpuFrameTimeMs, BenchmarkMetricKeys.DrawCalls,
                                            BenchmarkMetricKeys.SetPassCalls, BenchmarkMetricKeys.Triangles }),
            (PerfAxis.CpuFrameTime, new[] { BenchmarkMetricKeys.GameFrameTimeMs, BenchmarkMetricKeys.FrameTimeMs, BenchmarkMetricKeys.DrawCalls,
                                            BenchmarkMetricKeys.SetPassCalls }),
        };

        /// <summary>
        /// The order to try metric keys in, with the figures THIS round was aimed at first.
        ///
        /// HeadlineKeys is frame time, p95, GC, draw calls, SetPass, GPU — and no memory counter at all. So a round
        /// that ran a memory optimisation and moved nothing beyond its noise band showed frame time, stutter and GC:
        /// three figures the work was never about, and not one memory figure. Seen after applying PERF.TEX002 x24,
        /// with "Total used memory -3.0%" and "Graphics memory -4.5%" both present in the report and both off screen.
        ///
        /// The journal knows what was fixed, as rule ids, and the axis map knows what a rule moves. Preference only:
        /// a moved row still wins over an unmoved one, so this changes which figures fill the quiet case.
        /// </summary>
        static IEnumerable<string> PreferredKeys(IReadOnlyList<string> fixedRules)
        {
            if (fixedRules == null || fixedRules.Count == 0) return HeadlineKeys;

            var axes = new HashSet<PerfAxis>();
            foreach (var rule in fixedRules)
                foreach (var a in NextSteps.AxesOfRule(rule)) axes.Add(a);

            // Frame time keeps its place at the front whatever the round was about. It is the figure the user set a
            // goal against, and after a memory round they have two questions, not one: did memory move, and did I
            // break the frame doing it. Preferring the round's axis outright answered the first and dropped the
            // second off the screen entirely.
            var order = new List<string> { BenchmarkMetricKeys.FrameTimeMs };
            foreach (var (axis, keys) in KeysByAxis)
                if (axes.Contains(axis))
                    foreach (var k in keys) if (!order.Contains(k)) order.Add(k);
            foreach (var k in HeadlineKeys) if (!order.Contains(k)) order.Add(k);
            return order;
        }

        /// <summary>
        /// The decisive row that went the wrong way by the largest margin — the one every regression branch names.
        /// Observation-only rows are deliberately excluded: naming one as the reason to undo work would put it back
        /// in charge of the outcome through prose after the judgement logic refused it.
        /// </summary>
        /// <summary>
        /// The largest regression, for ACKNOWLEDGING that something moved — deliberately ungated.
        ///
        /// A row the verdict declined to count still moved, and refusing to name it is how "+41.4%" ends up on screen
        /// above "none of them can be told apart from doing nothing". Acknowledging movement and telling somebody to
        /// undo work over it are different acts with different burdens of proof; see <see cref="WorstRegressedToName"/>.
        /// </summary>
        static MetricRow WorstRegressed(IReadOnlyList<MetricRow> rows)
        {
            MetricRow worst = null;
            foreach (var r in rows)
                if (r.Regressed && !r.ObservationOnly
                    && (worst == null || Math.Abs(r.Cmp.DeltaPercent) > Math.Abs(worst.Cmp.DeltaPercent)))
                    worst = r;
            return worst;
        }

        /// <summary>
        /// The regression worth telling somebody to UNDO — gated exactly like the verdict itself.
        ///
        /// It must be, or the screen names a row the outcome declined to count. Measured on the museum round the
        /// moment the scope gate started excluding editor-inclusive rows: the verdict was then carried by GPU frame
        /// time, GC per second and SetPass, while the advice picked the largest delta on the page — editor-side GC at
        /// +117.5%, the one row that had just been ruled out — and said to undo the changes one at a time over it.
        /// </summary>
        static MetricRow WorstRegressedToName(IReadOnlyList<MetricRow> rows, bool memoryCalibrated)
        {
            MetricRow worst = null;
            foreach (var r in rows)
                if (r.Regressed && !r.ObservationOnly
                    && !(IsEditorInclusive(r.Key) && !memoryCalibrated)
                    && (worst == null || Math.Abs(r.Cmp.DeltaPercent) > Math.Abs(worst.Cmp.DeltaPercent)))
                    worst = r;
            return worst;
        }

        /// <summary>The largest editor-inclusive movement retained for context but excluded from the outcome.</summary>
        static MetricRow StrongestObservation(IReadOnlyList<MetricRow> rows)
        {
            MetricRow strongest = null;
            if (rows == null) return null;
            foreach (var r in rows)
                if (r.Moved && r.ObservationOnly
                    && (strongest == null || Math.Abs(r.Cmp.DeltaPercent) > Math.Abs(strongest.Cmp.DeltaPercent)))
                    strongest = r;
            return strongest;
        }

        /// <summary>
        /// Which figures restate each other. Three slots is three pieces of information, and picking purely by the
        /// size of the movement spends them on however many near-duplicates a round happens to move most.
        ///
        /// Measured on OasisScene, hiding 1.19M source triangles: the slots read Frame time, Draw calls 2,520 ->
        /// 1,594 (-36.8%) and Batches 2,506 -> 1,586 (-36.7%) — two rows fourteen counts and one tenth of a point
        /// apart, saying the same thing twice — while the round's direct result, Triangles -13.4% and Vertices
        /// -14.4%, was off-screen. Eight rows had moved and the screen spent two thirds of itself on one of them.
        ///
        /// Only a preference: a second pass drops the constraint rather than leave a slot empty, because a screen
        /// that shows two related figures still beats a screen that shows two.
        /// </summary>
        static string FamilyOf(string key)
        {
            if (key == BenchmarkMetricKeys.FrameTimeMs || key == BenchmarkMetricKeys.FrameTimeP95Ms ||
                key == BenchmarkMetricKeys.GpuFrameTimeMs) return "timing";
            if (key == BenchmarkMetricKeys.GcPerFrameBytes || key == BenchmarkMetricKeys.GcPerSecondBytes)
                return "gcRate";
            if (IsEditorInclusive(key)) return "memory";
            if (key == BenchmarkMetricKeys.DrawCalls || key == BenchmarkMetricKeys.SetPassCalls ||
                key == BenchmarkMetricKeys.Batches) return "calls";
            if (key == BenchmarkMetricKeys.Triangles || key == BenchmarkMetricKeys.Vertices) return "geometry";
            return key;
        }

        static List<MetricRow> HighlightsOf(IReadOnlyList<MetricRow> rows, IReadOnlyList<string> fixedRules = null,
            MetricRow titlePin = null, MetricRow advicePin = null)
        {
            var preferred = PreferredKeys(fixedRules);
            var picked = new List<MetricRow>();
            void Pin(MetricRow pin)
            {
                if (pin == null || rows == null || picked.Count >= 3) return;
                foreach (var r in rows)
                    if (ReferenceEquals(r, pin) && !picked.Contains(pin)) { picked.Add(pin); break; }
            }

            // Whether a figure saying this same thing is already on screen. The pins are exempt by construction:
            // they are what the prose above the table is about, and dropping one would put the claim's evidence
            // off-screen — the very thing the pinning exists to prevent.
            bool FamilyTaken(MetricRow r)
            {
                string family = FamilyOf(r.Key);
                foreach (var p in picked)
                    if (string.Equals(FamilyOf(p.Key), family, StringComparison.Ordinal)) return true;
                return false;
            }

            bool knowsWhatChanged = fixedRules != null && fixedRules.Count > 0;
            if (!knowsWhatChanged)
            {
                // A hand edit has no rule id for PreferredKeys to translate into an axis. The old fallback walked
                // HeadlineKeys, so a real GardenScene round that hid 330K roof triangles showed frame time, p95 and
                // draw calls while the direct result — Triangles 560,489 -> 369,754 (-34.0%, band +/-1.55%) — was
                // off-screen. Keep the heading's evidence, then spend the remaining slots on the largest measured
                // moves rather than whichever generic counters happen to occur first.
                Pin(titlePin);
                Pin(advicePin);
                for (int pass = 0; pass < 2 && picked.Count < 3; pass++)
                {
                    bool distinctFamilies = pass == 0;
                    while (picked.Count < 3)
                    {
                        MetricRow strongest = null;
                        foreach (var r in rows)
                        {
                            if (!r.Moved || picked.Contains(r)) continue;
                            if (distinctFamilies && FamilyTaken(r)) continue;
                            if (strongest == null ||
                                Math.Abs(r.Cmp.DeltaPercent) > Math.Abs(strongest.Cmp.DeltaPercent))
                                strongest = r;
                        }
                        if (strongest == null) break;
                        picked.Add(strongest);
                    }
                }
            }
            else
            {
                // Whatever the sentence above the table is about goes in the table, first. A claim whose evidence is
                // off-screen is the reader's problem to resolve, and they cannot. The title row is already represented
                // by the round-aware preference order; preserving that order keeps frame time first for scoped fixes.
                Pin(advicePin);
            }

            void Take(Func<MetricRow, bool> want)
            {
                foreach (var key in preferred)
                {
                    if (picked.Count >= 3) return;
                    foreach (var r in rows)
                        if (r.Key == key && want(r) && !picked.Contains(r)) { picked.Add(r); break; }
                }
            }
            // Preferred order lists draw calls and SetPass calls next to each other, so the same duplication is
            // reachable here: a fixed round would spend two slots restating one figure before geometry got one.
            if (knowsWhatChanged) { Take(r => r.Moved && !FamilyTaken(r)); Take(r => r.Moved); }

            // Then ANY row that moved, preferred list or not. Without this the screen states a verdict and then
            // shows only rows that did not earn it: observed on GardenScene, where the heading read "Something moved
            // the wrong way" above three rows all saying "no measurable change", because the two rows that actually
            // cleared their bands — Total used memory (+41.4%, band ±6.1%) and Graphics memory (+2.2%, band ±1.5%)
            // — are not in HeadlineKeys and so could never be picked. A claim whose evidence is off-screen is the
            // reader's problem to resolve, and they cannot.
            foreach (var r in rows)
            {
                if (picked.Count >= 3) break;
                if (r.Moved && !picked.Contains(r) && !FamilyTaken(r)) picked.Add(r);
            }
            foreach (var r in rows)
            {
                if (picked.Count >= 3) break;
                if (r.Moved && !picked.Contains(r)) picked.Add(r);
            }

            Take(r => true);

            // Nothing in the preferred list survived (an exotic counter set) — lead with whatever exists.
            foreach (var r in rows) { if (picked.Count >= 3) break; if (!picked.Contains(r)) picked.Add(r); }
            return picked;
        }

        static double GapMinutesOf(BenchmarkSession before, BenchmarkSession after)
        {
            var last = before.Runs[before.Runs.Count - 1];
            double mins = (after.Runs[0].StartedAtUtc - last.StartedAtUtc).TotalMinutes;
            return mins > 0 ? mins : 0;
        }

        /// <summary>States how far apart the two measurements were — the span the band has to be valid across.</summary>
        static string GapLine(double mins)
        {
            string gap = mins < 2
                ? L.Tr("right after the baseline", "紧接基线之后")
                : mins < 90 ? L.Tr($"{mins:0} minutes after the baseline", $"在基线之后 {mins:0} 分钟")
                : L.Tr($"{mins / 60:0.#} hours after the baseline", $"在基线之后 {mins / 60:0.#} 小时");

            return L.Tr($"Measured {gap}.", $"本次测量{gap}。");
        }

        /// <summary>
        /// What the band rests on.
        ///
        /// Said out loud because the uncalibrated case is one where the report cannot be trusted on small differences,
        /// and the reader has no other way to know that. A baseline's repetitions are a minute apart; a before/after
        /// spans however long the work took. Until a null comparison has been observed, the difference between those
        /// two spans is unmeasured, and the report says so instead of quietly using the wrong one.
        /// </summary>
        static string CalibrationLine(BenchmarkDrift.Band drift, double gapMinutes)
        {
            if (drift == null || !drift.HasData)
                return L.Tr("Drift not measured yet on this machine, so the band below is only the spread of the baseline's own repetitions — taken about a minute apart, which does not cover how far these numbers wander over a longer gap. Treat small differences as unproven until you run one comparison with nothing changed.",
                            "本机的漂移还没测过，所以下面的噪声带只是基线自身各轮的波动——它们相隔约一分钟，不覆盖这些数字在更长时间跨度上的游走。在做过一次「什么都不改」的对比之前，小幅差异都应视为未经证实。");

            string span = drift.WidestGapMinutes < 2
                ? L.Tr("back-to-back", "背靠背")
                : drift.WidestGapMinutes < 90
                    ? L.Tr($"up to {drift.WidestGapMinutes:0} minutes apart", $"最长间隔 {drift.WidestGapMinutes:0} 分钟")
                    : L.Tr($"up to {drift.WidestGapMinutes / 60:0.#} hours apart", $"最长间隔 {drift.WidestGapMinutes / 60:0.#} 小时");

            string n = drift.SampleCount == 1
                ? L.Tr("1 comparison", "1 次对比")
                : L.Tr($"{drift.SampleCount} comparisons", $"{drift.SampleCount} 次对比");

            string line = L.Tr($"Drift on this machine measured from {n} with nothing changed ({span}). A difference has to clear that as well as the baseline's repetition spread.",
                               $"本机漂移由 {n}「什么都不改」的对比实测得出（{span}）。差异必须同时超出它和基线的重复波动才算数。");

            string spanNote = DriftSpanNote(drift, gapMinutes);
            return spanNote == null ? line : line + " " + spanNote;
        }

        /// <summary>
        /// The sentence for a band that does not reach across this round, or null when it does.
        ///
        /// Its own function because two screens need it and only one of them prints <see cref="CalibrationLine"/>:
        /// the Autopilot's verify screen shows the gap and what changed, so a rule folded into the calibration prose
        /// would be applied there and never stated there. Names the affected rows, because "uncalibrated" without a
        /// subject reads as "distrust everything on this screen", which is the opposite of what it means.
        /// </summary>
        static string DriftSpanNote(BenchmarkDrift.Band drift, double gapMinutes)
        {
            if (drift == null || !drift.HasData || DriftCoversGap(drift.WidestGapMinutes, gapMinutes)) return null;

            return L.Tr($"This round spans {gapMinutes:0} minutes, well past the {drift.WidestGapMinutes:0} the drift was measured across, so the memory figures that include the editor are treated as uncalibrated — they can neither prove nor overturn this result. Measure a comparison with nothing changed over a similar gap to make them count.",
                        $"本轮跨度 {gapMinutes:0} 分钟，远超漂移标定的 {drift.WidestGapMinutes:0} 分钟，所以包含编辑器自身内存的那几项按未标定处理——它们既不能证明也不能推翻本次结论。想让它们算数，请在相近的时间跨度上再做一次「什么都不改」的对比。");
        }

        // ── Wording ───────────────────────────────────────────

        /// <summary>
        /// The sentence under the headline: what may be believed, and what to do about what may not.
        ///
        /// The case this exists for is <see cref="Outcome.Unproven"/>. "I could not prove it" is a real answer and it
        /// will happen often, but on its own it reads as a broken tool. Naming the figure that IS trustworthy, and
        /// saying how to make the next measurement sharper, is the difference between an honest answer and a dead end.
        /// </summary>
        /// <param name="named">
        /// The row this sentence talks about, so the caller can guarantee it is one of the rows actually shown.
        /// Null when the sentence names no row.
        /// </param>
        static string Advice(Outcome outcome, IReadOnlyList<MetricRow> rows, HotspotComparison.Result hotspots,
            MetricRow frame, bool calibrated, bool memoryCalibrated, out MetricRow named)
        {
            named = null;
            switch (outcome)
            {
                case Outcome.Proved:
                {
                    // A hotspot that got cheaper AND now runs in fewer frames is the strongest thing this loop can
                    // say, because the second half is about the code rather than the machine. Led with when present.
                    var won = BestHotspot(hotspots, improved: true);
                    if (won != null && won.Hit == HotspotComparison.HitChange.Fell)
                        return L.Tr($"{won.Marker} is not only cheaper, it now runs in fewer of the sampled frames ({won.HitText}). How often a code path runs is a property of your code, not of this machine, so that part holds on a device — how many frames per second it becomes there still needs a build on real hardware.",
                                    $"{won.Marker} 不只是变便宜了，它现在出现在更少的采样帧里（{won.HitText}）。一段代码多久跑一次是你代码的属性、不是这台机器的，所以这部分在设备上同样成立——至于折算成多少帧，仍需打包上真机才知道。");

                    // Every claim about the device is a claim about the reduction, never about the absolute reading.
                    bool anyContent = false;
                    foreach (var r in rows) if (r.Improved && r.Scope == MetricScope.Content) anyContent = true;
                    return anyContent
                        ? L.Tr("What you removed came out of your own code and content, so a device sheds it too. How many frames per second that becomes on the device, only a build on real hardware can say.",
                               "省下的开销来自你自己的代码和内容，设备上同样会省。至于在设备上折算成多少帧，只有打包上真机才能知道。")
                        : L.Tr("This is the editor on this machine. The reduction is real here; whether it shows up the same way on a device needs a build on real hardware.",
                               "这是本机编辑器口径。减少量在这里是真的；设备上是否同样体现，需要打包上真机确认。");
                }

                case Outcome.Unproven:
                {
                    // Point at whatever DID clear the bar, if anything, before saying the frame-time story is unproven.
                    //
                    // A process-wide memory row is skipped while uncalibrated, because "and that part is solid" is
                    // the exact claim OutcomeOf just declined to make about it: the counter includes the editor's own
                    // heap, which is collected on its own schedule, and without a drift reading nothing separates
                    // that from the user's work. Calling it solid here would put the refused verdict back in prose.
                    MetricRow trustworthy = null, deferred = null;
                    var observation = StrongestObservation(rows);
                    foreach (var r in rows)
                    {
                        if (!r.Improved) continue;
                        if (r.ObservationOnly) continue;
                        if (calibrated || !IsEditorInclusive(r.Key)) { trustworthy = r; break; }
                        if (deferred == null) deferred = r;   // improved, but not something to lean on yet
                    }
                    string band = frame != null
                        ? L.Tr($"smaller than the ±{frame.Cmp.NoiseBandPercent:0.0}% this machine moves on its own",
                               $"比这台机器自己的 ±{frame.Cmp.NoiseBandPercent:0.0}% 波动还小")
                        : L.Tr("smaller than this machine's own variation", "比这台机器自己的波动还小");

                    // Naming frame time only when frame time is ON SCREEN. It is dropped in two situations — Deep
                    // Profile, and a sample the editor interrupted — and in both the sentence went on saying "frame
                    // time's change is smaller than this machine's own variation" about a row the reader cannot see.
                    // Observed live: a report whose every per-frame figure had been dropped still discussed frame time.
                    string rest = frame != null
                        ? L.Tr($"Frame time's change is {band}", $"而帧时间的变化{band}")
                        : L.Tr($"Every other change here is {band}", $"其余各项的变化都{band}");

                    // Unproven is reached two ways and only one of them means nothing moved. Either nothing cleared
                    // its band anywhere — where "none of it can be told apart from doing nothing" is exactly right —
                    // or something DID clear its own band and the regression verdict is being withheld for want of a
                    // drift reading, which OutcomeOf is deliberately asymmetric about.
                    //
                    // This said the first thing in both cases. Observed on GardenScene: a report carrying "Total used
                    // memory +41.4%" against a ±6.1% band, headed "Every change is smaller than the ±9.5% this
                    // machine moves on its own, so none of them can be told apart from doing nothing". Two figures on
                    // one screen contradicting each other, with nothing to tell the reader which one to believe —
                    // and the quantifier is the giveaway, because the ±9.5% is FRAME TIME's band being spoken of as
                    // if it governed rows whose own bands are far narrower.
                    // Ungated: this sentence exists to admit that something moved, which is true of an uncalibrated
                    // row too. Gating it here would restore the contradiction the test above this branch pins.
                    var past = WorstRegressed(rows);
                    named = past ?? trustworthy ?? deferred ?? observation;

                    // A band of zero is not "this figure never moves", it is "nothing has ever been measured moving
                    // it" — every repetition happened to record the same value and no drift reading covers the key.
                    // Printed as "past the ±0.0% that figure moves on its own", it read as the strongest possible
                    // claim about stability, in the same sentence that goes on to say the bar is not calibrated.
                    // Photographed on a real comparison, where an 11% change in Batches cleared a band of exactly 0.
                    string head = past != null
                        ? (past.Cmp.NoiseBandPercent >= 0.05
                            ? L.Tr($"{past.ShortLabel} moved {past.DeltaText}, past the ±{past.Cmp.NoiseBandPercent:0.0}% that figure moves on its own — so something here did change. Calling it a regression needs one thing this machine hasn't given yet. ",
                                   $"{past.ShortLabel} 变动了 {past.DeltaText}，超过了它自身 ±{past.Cmp.NoiseBandPercent:0.0}% 的波动——所以确实有东西变了。但要判定为「变差」，还差本机的一样东西。 ")
                            : L.Tr($"{past.ShortLabel} moved {past.DeltaText}, but nothing has ever been measured moving that figure on this machine — its bar is zero, so any difference at all clears it. Treat this as something to look at, not as a result. ",
                                   $"{past.ShortLabel} 变动了 {past.DeltaText}，但本机从未测到过这个数字自行波动——它的判定线是 0，任何差异都会「超出」。请把它当作值得看一眼的线索，而不是结论。 "))
                            : trustworthy != null
                            ? L.Tr($"{trustworthy.ShortLabel} did come down {trustworthy.DeltaText} and that part is solid. {rest}, so it can't be told apart from doing nothing. ",
                                   $"{trustworthy.ShortLabel} 确实降了 {trustworthy.DeltaText}，这一项是可信的。{rest}，与什么都不做无法区分。")
                            // Declining to lean on a row is not the same as that row not having moved, and saying the
                            // second while the screen shows the first in green is a contradiction the reader cannot
                            // resolve. Observed: "none of them can be told apart from doing nothing" printed directly
                            // above "Managed heap  −2.4%  improved".
                            : deferred != null
                                ? L.Tr($"{deferred.ShortLabel} did come down {deferred.DeltaText}, but that figure counts the editor's own memory, which grows and is collected on a schedule of its own — over a gap this long that drift can be larger than the change you are looking for. ",
                                       $"{deferred.ShortLabel} 确实降了 {deferred.DeltaText}，但这个数字连编辑器自身内存一起算，而编辑器的堆按自己的节奏增长与回收——间隔一长，这种漂移可能大于你要找的那个变化。")
                                : observation != null
                                    ? L.Tr($"{observation.ShortLabel} moved {observation.DeltaText}, but it is an observation rather than a verdict: it includes the Unity Editor's own memory, so package compilation, imports and editor caches can move it independently of the scene. ",
                                           $"{observation.ShortLabel} 变化了 {observation.DeltaText}，但这只是观察、不是判定：它包含 Unity 编辑器自身内存，包编译、资源导入和编辑器缓存都可能让它脱离场景独立变化。")
                                // Deliberately quotes no single band. The same mistake is documented twenty lines up
                                // — frame time's ±9.5% spoken of as if it governed rows whose own bands are far
                                // narrower — and it was fixed on the path that has a moved row to name, while this
                                // path, the one that runs when nothing moved at all, kept doing it.
                                //
                                // The rows do not share a bar and the gap between them is wide. Frame time wanders
                                // several percent on this machine; allocation inside PlayerLoop is very nearly
                                // deterministic — across three alternating windows it read 2778 B every single time
                                // while frame time moved 3.6%. Printing frame time's figure next to the word "every"
                                // tells the reader a 0.4% allocation change was swallowed by a ±3.4% band, when
                                // allocation's own bar is nothing like that wide. Each row already carries its own
                                // verdict; this sentence's job is to say they came out the same way, not to invent a
                                // shared number for it.
                                : L.Tr("Every figure here stayed inside its own bar — they are not the same bar, and each row is judged against what THAT figure does on this machine — so none of them can be told apart from doing nothing. ",
                                       "每一项都落在它自己的判定线之内——各行的判定线并不相同，每行都按「这个数字在本机会怎么波动」单独判——所以没有一项能与什么都不做区分开。");

                    if (past == null && trustworthy == null && deferred == null && observation != null)
                        return head + L.Tr("It does not decide the overall result. Judge the round from content, timing and call-path figures; measure again soon after the next change if you also want to watch this counter.",
                                           "它不参与总判定。请以内容、耗时和调用路径指标判断这一轮；如果也想观察这个计数器，下次改动后尽快复测。");

                    // Only meaningful on the "nothing moved" road into Unproven — the other one has a moved row to
                    // point at, so having no call paths is beside the point there. Whole-frame time
                    // is the noisiest figure in the report, so "we had nothing but the noisiest measurement" is worth
                    // knowing before concluding the change did nothing.
                    string noCallPaths = past == null && hotspots != null && !hotspots.HasRows && !string.IsNullOrEmpty(hotspots.Blocker)
                        ? L.Tr($" There was also no call-path detail to fall back on — {hotspots.Blocker}. Whole-frame time is the noisiest figure here; comparing the individual hotspots is far more likely to settle it.",
                               $" 而且也没有可退而求其次的调用路径细节——{hotspots.Blocker}。整帧时间是这里噪声最大的量；逐热点对比更有可能给出结论。")
                        : "";

                    return head + (calibrated
                        ? L.Tr("That does not mean the change was useless — it means this measurement can't settle it. Measure in a scene under more realistic load, where a real difference has more room to show.",
                               "这不代表改动没用，只代表这次测量下不了结论。换一个压力更接近实战的场景再测，真实差异才有足够空间显现出来。")
                        : L.Tr("Run one comparison with nothing changed first: that measures how much these numbers move on their own here, and every verdict after it gets sharper.",
                               "先做一次「什么都不改」的对比：它会测出这些数字在本机自行波动多少，之后每次判定都会更锐利。")) + noCallPaths;
                }

                case Outcome.Worse:
                {
                    var worst = WorstRegressedToName(rows, memoryCalibrated);
                    named = worst;
                    string what = worst != null ? worst.ShortLabel : L.Tr("something", "有指标");
                    return L.Tr($"{what} went the wrong way. Undo the changes one at a time and measure again — with more than one applied at once, nothing here can say which of them did it.",
                                $"{what} 走反了。请一项一项撤销并重测——同时应用了多项时，这里的数据无法指出是哪一项造成的。");
                }

                case Outcome.DriftReading:
                    return L.Tr("Nothing is wrong: this is what the numbers do when left alone, and knowing it is what lets a later verdict mean something. Apply a change and measure again right afterwards — the shorter the gap, the less drift there is to beat.",
                                "这不是出问题：这就是放着不动时数字自己的变化，而知道它是多少，才能让之后的判定有意义。改一处然后立刻复测——间隔越短，需要超过的漂移越小。");

                case Outcome.Calibrated:
                    // Nobody changed anything and nothing moved. Telling them "this measurement can't settle it, try a
                    // heavier scene" would be answering a question they never asked.
                    //
                    // Picks up where the heading stops rather than restating it. The heading now says what the run
                    // established and why it judges nothing; repeating "nothing was changed, so nothing should have
                    // moved" underneath it spent the reader's first line on a sentence they had just read — caught by
                    // photographing the screen, which is the only way a heading and its body get looked at together.
                    return L.Tr("What you bought with those 70 seconds is the bar: this reading widens the margin a real change has to clear before it counts. Go fix something, then measure again straight away.",
                                "这 70 秒换来的是那道杠：本次读数计入「真实改动必须超过的幅度」，之后的判定才有依据。现在去修一处，然后立刻复测。");

                default:
                    return null;
            }
        }

        static string Headline(MetricRow frame, IReadOnlyList<MetricRow> rows, HotspotComparison.Result hotspots,
            bool hasNoiseBand, bool anythingRecorded, int recordedFixes)
        {
            // A call path that measurably changed outranks a frame time that did not. Frame time is the noisiest
            // figure here and the one least able to carry a conclusion; leading with "no measurable change in frame
            // time" while the edited method halved would bury the answer under the weakest measurement in the report.
            if (anythingRecorded && (frame == null || !frame.Moved))
            {
                var lead = BestHotspot(hotspots, improved: false) ?? BestHotspot(hotspots, improved: true);
                if (lead != null)
                {
                    string frameNote = frame == null
                        ? ""
                        : L.Tr($" Whole-frame time did not move measurably ({frame.BeforeText} -> {frame.AfterText}) — it is the noisiest figure here, and this is exactly the case a per-call-path comparison exists for.",
                               $" 整帧时间没有可测出的变化（{frame.BeforeText} -> {frame.AfterText}）——它是这里噪声最大的量，而逐调用路径的对比正是为这种情况存在的。");
                    return lead.Sentence() + frameNote;
                }
            }

            // Nothing was recorded as changed, so there is no claim to make about anybody's work. What this
            // measurement establishes is how far the numbers move on their own — which is not a lesser result, it is
            // the calibration every later verdict is held to. Presenting it as a verdict is what produced both
            // "something in those changes cost more than it saved" (there were none) and, once the sample was fed
            // back into its own band, "no measurable change" for a 10% movement.
            if (!anythingRecorded && frame != null && frame.Moved)
                return L.Tr($"Nothing was recorded as changed, so this is a drift reading rather than a verdict: frame time moved {frame.DeltaText} on its own ({frame.BeforeText} -> {frame.AfterText}). That is now part of what a real change has to beat — and the shorter the gap between a change and its measurement, the less of it there is to beat.",
                            $"期间没有记录到任何改动，所以这是一次漂移读数、不是判定：帧时间自行变化了 {frame.DeltaText}（{frame.BeforeText} -> {frame.AfterText}）。它已计入「真实改动必须超过的幅度」——而改动与测量之间的间隔越短，需要超过的幅度就越小。");

            if (frame == null)
            {
                int improved = 0, regressed = 0;
                foreach (var r in rows) { if (r.Improved) improved++; else if (r.Regressed) regressed++; }
                if (improved == 0 && regressed == 0)
                    return L.Tr("No measurable change in anything that was measured.", "所有测到的指标都没有可测出的变化。");
                return improved > 0 && regressed == 0
                    ? L.Tr($"{improved} of {rows.Count} measured figures improved; frame time wasn't among them.",
                           $"{rows.Count} 项实测中有 {improved} 项改善；其中不含帧时间。")
                    : L.Tr($"{improved} improved, {regressed} got worse.", $"{improved} 项改善，{regressed} 项变差。");
            }

            string pair = $"{frame.BeforeText} -> {frame.AfterText}";

            switch (frame.Cmp.Verdict)
            {
                case DeltaVerdict.NoNoiseBand:
                    return L.Tr($"Frame time {pair} ({frame.DeltaText}) — but the baseline was measured only once, so there is no run-to-run spread to judge that against. Re-run the baseline at least twice to get a verdict.",
                                $"帧时间 {pair}（{frame.DeltaText}）——但基线只测了一次，没有run-to-run 波动范围可作判据。基线至少测两次才能给出结论。");

                // Neither direction may assert a cause. What the measurement establishes is that the number moved
                // further than the baseline's own repetitions did; attributing that to the user's work needs a second
                // fact, which is whether any work was recorded. Saying "something in those changes cost more than it
                // saved" to somebody who changed nothing is the most damaging thing this report can do.
                case DeltaVerdict.Improved:
                    // Led with the cost REMOVED rather than the percentage or the new absolute. Milliseconds per frame
                    // is the part that carries: it came out of the user's code and content, so the device sheds it
                    // too. "You are now at 61 FPS" would be a claim about a desktop editor dressed up as a claim about
                    // their phone — see docs/goal-benchmark-loop-plan.md §3.2.
                    return L.Tr($"You cut {frame.Cmp.Before - frame.Cmp.After:0.0} ms off every frame ({pair}). That is beyond the ±{frame.Cmp.NoiseBandPercent:0.0}% {BandName(frame.Cmp)}, so it is a real change and not noise. ",
                                $"每帧省下了 {frame.Cmp.Before - frame.Cmp.After:0.0} ms（{pair}）。这超出了 ±{frame.Cmp.NoiseBandPercent:0.0}% 的{BandName(frame.Cmp)}，所以是真实变化、不是噪声。")
                           + Attribution(anythingRecorded, recordedFixes, improved: true);

                case DeltaVerdict.Regressed:
                    return L.Tr($"Frame time is {frame.DeltaText} higher than the baseline: {pair}, further than the ±{frame.Cmp.NoiseBandPercent:0.0}% {BandName(frame.Cmp)}. ",
                                $"帧时间比基线高了 {frame.DeltaText}：{pair}，超出 ±{frame.Cmp.NoiseBandPercent:0.0}% 的{BandName(frame.Cmp)}。")
                           + Attribution(anythingRecorded, recordedFixes, improved: false);

                default:
                    // WithinNoise. Stated as "could not measure", never as "no change" — the difference matters, and
                    // this is the case where the tool has to be willing to say it failed to prove anything.
                    string suffix = hasNoiseBand
                        ? L.Tr($" The difference ({frame.DeltaText}) is inside the ±{frame.Cmp.NoiseBandPercent:0.0}% {BandName(frame.Cmp)}, so it can't be told apart from changing nothing.",
                               $"差值（{frame.DeltaText}）落在 ±{frame.Cmp.NoiseBandPercent:0.0}% 的{BandName(frame.Cmp)}之内，与什么都不改无法区分。")
                        : "";
                    return L.Tr($"No measurable change in frame time ({pair}).", $"帧时间没有可测出的变化（{pair}）。") + suffix;
            }
        }

        /// <summary>
        /// Names what the band actually is, since the two are evidence of different things and the wider one wins.
        /// Calling observed drift "run-to-run noise" would misdescribe the very measurement that makes the verdict
        /// trustworthy.
        /// </summary>
        static string BandName(BenchmarkStats.Comparison c) =>
            c.BandFromUnsteadySampling
                ? L.Tr("how far this figure moved during the sampling itself", "该指标在采样过程中自身移动的幅度")
                : c.BandFromDrift
                    ? L.Tr("this figure has been seen to drift with nothing changed", "该指标在什么都不改时被实测到的漂移幅度")
                    : L.Tr("spread of the baseline's own repetitions", "基线自身各轮的波动范围");

        /// <summary>
        /// How far a figure moved inside a single sampling window, on whichever side moved more.
        ///
        /// Both sides, because either one poisons the pair: a steady "after" compared against a baseline taken while
        /// the camera was being moved is exactly the case this exists for, and it is the one that produced a red
        /// "regressed" verdict for work nobody did.
        /// </summary>
        static double Unsteady(BenchmarkSession before, BenchmarkSession after, string key) =>
            Math.Max(BenchmarkStats.WithinRunSwingPercent(before?.Runs, key),
                     BenchmarkStats.WithinRunSwingPercent(after?.Runs, key));

        /// <summary>
        /// What the movement may be attributed to. With no recorded work in between, drift is the likelier
        /// explanation and is named as such; with recorded work, the wording still stops short of proof.
        /// </summary>
        static string Attribution(bool anythingRecorded, int recordedFixes, bool improved)
        {
            if (!anythingRecorded)
                return L.Tr("Nothing was recorded as changed in between, so this is more likely drift between the two sessions than an effect of anything you did.",
                            "期间没有记录到任何改动，因此这更可能是两次测量之间的漂移，而不是你做了什么造成的。");

            // Something was recorded, but it was the user's own editing rather than a fix of ours. That is enough to
            // call this a result and not drift, and not enough to name what caused it — the two must not be conflated,
            // since naming a cause we cannot see is exactly how this report would start inventing them.
            if (recordedFixes <= 0)
                return improved
                    ? L.Tr("Your own edits were recorded in between. PerfLint didn't make them, so it can't say which one did this — only that the change is real and that something was done.",
                           "期间记录到你自己的改动。这些不是 PerfLint 做的，所以它说不出是哪一处造成的——只能确认变化是真的、且期间确实有人动了东西。")
                    : L.Tr("Your own edits were recorded in between. PerfLint didn't make them, so it can't say which one cost this — only that the change is real. Undo them one at a time and measure again.",
                           "期间记录到你自己的改动。这些不是 PerfLint 做的，所以它说不出是哪一处造成的——只能确认变化是真的。请一项一项撤销并重测。");

            return improved
                ? L.Tr("That is consistent with the work recorded in between having helped, on this machine.",
                       "这与期间记录到的改动起了作用相符（本机口径）。")
                : L.Tr("Something recorded in between may have cost more than it saved — or the machine drifted between the two sessions. This measurement cannot tell those apart.",
                       "期间记录到的某项改动可能开销大于收益——也可能只是两次测量之间机器状态漂移。本次测量无法区分这两者。");
        }

        /// <summary>
        /// Where the "after" reading sits against the user's target — and, in the same breath, why that is not a
        /// promise about their phone. The two halves are one string on purpose: they must never be shown apart.
        /// </summary>
        static string GoalLine(MetricRow frame, PerfGoal goal)
        {
            if (frame == null) return null;
            double after = frame.Cmp.After;
            if (double.IsNaN(after) || after <= 0) return null;

            double budget = goal.FrameBudgetMs;
            string caveat = L.Tr("This is the editor on this machine — it is not a prediction of your device's frame rate.",
                                 "这是本机编辑器口径——不是对你目标设备帧率的预测。");

            return after <= budget
                ? L.Tr($"Now inside the {budget:0.0} ms budget for {goal.TargetFps} FPS ({after:0.0} ms). {caveat}",
                       $"现在在 {goal.TargetFps} FPS 的 {budget:0.0} ms 预算之内（{after:0.0} ms）。{caveat}")
                : L.Tr($"Still over the {budget:0.0} ms budget for {goal.TargetFps} FPS ({after:0.0} ms). {caveat}",
                       $"仍然超出 {goal.TargetFps} FPS 的 {budget:0.0} ms 预算（{after:0.0} ms）。{caveat}");
        }

        /// <param name="nothingRecorded">
        /// True when no fix was recorded between the two measurements. "improved" and "worse" are verdicts about
        /// somebody's work, and there is no work here to pass judgement on — the figure moved by itself, which is a
        /// different statement and the useful one.
        /// </param>
        static string VerdictText(BenchmarkStats.Comparison c, bool nothingRecorded) => c.Verdict switch
        {
            DeltaVerdict.Improved => nothingRecorded ? L.Tr("drifted", "自行漂移") : L.Tr("improved", "改善"),
            DeltaVerdict.Regressed => nothingRecorded ? L.Tr("drifted", "自行漂移") : L.Tr("worse", "变差"),
            // An unstable metric is forced to WithinNoise, but the two are not the same statement and printing them
            // with the same words refutes itself in front of the reader: measured live, "−74.3% · no measurable
            // change", from a baseline whose three repetitions read 905 KB, 9 KB and 9 KB per frame. The delta is
            // real arithmetic on a mean that means nothing. Say which it is.
            DeltaVerdict.WithinNoise => c.Stability == MetricStability.Unstable
                ? L.Tr("too unsteady to judge", "波动过大，判不了")
                : L.Tr("no measurable change", "无可测出的变化"),
            DeltaVerdict.NoNoiseBand => L.Tr("no noise band", "无噪声基准"),
            _ => L.Tr("not comparable", "不可比")
        };

        /// <summary>
        /// Observation-only counters keep their arithmetic direction and delta, but the verdict column must not call
        /// them "improved" or "worse": those words say the user's work caused a result that this counter cannot
        /// isolate from the editor process.
        /// </summary>
        static string ObservationVerdictText(BenchmarkStats.Comparison c, bool nothingRecorded)
        {
            if (nothingRecorded || (c.Verdict != DeltaVerdict.Improved && c.Verdict != DeltaVerdict.Regressed))
                return VerdictText(c, nothingRecorded);
            return L.Tr("observation only", "仅供观察");
        }

        static string DeltaText(BenchmarkStats.Comparison c)
        {
            if (c.Verdict == DeltaVerdict.Incomparable || double.IsNaN(c.DeltaPercent)) return "";
            // U+2212 MINUS SIGN and U+002B are both present in the editor font; no emoji, no arrows that need one.
            string sign = c.DeltaPercent < 0 ? "−" : "+";
            return $"{sign}{Math.Abs(c.DeltaPercent):0.0}%";
        }

        /// <summary>
        /// Whether this row rose only because the frame rate moved.
        ///
        /// Allocation per second = allocation per frame x frames per second, so the GC pair cannot both be read as
        /// independent measurements. If one rose and the other did NOT, the code did not allocate more: the frames
        /// got shorter (or longer) and the same bytes were divided differently. Which of the two moves is a property
        /// of the project — per-frame allocations in Update keep bytes-per-frame flat and push bytes-per-second up
        /// when the frame speeds up; time-driven work does the opposite — so this is decided by looking at the
        /// partner rather than by assuming which figure is the stable one. The old note assumed per-second was
        /// stable, which is exactly backwards for the common case.
        ///
        /// Only a RISE is discounted. A fall that is also an artifact costs nobody anything: it cannot manufacture
        /// the "you broke something" heading, which is the failure this exists to prevent.
        /// </summary>
        static bool IsFrameRateArtifact(string key, BenchmarkStats.Comparison own,
            BenchmarkStats.Comparison perFrame, BenchmarkStats.Comparison perSecond)
        {
            if (own.Verdict != DeltaVerdict.Regressed) return false;

            if (key == BenchmarkMetricKeys.GcPerSecondBytes)
                return perFrame.Verdict != DeltaVerdict.Regressed
                    && perFrame.Verdict != DeltaVerdict.Incomparable;

            if (key == BenchmarkMetricKeys.GcPerFrameBytes)
                return perSecond.Verdict != DeltaVerdict.Regressed
                    && perSecond.Verdict != DeltaVerdict.Incomparable;

            return false;
        }

        /// <summary>Says what happened instead of "worse", which is the one word this row has not earned.</summary>
        static string ArtifactVerdictText() =>
            L.Tr("up with the frame rate, not the code", "随帧率上升，非代码变化");

        static string ArtifactNote(string key, BenchmarkStats.Comparison perFrame,
            BenchmarkStats.Comparison perSecond, BenchmarkStats.Comparison frameCmp)
        {
            var partner = key == BenchmarkMetricKeys.GcPerSecondBytes ? perFrame : perSecond;
            string partnerName = key == BenchmarkMetricKeys.GcPerSecondBytes
                ? L.Tr("per frame", "每帧") : L.Tr("per second", "每秒");

            return L.Tr($"Allocation per second is allocation per frame times frames per second. The {partnerName} figure did not rise ({DeltaText(partner)}), and frame time moved {DeltaText(frameCmp)} — so this is the same bytes divided over a different number of frames, not the code allocating more.",
                        $"每秒分配 ＝ 每帧分配 × 每秒帧数。{partnerName}那一项并没有上升（{DeltaText(partner)}），而帧时间变化了 {DeltaText(frameCmp)}——所以这是同样的字节按不同的帧数摊开，不是代码分配变多了。");
        }

        /// <summary>
        /// Per-row caveats. The only one so far is the per-frame allocation trap: at a different frame rate the same
        /// code produces a different bytes-per-frame figure, so the pair of GC rows has to be read together.
        /// </summary>
        static string NoteFor(string key, bool frameRateShifted, BenchmarkStats.Comparison frameCmp,
            BenchmarkStats.Comparison own)
        {
            bool moved = own.Verdict == DeltaVerdict.Improved || own.Verdict == DeltaVerdict.Regressed;

            // Process-wide memory counters read the whole editor, not the game (Stage 0 measured 4.4 GB on a scene
            // that ships far smaller). The corollary only showed up in a null comparison: if the absolute value
            // includes the editor, so does the CHANGE — the editor's own heap grows and is collected on a schedule of
            // its own, and over 24 idle minutes that moved the managed heap by 6.7% with nothing edited.
            // Why a big-looking delta is being refused. Without this the row reads as the tool failing to notice a
            // 74% change; with it, the reader can see the baseline itself was the problem — most often a first
            // repetition that was still loading, which the warmup was supposed to cover and did not.
            if (own.Stability == MetricStability.Unstable)
                return L.Tr("The repetitions of a single measurement disagree about this figure by more than the two measurements disagree with each other, so no before/after can be read from it at all. Usually the first repetition was still loading — a longer warmup, or discarding that measurement and taking it again, is what fixes it.",
                            "这个指标在**同一次测量的各轮之间**的差异，比两次测量之间的差异还大，所以从它读不出任何前后结论。通常是第一轮还在加载——加长 warmup，或者丢掉这次测量重测，才是解法。");

            if (moved && IsObservationOnly(key))
                return L.Tr("Observation only. This includes the Unity Editor's own memory, so package compilation, imports and editor caches can move it independently of the scene. It is shown for context and does not decide the overall result.",
                            "仅供观察。该计数器包含 Unity 编辑器自身内存，包编译、资源导入和编辑器缓存都可能让它脱离场景独立变化。这里保留它作为上下文，但它不参与总判定。");

            if (moved && IsEditorInclusive(key))
            {
                string note = L.Tr("Includes the editor's own memory, which grows and is collected on its own schedule — over a long gap that drift can be larger than the change you are looking for.",
                                   "该计数器含编辑器自身内存，它会按自己的节奏增长与回收——时间跨度一长，这种漂移可能大于你想找的那个变化。");
                // GC per frame is editor-inclusive AND frame-rate sensitive. Since the scope gate started catching it,
                // the generic note would return first and drop the specific one; both are true and both are needed.
                if (frameRateShifted && key == BenchmarkMetricKeys.GcPerFrameBytes)
                    note += L.Tr($" Frame time also moved {DeltaText(frameCmp)}, so part of this is the frame rate rather than the code — read it together with GC alloc / second.",
                                 $" 另外帧时间变化了 {DeltaText(frameCmp)}，因此这里还有一部分来自帧率而非代码——请与「GC 分配/秒」一起看。");
                return note;
            }

            if (!frameRateShifted) return null;

            if (key == BenchmarkMetricKeys.GcPerFrameBytes)
                return L.Tr($"Frame time moved {DeltaText(frameCmp)}, so part of this is the frame rate rather than the code — read it together with GC alloc / second.",
                            $"帧时间变化了 {DeltaText(frameCmp)}，因此这里有一部分来自帧率而非代码——请与「GC 分配/秒」一起看。");

            // This used to claim per-second allocation "does not move with the frame rate", which is backwards for
            // the common case: code that allocates once per Update keeps bytes-per-FRAME flat, so bytes-per-second
            // is the one that rises when the frame speeds up. Neither figure is the stable one in general — which of
            // the pair moves is a property of the project, and the way to tell is to read them together.
            if (key == BenchmarkMetricKeys.GcPerSecondBytes)
                return L.Tr($"Frame time moved {DeltaText(frameCmp)}, so part of this is the frame rate rather than the code — read it together with GC alloc / frame. Whichever of the two stayed put is the one describing your code.",
                            $"帧时间变化了 {DeltaText(frameCmp)}，因此这里有一部分来自帧率而非代码——请与「GC 分配/帧」一起看。两者中没动的那一个，才是在描述你的代码。");

            return null;
        }

        // ── Values & formatting ───────────────────────────────

        /// <summary>Per-run values for a metric, including derived ones that no run stores directly.</summary>
        static List<double> ValuesOf(IReadOnlyList<BenchmarkRun> runs, string key)
        {
            // A different STATISTIC of a stored counter, not a different counter: p95 frame time rather than median.
            if (string.Equals(key, BenchmarkMetricKeys.FrameTimeP95Ms, StringComparison.Ordinal))
                return BenchmarkStats.ValuesOf(runs, BenchmarkMetricKeys.FrameTimeMs, BenchmarkStat.P95);

            if (!string.Equals(key, BenchmarkMetricKeys.GcPerSecondBytes, StringComparison.Ordinal))
                return BenchmarkStats.ValuesOf(runs, key);

            var values = new List<double>();
            if (runs == null) return values;
            foreach (var r in runs)
            {
                double v = r?.Derived(key) ?? double.NaN;
                if (!double.IsNaN(v)) values.Add(v);
            }
            return values;
        }

        /// <summary>
        /// Counters that measure the whole editor process rather than the game. Allocation PER FRAME is deliberately
        /// not one of them: it is a rate produced by the running scene, and it was the memory-side figure that came
        /// out clean in the null comparison while every process-wide total drifted.
        /// </summary>
        /// <summary>
        /// Whether a counter reads the editor process as well as the game — the rows an uncalibrated round cannot
        /// pin a verdict on.
        ///
        /// Asked of <see cref="BenchmarkMetricKeys.Scope"/> rather than a list of keys, because the list drifted from
        /// the scope table and the drift had teeth. It named four counters whose names contain "memory"; meanwhile
        /// <c>GcPerFrameBytes</c> had already been reclassified to <see cref="MetricScope.ContentPlusEditor"/> after
        /// being measured as a process counter — and, not being called memory, never got added here. So a round whose
        /// editor-side GC per frame rose 117% while the GAME-side counter sat unchanged at 385 bytes for every sampled
        /// frame was titled "Something moved the wrong way", above advice to undo the work one item at a time. The
        /// user's changes were two audio load types and 28 Read/Write flags; the allocation was the editor compiling
        /// and running scripts between the two samples.
        ///
        /// One source of truth: a counter that includes the editor says so in the scope table, and everything that
        /// gates on that fact asks the table.
        /// </summary>
        static bool IsEditorInclusive(string key) =>
            BenchmarkMetricKeys.Scope(key) == MetricScope.ContentPlusEditor;

        /// <summary>
        /// The broadest editor-process counter. Unlike the other memory rows, it cannot isolate scene residency even
        /// after a drift band exists because compilation, imports and editor caches vary with the work between runs.
        /// </summary>
        static bool IsObservationOnly(string key) =>
            key == BenchmarkMetricKeys.TotalReservedBytes;

        /// <summary>How far past the calibrated span a gap may reach before the band stops standing in for it.</summary>
        const double DriftGapGraceMinutes = 2.0;
        const double DriftGapExtrapolationFactor = 2.0;

        /// <summary>
        /// Whether the drift band was measured across a span like this round's — not merely whether it exists.
        ///
        /// The editor's own memory wanders with elapsed time: <see cref="MetricScope.ContentPlusEditor"/> records the
        /// managed heap reading 6.0-6.3% lower across three comparisons in which nothing was edited at all. A band
        /// calibrated back-to-back is therefore a statement about two minutes, and "has it been calibrated" was a
        /// bool — so a 0.03% band taken 2.1 minutes apart was being used to judge a comparison spanning 4.8, and
        /// would equally be used at thirty. That is the GardenScene failure by another row: Total reserved is now
        /// observation-only at all times, but Total used memory, Managed heap and Graphics memory can still veto a
        /// clear content win the moment a band of any width exists.
        ///
        /// Drift accumulates roughly with elapsed time, so twice the calibrated span is the outer edge of an honest
        /// extrapolation. The flat grace keeps a 0.4-minute calibration from disqualifying a 1.5-minute round, where
        /// the ratio is alarming and the absolute difference is nothing.
        /// </summary>
        internal static bool DriftCoversGap(double calibratedSpanMinutes, double gapMinutes) =>
            gapMinutes <= calibratedSpanMinutes + DriftGapGraceMinutes ||
            gapMinutes <= calibratedSpanMinutes * DriftGapExtrapolationFactor;

        // A key added anywhere else and forgotten here renders without its unit: GameFrameTimeMs arrived with a
        // label, a scope and an axis, and missing from this list it printed "2 -> 2" beside an editor frame time
        // reading "3.28 -> 3.13 ms" — a row claiming -10.8% between two numbers that look identical.
        // MetricUnitsCoverEveryStoredKey pins the pair of lists to the key names so the next one cannot slip.
        internal static bool IsTime(string key) =>
            key == BenchmarkMetricKeys.FrameTimeMs || key == BenchmarkMetricKeys.FrameTimeP95Ms ||
            key == BenchmarkMetricKeys.GpuFrameTimeMs || key == BenchmarkMetricKeys.GameFrameTimeMs;

        /// <summary>
        /// The pair with the unit stated once — "697.0 -> 654.8 MB" rather than "697.0 MB -> 654.8 MB". Falls back to
        /// spelling both out when the two values land in different units, where dropping one would be a lie.
        /// </summary>
        static string PairText(string key, double before, double after)
        {
            string a = Format(key, before), b = Format(key, after);
            string ua = UnitOf(a), ub = UnitOf(b);
            if (ua.Length > 0 && ua == ub)
                return a.Substring(0, a.Length - ua.Length).TrimEnd() + " -> " + b;
            return a + " -> " + b;
        }

        /// <summary>Trailing non-numeric part of a formatted value ("MB", " ms", "/s"), or empty for a bare count.</summary>
        static string UnitOf(string formatted)
        {
            int i = formatted.Length;
            while (i > 0 && !char.IsDigit(formatted[i - 1])) i--;
            return formatted.Substring(i);
        }

        internal static bool IsBytes(string key) =>
            key == BenchmarkMetricKeys.GcPerFrameBytes || key == BenchmarkMetricKeys.GcPerSecondBytes ||
            key == BenchmarkMetricKeys.TotalMemoryBytes || key == BenchmarkMetricKeys.TotalReservedBytes ||
            key == BenchmarkMetricKeys.GcUsedBytes || key == BenchmarkMetricKeys.GfxUsedBytes ||
            key == BenchmarkMetricKeys.GameGcPerFrameBytes;

        static string Format(string key, double v)
        {
            if (double.IsNaN(v)) return "—";
            if (IsTime(key)) return v.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
            if (IsBytes(key)) return ScannerUtil.Human((long)Math.Round(v)) + (key == BenchmarkMetricKeys.GcPerSecondBytes ? "/s" : "");
            return v.ToString("#,0", CultureInfo.InvariantCulture);
        }

        static Report Blocked(string reason, string remedy = null) =>
            new Report(Array.Empty<MetricRow>(), reason, null,
                L.Tr($"Can't compare these two measurements: {reason}.", $"这两次测量无法对比：{reason}。"),
                null, null, null, null, 0, false, false,
                Outcome.Blocked, null,
                // The way out doubles as the advice: a refusal with no exit is where this feature dead-ends.
                remedy,
                L.Tr("These two measurements can't be compared", "这两次测量无法对比"));
    }
}
