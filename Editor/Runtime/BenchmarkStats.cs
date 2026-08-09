using System;
using System.Collections.Generic;

namespace PerfLint.Runtime
{
    /// <summary>Which statistic of a metric is the headline for that metric.</summary>
    public enum BenchmarkStat
    {
        Avg,
        Median,
        Max,
        P95
    }

    /// <summary>How trustworthy a metric's run-to-run behaviour is on this machine, in this mode.</summary>
    public enum MetricStability
    {
        /// <summary>Fewer than two runs — no spread can be computed at all.</summary>
        NoData,
        /// <summary>CV ≤ 5%: a ~10% change is comfortably detectable. Deltas may be reported plainly.</summary>
        Stable,
        /// <summary>5% &lt; CV ≤ 15%: only large changes are distinguishable. Deltas must be shown with their noise band.</summary>
        Marginal,
        /// <summary>CV &gt; 15%: the measurement is dominated by noise. Do not report deltas for this metric.</summary>
        Unstable
    }

    /// <summary>
    /// Where a comparison's noise band came from. Three different facts about the world, and the widest one wins.
    ///
    /// They are kept apart because they license different sentences. "The baseline's own repetitions disagreed by
    /// this much" is about precision; "this figure drifts on its own" is about the machine; "this figure was moving
    /// while we watched it" is about the measurement not describing a steady state at all — and only the third one
    /// has an action attached, because it is the only one the user can remove.
    /// </summary>
    public enum NoiseSource
    {
        /// <summary>The spread of the repetitions themselves.</summary>
        Repetitions,
        /// <summary>Observed movement with nothing changed, over the span a real before/after covers.</summary>
        Drift,
        /// <summary>The figure did not hold still inside a single sampling window. No number of repetitions fixes this.</summary>
        UnsteadySampling
    }

    /// <summary>What a before/after comparison is allowed to claim.</summary>
    public enum DeltaVerdict
    {
        /// <summary>Fingerprints differ, or one side has no data — no number may be shown.</summary>
        Incomparable,
        /// <summary>Only one run on a side: a delta exists but there is no noise band to judge it against.</summary>
        NoNoiseBand,
        /// <summary>The change is smaller than run-to-run noise. The honest statement is "no measurable change", NOT "no change".</summary>
        WithinNoise,
        Improved,
        Regressed
    }

    /// <summary>
    /// Pure statistics over a set of repeated benchmark runs, and the rules that decide what a before/after
    /// difference is allowed to claim.
    ///
    /// The design premise: an improvement is only real if it is bigger than the spread we get from changing
    /// nothing at all. So a baseline is measured more than once on purpose, the spread of those repetitions
    /// becomes the noise band, and any delta inside that band is reported as "no measurable change". This is
    /// the whole reason the feature is allowed to say "we proved it" — and the reason it will sometimes have
    /// to say "we could not".
    ///
    /// No Unity API use: fully unit-testable.
    /// </summary>
    public static class BenchmarkStats
    {
        /// <summary>Coefficient of variation at or below which a metric is treated as stable.</summary>
        public const double StableCvMax = 0.05;
        /// <summary>Coefficient of variation above which a metric is treated as unusable for deltas.</summary>
        public const double MarginalCvMax = 0.15;
        /// <summary>Half-width of the noise band, in standard deviations of the run-to-run spread (~95%).</summary>
        public const double NoiseSigmas = 2.0;
        /// <summary>Deltas below this are never called an improvement even when the measurement is perfectly deterministic — not worth a claim.</summary>
        public const double MinReportableDeltaPercent = 1.0;

        /// <summary>Run-to-run spread of one statistic of one metric.</summary>
        public readonly struct Spread
        {
            public readonly string Key;
            public readonly int N;
            public readonly double Mean;
            public readonly double StdDev;
            public readonly double Min;
            public readonly double Max;

            /// <summary>Relative spread (StdDev / |Mean|). 0 when the metric is perfectly deterministic; NaN-free by construction.</summary>
            public readonly double Cv;

            public bool HasData => N > 0;

            public Spread(string key, int n, double mean, double stdDev, double min, double max)
            {
                Key = key; N = n; Mean = mean; StdDev = stdDev; Min = min; Max = max;
                Cv = (n >= 2 && Math.Abs(mean) > double.Epsilon) ? stdDev / Math.Abs(mean) : 0.0;
            }

            public MetricStability Stability =>
                N < 2 ? MetricStability.NoData
                : Cv <= StableCvMax ? MetricStability.Stable
                : Cv <= MarginalCvMax ? MetricStability.Marginal
                : MetricStability.Unstable;
        }

        /// <summary>The outcome of comparing a baseline set of runs to an "after" set.</summary>
        public readonly struct Comparison
        {
            public readonly string Key;
            public readonly DeltaVerdict Verdict;
            public readonly double Before;
            public readonly double After;
            /// <summary>Signed change as a percentage of the baseline. Negative = the number went down (which, for every metric we track, is better).</summary>
            public readonly double DeltaPercent;
            /// <summary>Half-width of the run-to-run noise band, as a percentage of the baseline. A delta inside this is not distinguishable from doing nothing.</summary>
            public readonly double NoiseBandPercent;
            public readonly MetricStability Stability;
            /// <summary>Populated when <see cref="Verdict"/> is <see cref="DeltaVerdict.Incomparable"/>.</summary>
            public readonly string IncomparableReason;
            /// <summary>
            /// Which of the three sources <see cref="NoiseBandPercent"/> came from. Not cosmetic: it changes what the
            /// band is evidence OF, and therefore how the verdict may be worded.
            /// </summary>
            public readonly NoiseSource BandFrom;

            /// <summary>True when the band came from observed drift. Kept as the name the report and its tests already use.</summary>
            public bool BandFromDrift => BandFrom == NoiseSource.Drift;

            /// <summary>True when the figure did not hold still inside a single sampling window, which no repetition count can fix.</summary>
            public bool BandFromUnsteadySampling => BandFrom == NoiseSource.UnsteadySampling;

            public Comparison(string key, DeltaVerdict verdict, double before, double after,
                double deltaPercent, double noiseBandPercent, MetricStability stability, string incomparableReason,
                NoiseSource bandFrom = NoiseSource.Repetitions)
            {
                Key = key; Verdict = verdict; Before = before; After = after;
                DeltaPercent = deltaPercent; NoiseBandPercent = noiseBandPercent;
                Stability = stability; IncomparableReason = incomparableReason;
                BandFrom = bandFrom;
            }

            /// <summary>True only when the change is real AND in the good direction. This is the flag a "goal met" tick is allowed to read.</summary>
            public bool IsRealImprovement => Verdict == DeltaVerdict.Improved;
        }

        /// <summary>Every metric currently tracked is lower-is-better. Stated explicitly so that adding e.g. an FPS metric forces this to be revisited rather than silently inverting a verdict.</summary>
        public static bool LowerIsBetter(string key) => true;

        /// <summary>
        /// The statistic that represents a metric. Frame time uses the median (one hitch must not decide the
        /// verdict); memory uses the peak (that is what runs a device out of RAM); everything else uses the mean.
        /// </summary>
        public static BenchmarkStat Headline(string key) => key switch
        {
            BenchmarkMetricKeys.FrameTimeMs => BenchmarkStat.Median,
            BenchmarkMetricKeys.GpuFrameTimeMs => BenchmarkStat.Median,
            BenchmarkMetricKeys.TotalMemoryBytes => BenchmarkStat.Max,
            BenchmarkMetricKeys.TotalReservedBytes => BenchmarkStat.Max,
            BenchmarkMetricKeys.GcUsedBytes => BenchmarkStat.Max,
            BenchmarkMetricKeys.GfxUsedBytes => BenchmarkStat.Max,
            _ => BenchmarkStat.Avg
        };

        public static double Value(BenchmarkMetric m, BenchmarkStat stat)
        {
            if (m == null || !m.hasData) return double.NaN;
            return stat switch
            {
                BenchmarkStat.Median => m.median,
                BenchmarkStat.Max => m.max,
                BenchmarkStat.P95 => m.p95,
                _ => m.avg
            };
        }

        /// <summary>
        /// One value per run: the metric's headline statistic, with runs that lack it dropped.
        ///
        /// Exposed separately from <see cref="SpreadOf"/> so that a DERIVED figure — one computed from two stored
        /// counters rather than sampled directly, such as GC bytes per second — can be fed through exactly the same
        /// noise-band and verdict rules instead of growing its own, laxer, copy of them.
        /// </summary>
        public static List<double> ValuesOf(IReadOnlyList<BenchmarkRun> runs, string key, BenchmarkStat? stat = null)
        {
            var s = stat ?? Headline(key);
            var values = new List<double>();
            if (runs == null) return values;
            foreach (var r in runs)
            {
                double v = Value(r?.Get(key), s);
                if (!double.IsNaN(v)) values.Add(v);
            }
            return values;
        }

        /// <summary>
        /// How much a figure moved WITHIN a single sampling window, as a percentage — the widest such move across
        /// the repetitions. Zero when nothing recorded it.
        ///
        /// This is a different quantity from the run-to-run spread and no number of repetitions substitutes for it.
        /// Repetitions answer "does this figure land in the same place each time"; this answers "was it standing
        /// still while we watched". A camera being moved through the window fails the second while passing the
        /// first, because every repetition tours the same route and averages to the same place.
        ///
        /// Measured, which is why it exists. On a museum project, three baseline repetitions with the camera being
        /// moved: run-to-run CV of vertices was 8.15% — graded Marginal, band ±16.3% — while inside each window the
        /// first half averaged ~445k against ~250k for the second, a swing of 39–54%. A later pair taken with the
        /// camera parked agreed to 0.002%. The comparison between them reported "+30.8%, regressed": a verdict
        /// entirely produced by where the user happened to be looking.
        ///
        /// Deliberately reads the halves rather than the min/max: a single dropped frame reads 0 and a single
        /// double-counted frame reads 2×, and neither says the scene was in a different state. Half against half is
        /// 1000 frames against 1000 frames.
        /// </summary>
        public static double WithinRunSwingPercent(IReadOnlyList<BenchmarkRun> runs, string key)
        {
            if (runs == null) return 0;
            double worst = 0;
            foreach (var r in runs)
            {
                var m = r?.Get(key);
                // Both halves must be present AND the metric must have data: a run written before the halves were
                // recorded reports zeros, and reading that as "perfectly steady" would silence the check exactly
                // where it has no evidence.
                if (m == null || !m.hasData) continue;
                double scale = Math.Abs(m.avg);
                if (scale <= double.Epsilon) continue;
                if (m.firstHalfAvg == 0 && m.secondHalfAvg == 0) continue;

                double swing = Math.Abs(m.firstHalfAvg - m.secondHalfAvg) / scale * 100.0;
                if (swing > worst) worst = swing;
            }
            return worst;
        }

        /// <summary>Run-to-run spread of a metric's headline statistic across a set of repetitions. Runs missing the metric are skipped.</summary>
        public static Spread SpreadOf(IReadOnlyList<BenchmarkRun> runs, string key, BenchmarkStat? stat = null) =>
            SpreadOfValues(key, ValuesOf(runs, key, stat));

        /// <summary>Run-to-run spread of an already-extracted set of per-run values.</summary>
        public static Spread SpreadOfValues(string key, IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0) return new Spread(key, 0, 0, 0, 0, 0);

            double sum = 0, min = double.MaxValue, max = double.MinValue;
            foreach (var v in values)
            {
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double mean = sum / values.Count;

            // Sample standard deviation (n-1). With n=2 this is the honest, wide estimate; using the population
            // form here would understate the band exactly when we have the least evidence.
            double sd = 0;
            if (values.Count >= 2)
            {
                double sq = 0;
                foreach (var v in values) sq += (v - mean) * (v - mean);
                sd = Math.Sqrt(sq / (values.Count - 1));
            }

            return new Spread(key, values.Count, mean, sd, min, max);
        }

        /// <summary>
        /// Compares a baseline to an "after" measurement for one metric and decides what may be claimed.
        ///
        /// The noise band is taken from whichever side is noisier (conservative: we would rather stay silent about
        /// a real win than announce a fake one), and a delta must clear both that band and a minimum reportable
        /// size before it is called an improvement.
        /// </summary>
        public static Comparison Compare(BenchmarkSession before, BenchmarkSession after, string key, BenchmarkStat? stat = null)
        {
            if (before == null || after == null || !before.HasRuns || !after.HasRuns)
                return Incomparable(key, "one side has no runs");

            if (!BenchmarkFingerprint.Comparable(before.Fingerprint, after.Fingerprint, out string reason))
                return Incomparable(key, reason);

            var s = stat ?? Headline(key);
            return CompareValues(key, ValuesOf(before.Runs, key, s), ValuesOf(after.Runs, key, s),
                observedDriftPercent: 0,
                unsteadySamplingPercent: Math.Max(WithinRunSwingPercent(before.Runs, key),
                                                  WithinRunSwingPercent(after.Runs, key)));
        }

        /// <summary>
        /// The verdict rules themselves, over per-run values that have already been extracted (and whose
        /// comparability has already been established by the caller).
        ///
        /// Split out from <see cref="Compare"/> so a derived figure computed from two counters goes through the
        /// same "is this bigger than doing nothing?" test as a directly sampled one.
        /// </summary>
        /// <param name="observedDriftPercent">
        /// How far this figure has been seen to move with nothing changed, as a percentage. Raises the band when it
        /// is wider than the repetition spread — which, for anything that reads the machine's momentary state or the
        /// editor's own process, it usually is. Zero means no drift has been observed yet.
        /// </param>
        /// <param name="unsteadySamplingPercent">
        /// How far this figure moved inside a single sampling window — see <see cref="WithinRunSwingPercent"/>.
        /// Raises the band like drift does, and for the same reason: a difference smaller than the figure's own
        /// movement while nothing was being changed cannot be attributed to anything.
        /// </param>
        public static Comparison CompareValues(string key, IReadOnlyList<double> before, IReadOnlyList<double> after,
            double observedDriftPercent = 0, double unsteadySamplingPercent = 0)
        {
            var b = SpreadOfValues(key, before);
            var a = SpreadOfValues(key, after);

            if (!b.HasData || !a.HasData) return Incomparable(key, "metric not available on both sides");
            if (Math.Abs(b.Mean) <= double.Epsilon) return Incomparable(key, "baseline is zero");

            double deltaPercent = (a.Mean - b.Mean) / Math.Abs(b.Mean) * 100.0;

            // No repetitions on either side → a difference exists but nothing says whether it is signal.
            if (b.N < 2 && a.N < 2)
                return new Comparison(key, DeltaVerdict.NoNoiseBand, b.Mean, a.Mean, deltaPercent, 0,
                    MetricStability.NoData, null);

            double cv = Math.Max(b.Cv, a.Cv);
            var stability = new Spread(key, Math.Max(b.N, a.N), b.Mean, cv * Math.Abs(b.Mean), b.Min, b.Max).Stability;
            double repetitionBand = NoiseSigmas * cv * 100.0;

            // The widest of the THREE, because they measure different things and all have to be cleared:
            //
            //   repetitions  short-term precision — does it land in the same place each time?
            //   drift        how far it wanders over the span a real before/after covers. A null comparison over
            //                24 minutes moved the managed heap 6.7% while its repetition spread was a fraction of a
            //                percent — judged by repetitions alone, that reads as a win.
            //   unsteady     how far it moved WHILE BEING WATCHED. Repetitions cannot catch this: every repetition
            //                that tours the same route averages to the same place, so the spread stays small while
            //                no single window describes a steady state. Measured at 39-54% on vertices with a
            //                camera being moved, against a repetition spread of 8%.
            double drift = observedDriftPercent > 0 ? observedDriftPercent : 0;
            double unsteady = unsteadySamplingPercent > 0 ? unsteadySamplingPercent : 0;
            double bandPercent = Math.Max(repetitionBand, Math.Max(drift, unsteady));
            // Attributed only when that source is what actually binds. A band narrower than the reportable floor
            // suppresses nothing — the floor does — so naming drift or unsteady sampling there would put a cause on
            // screen that did not decide anything. Caught by a metric whose window moved 0.5%: true by the
            // widest-wins arithmetic, and a sentence about an unsteady measurement that was in fact rock steady.
            double binding = Math.Max(repetitionBand, MinReportableDeltaPercent);
            var bandFrom = bandPercent <= binding ? NoiseSource.Repetitions
                         : unsteady >= drift ? NoiseSource.UnsteadySampling
                         : NoiseSource.Drift;
            double threshold = Math.Max(bandPercent, MinReportableDeltaPercent);

            // Decided on the SAME figures the report prints, to one decimal. Deciding on raw values while printing
            // rounded ones produced a sentence that refutes itself: "+1.1%, beyond the ±1.1% run-to-run noise",
            // from a delta of 1.14% against a band of 1.06%. A verdict the displayed numbers do not support is worse
            // than no verdict, because the reader can see it is wrong and has no way to tell which part is.
            double shownDelta = Math.Round(Math.Abs(deltaPercent), 1);
            double shownThreshold = Math.Round(threshold, 1);

            DeltaVerdict verdict;
            if (stability == MetricStability.Unstable)
                verdict = DeltaVerdict.WithinNoise; // too noisy to distinguish anything — never claim a direction
            else if (shownDelta <= shownThreshold)
                verdict = DeltaVerdict.WithinNoise;
            else
            {
                bool wentDown = deltaPercent < 0;
                bool better = LowerIsBetter(key) ? wentDown : !wentDown;
                verdict = better ? DeltaVerdict.Improved : DeltaVerdict.Regressed;
            }

            return new Comparison(key, verdict, b.Mean, a.Mean, deltaPercent, bandPercent, stability, null, bandFrom);
        }

        static Comparison Incomparable(string key, string reason) =>
            new Comparison(key, DeltaVerdict.Incomparable, double.NaN, double.NaN, 0, 0, MetricStability.NoData, reason);
    }
}
