using System;
using System.Collections.Generic;
using PerfLint.L10n;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Before/after at the level of individual call paths, rather than at the level of the whole frame.
    ///
    /// Why this exists at all: whole-frame time is a property of the machine, and this project has measured it moving
    /// ±13% between two measurements of a project nobody touched. Eight rounds went into making statistics smart enough
    /// for a quantity that cannot carry a conclusion. The way out is not a better estimator, it is a different subject
    /// — "EnemySpawner.Update cost 7.8 ms of each frame and was running in 92% of them, and now costs 3.0 ms and runs
    /// in 28%" is a claim about the user's code. It is what they changed, so it is what should be measured.
    ///
    /// Two figures per hotspot, and they are NOT the same kind of claim:
    /// - **Self time per frame** — milliseconds on this CPU. Compared through the same
    ///   <see cref="BenchmarkStats.CompareValues"/> rules and the same <see cref="BenchmarkDrift"/> calibration as every
    ///   counter, so its noise band is measured on the user's machine rather than assumed to be small here.
    /// - **Sampled-frame hit rate** — the share of representative frames the marker did main-thread work in. This one is
    ///   a property of the code path, not of the machine: the frames come from the same scene, and "does this run every
    ///   frame" has the same answer on a phone. It is judged by non-overlapping confidence intervals, which is stricter
    ///   than a proper two-proportion test and errs toward saying nothing.
    ///
    /// The trap this type is most careful about: a hotspot list is TRUNCATED to the most expensive markers. A marker
    /// missing from the "after" side has not been proven to cost zero — it has dropped out of a top-N list, and those
    /// are different statements. Every absence is reported as the second one.
    /// </summary>
    public static class HotspotComparison
    {
        /// <summary>How many rows a comparison will produce at most, matching the hotspot list's own truncation.</summary>
        public const int MaxRows = 12;

        /// <summary>Which sides listed a marker. Absence is about the LIST, never about the cost — see the type remarks.</summary>
        public enum Presence
        {
            /// <summary>Listed by every merged run on both sides — the only case where self time may be compared.</summary>
            Both,
            /// <summary>Listed before, no longer among the markers listed after.</summary>
            DroppedOut,
            /// <summary>Not listed before, among the markers listed after.</summary>
            Appeared
        }

        /// <summary>What the sampled-frame hit rate did. Judged from confidence intervals, so a small denominator answers <see cref="Unclear"/> rather than overclaiming.</summary>
        public enum HitChange
        {
            /// <summary>One or both sides didn't record a hit rate (a session from before the metric existed).</summary>
            NotRecorded,
            /// <summary>The intervals overlap: the frames we sampled cannot tell the two rates apart.</summary>
            Unclear,
            /// <summary>The code path runs in measurably fewer frames than before.</summary>
            Fell,
            /// <summary>The code path runs in measurably more frames than before.</summary>
            Rose
        }

        public sealed class Row
        {
            public string Marker { get; }
            /// <summary>Project script this marker maps to, or null. The anchor for "open the code that costs this".</summary>
            public string ScriptPath { get; }
            public Presence Presence { get; }

            /// <summary>Self time per frame, with the verdict and noise band. <see cref="DeltaVerdict.Incomparable"/> unless <see cref="Presence"/> is <see cref="Presence.Both"/>.</summary>
            public BenchmarkStats.Comparison SelfMs { get; }

            /// <summary>Hit counts pooled across the merged runs of each side. Zero denominators mean the side recorded no hit rate.</summary>
            public int BeforeHitFrames { get; }
            public int BeforeSampledFrames { get; }
            public int AfterHitFrames { get; }
            public int AfterSampledFrames { get; }
            public HitChange Hit { get; }

            /// <summary>Self time on the side that has it, for ordering and for describing a row that only one side listed.</summary>
            public double BeforeSelfMs { get; }
            public double AfterSelfMs { get; }

            /// <summary>
            /// Deep Profile was on. The direction of a self-time change is still real, but its SIZE is not: the
            /// profiler charges per call, so removing calls removes bookkeeping along with work and the millisecond
            /// figure falls further than the game did. The hit rate is untouched — presence in a frame is not
            /// something instrumentation can change — which is why it becomes the headline in this mode.
            /// </summary>
            public bool TimingsInflated { get; }

            public Row(string marker, string scriptPath, Presence presence, BenchmarkStats.Comparison selfMs,
                int beforeHits, int beforeFrames, int afterHits, int afterFrames, HitChange hit,
                double beforeSelfMs, double afterSelfMs, bool timingsInflated = false)
            {
                Marker = marker; ScriptPath = scriptPath; Presence = presence; SelfMs = selfMs;
                BeforeHitFrames = beforeHits; BeforeSampledFrames = beforeFrames;
                AfterHitFrames = afterHits; AfterSampledFrames = afterFrames; Hit = hit;
                BeforeSelfMs = beforeSelfMs; AfterSelfMs = afterSelfMs;
                TimingsInflated = timingsInflated;
            }

            public string Key => BenchmarkMetricKeys.HotspotKey(Marker);
            public bool IsScript => !string.IsNullOrEmpty(ScriptPath);

            /// <summary>
            /// Whether this call path is code the reader can open and change — the gate for saying anything about it
            /// on the verify screen, and for letting it decide an outcome.
            ///
            /// <see cref="IsScript"/> only says a .cs file was found, and a marker inside a package is as unopenable
            /// as one with no file at all. What the reader actually got, on a round that hid 1.19M triangles by hand:
            /// "Inl_On Record Render Graph · busiest call path · 100% -> 100% (92/92 -> 62/62) · 0.24 -> 0.22 ms",
            /// directly under the headline, above the figures that did explain the win. It is engine code, it cannot
            /// be opened, and the two engine rows shown together accounted for 0.07 ms of a 1.4 ms improvement. In
            /// that measurement not one of the twelve ranked markers was under Assets/ — the only one with a script
            /// path at all pointed into com.unity.visualeffectgraph.
            ///
            /// A user's own method getting cheaper stays the strongest thing this loop can say, and still shows.
            /// </summary>
            public bool IsUserCode => IsUnderAssets(ScriptPath);

            /// <summary>Asset-path test, tolerant of the backslashes some call paths arrive with.</summary>
            internal static bool IsUnderAssets(string scriptPath)
            {
                if (string.IsNullOrEmpty(scriptPath)) return false;
                string p = scriptPath.Replace('\\', '/').TrimStart('/');
                return p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            }

            public bool Improved => SelfMs.Verdict == DeltaVerdict.Improved;
            public bool Regressed => SelfMs.Verdict == DeltaVerdict.Regressed;
            public bool Moved => Improved || Regressed;

            public bool HasHitRates => BeforeSampledFrames > 0 && AfterSampledFrames > 0;
            public double BeforeHitPercent => BeforeSampledFrames > 0 ? 100.0 * BeforeHitFrames / BeforeSampledFrames : 0;
            public double AfterHitPercent => AfterSampledFrames > 0 ? 100.0 * AfterHitFrames / AfterSampledFrames : 0;

            /// <summary>"3.10 -> 1.20 ms" for a matched row; the single side's figure otherwise.</summary>
            public string PairText => Presence == Presence.Both
                ? $"{SelfMs.Before:0.00} -> {SelfMs.After:0.00} ms"
                : Presence == Presence.DroppedOut ? $"{BeforeSelfMs:0.00} ms -> —" : $"— -> {AfterSelfMs:0.00} ms";

            /// <summary>"92% -> 28%" with the counts, or null when a side recorded no hit rate.</summary>
            public string HitText => HasHitRates
                ? $"{BeforeHitPercent:0}% -> {AfterHitPercent:0}% ({BeforeHitFrames}/{BeforeSampledFrames} -> {AfterHitFrames}/{AfterSampledFrames})"
                : null;

            public string DeltaText
            {
                get
                {
                    if (Presence != Presence.Both || double.IsNaN(SelfMs.DeltaPercent)) return "";
                    string sign = SelfMs.DeltaPercent < 0 ? "−" : "+";
                    return $"{sign}{Math.Abs(SelfMs.DeltaPercent):0.0}%";
                }
            }

            /// <summary>
            /// The row in one sentence, with the claim kept to what the numbers support.
            ///
            /// The two halves are worded differently on purpose. Milliseconds are this machine's; the hit rate is the
            /// code's, and only that half is allowed to be spoken about as if it would hold anywhere else.
            /// </summary>
            public string Sentence()
            {
                switch (Presence)
                {
                    case Presence.DroppedOut:
                        return L.Tr($"{Marker} is no longer among the most expensive markers (it cost {BeforeSelfMs:0.00} ms/frame before). That is a drop out of a top-{MaxRows} list, not a measurement of zero — it may still be running, more cheaply.",
                                    $"{Marker} 已不在耗时最高的 marker 之列（此前 {BeforeSelfMs:0.00} ms/帧）。这是掉出前 {MaxRows} 名，不是测到了零——它可能仍在跑，只是更便宜了。");

                    case Presence.Appeared:
                        return L.Tr($"{Marker} is now among the most expensive markers at {AfterSelfMs:0.00} ms/frame; it wasn't listed before. It may be new work, or work that was always there and has only now risen into the top {MaxRows}.",
                                    $"{Marker} 现在进入了耗时最高的 marker 之列（{AfterSelfMs:0.00} ms/帧），此前不在榜上。可能是新增的开销，也可能一直存在、只是现在才升进前 {MaxRows}。");
                }

                string ms = SelfMs.Verdict switch
                {
                    DeltaVerdict.Improved => L.Tr($"{Marker} costs {SelfMs.Before - SelfMs.After:0.00} ms less per frame ({PairText}), beyond the ±{SelfMs.NoiseBandPercent:0.0}% this figure moves on its own.",
                                                  $"{Marker} 每帧少花了 {SelfMs.Before - SelfMs.After:0.00} ms（{PairText}），超出该指标自身 ±{SelfMs.NoiseBandPercent:0.0}% 的波动。"),
                    DeltaVerdict.Regressed => L.Tr($"{Marker} costs {SelfMs.After - SelfMs.Before:0.00} ms MORE per frame ({PairText}), beyond the ±{SelfMs.NoiseBandPercent:0.0}% this figure moves on its own.",
                                                   $"{Marker} 每帧多花了 {SelfMs.After - SelfMs.Before:0.00} ms（{PairText}），超出该指标自身 ±{SelfMs.NoiseBandPercent:0.0}% 的波动。"),
                    DeltaVerdict.NoNoiseBand => L.Tr($"{Marker}: {PairText}, but with a single run on each side there is no spread to judge that against.",
                                                     $"{Marker}：{PairText}，但两侧各只有一轮，没有波动范围可作判据。"),
                    _ => L.Tr($"No measurable change in what {Marker} costs per frame ({PairText}).",
                              $"{Marker} 的每帧开销没有可测出的变化（{PairText}）。")
                };

                // Under Deep Profile the millisecond claim has to be walked back to a direction before the hit-rate
                // clause — which is worth its full weight — is allowed to follow it.
                if (TimingsInflated && (SelfMs.Verdict == DeltaVerdict.Improved || SelfMs.Verdict == DeltaVerdict.Regressed))
                    ms = L.Tr($"{Marker} moved {DeltaText} in self time ({PairText}) — with Deep Profile on, the direction is real but the size is not: the profiler charges per call, so changing how often something runs moves this figure further than the game moved.",
                              $"{Marker} 的自耗时变化了 {DeltaText}（{PairText}）——开着 Deep Profile 时，**方向**是真的、**幅度**不是：Profiler 按调用次数计费，所以改变调用频率会让这个数字比游戏本身多降一截。");

                string hit = Hit switch
                {
                    HitChange.Fell => L.Tr($" It also runs in fewer frames now: {HitText}. That part is about your code rather than this machine, so it is the half of this result that carries to a device.",
                                           $" 它出现在更少的帧里了：{HitText}。这一半说的是你的代码而非这台机器，所以它才是可以带到设备上的那一半。"),
                    HitChange.Rose => L.Tr($" It now runs in MORE frames than before: {HitText}.",
                                           $" 它现在出现在比之前更多的帧里：{HitText}。"),
                    HitChange.Unclear when HasHitRates =>
                        L.Tr($" It still runs in about as many frames ({HitText}) — the frames sampled can't tell the two rates apart.",
                             $" 它出现在的帧数大致不变（{HitText}）——所采样的帧还分不出这两个比例。"),
                    _ => ""
                };

                return ms + hit;
            }
        }

        /// <summary>The rows plus, when there are none, the reason — which must never be silently read as "nothing moved".</summary>
        public sealed class Result
        {
            public IReadOnlyList<Row> Rows { get; }
            /// <summary>Why there is no hotspot comparison, or null when there is one.</summary>
            public string Blocker { get; }

            public Result(IReadOnlyList<Row> rows, string blocker)
            {
                Rows = rows ?? Array.Empty<Row>();
                Blocker = blocker;
            }

            public bool HasRows => Rows.Count > 0;

            /// <summary>
            /// The rows that may be shown on the verify screen and may decide an outcome: call paths under Assets/.
            ///
            /// Separate from <see cref="Rows"/> rather than filtered at build time because the main panel's fold is
            /// explicitly "every figure" — engine markers belong there, and they stay in <see cref="Deltas"/>, which
            /// feeds the drift record (how far the machine wanders is a fact about the machine, engine paths
            /// included).
            ///
            /// Display and judgement read the SAME list on purpose. Removing an unreadable row from the screen while
            /// leaving it able to set the verdict is the "evidence off-screen" failure this project keeps closing;
            /// one property is what stops the two from drifting apart again.
            /// </summary>
            public IReadOnlyList<Row> Actionable
            {
                get
                {
                    if (_actionable != null) return _actionable;
                    var list = new List<Row>();
                    foreach (var r in Rows) if (r.IsUserCode) list.Add(r);
                    _actionable = list;
                    return _actionable;
                }
            }
            List<Row> _actionable;

            public bool HasActionableRows => Actionable.Count > 0;

            /// <summary>
            /// The hotspot a conclusion should lead with: cost, weighted by whether it changed.
            ///
            /// Two failure modes, one on each side, and both were reached by trying the extremes.
            ///
            /// Ranking by cost among markers BOTH sides listed — the original rule — excluded the most informative
            /// fact there was. Measured live: a user deleted 500 log calls from an Update, "LogStringToConsole" went
            /// from 15.55 ms per frame to off the list entirely, and the panel led with "Buffer.Memcpy() 0.41 -> 0.30
            /// ms" because that was the largest marker still present on both sides. The rule that was meant to keep
            /// the report honest about drop-outs (absent is not proven to be zero) had quietly become a rule about
            /// which one leads. <see cref="Row.Sentence"/> already words a drop-out correctly; it can lead.
            ///
            /// Ranking by change alone is the opposite error, and a test written for the first fix caught it: a
            /// 0.4 ms marker halving would outrank a 9 ms one sitting still, telling the reader the small thing is
            /// the story. So change is a multiplier on cost rather than a filter — the same shape
            /// <see cref="BenchmarkRunner"/> uses to rank spike frames by magnitude × attribution.
            /// </summary>
            public Row Lead
            {
                get
                {
                    Row best = null;
                    foreach (var r in Actionable)
                        if (best == null || LeadScore(r) > LeadScore(best)) best = r;
                    return best;
                }
            }

            /// <summary>
            /// Whether this call path was MEASURED to change — its self time cleared its own noise band, or its hit
            /// rate cleared its confidence interval.
            ///
            /// Presence used to count, and it is the one of the three that is not a measurement: a marker absent from
            /// the "after" side has dropped out of a top-N list, which the type's remarks are careful to say is not
            /// the same as having been shown to cost zero. Multiplying a lead score by it promoted an absence over a
            /// reading — Inl_RenderPipeline.BeginCameraRendering, 0.077 ms and merely off the list, led the screen as
            /// "busiest call path" over Inl_On Record Render Graph at 0.214 ms, because 0.077 x 4 beats 0.214 x 1.
            ///
            /// A marker that really was expensive and vanished still leads, on its own size: 9 ms x 1 outranks
            /// everything smaller without needing the multiplier's help.
            /// </summary>
            static bool Changed(Row r) =>
                r.Moved || r.Hit == HitChange.Fell || r.Hit == HitChange.Rose;

            /// <summary>
            /// Cost × 4 when something about the row changed. Four is enough that a marker which moved beats a
            /// comparable one that did not, and small enough that a trivial one cannot displace a story four times
            /// its size.
            /// </summary>
            static double LeadScore(Row r) => Math.Max(r.BeforeSelfMs, r.AfterSelfMs) * (Changed(r) ? 4.0 : 1.0);

            public int MovedCount
            {
                get { int n = 0; foreach (var r in Rows) if (r.Moved) n++; return n; }
            }

            /// <summary>Self-time deltas keyed for the drift store, so a null comparison teaches how far each hotspot wanders on its own.</summary>
            public List<KeyValuePair<string, double>> Deltas()
            {
                var list = new List<KeyValuePair<string, double>>();
                foreach (var r in Rows)
                    if (r.Presence == Presence.Both && !double.IsNaN(r.SelfMs.DeltaPercent))
                        list.Add(new KeyValuePair<string, double>(r.Key, r.SelfMs.DeltaPercent));
                return list;
            }
        }

        /// <summary>
        /// Compares the hotspots of two measurements. Comparability of the two sessions is the caller's business —
        /// this runs after <see cref="BenchmarkFingerprint.Comparable"/> has already said yes.
        /// </summary>
        /// <param name="timingsInflated">
        /// Deep Profile was on for both measurements. Self-time deltas keep their direction and lose their magnitude;
        /// hit rates are unaffected. See <see cref="Row.TimingsInflated"/>.
        /// </param>
        public static Result Build(BenchmarkSession before, BenchmarkSession after, BenchmarkDrift.Band drift = null,
            bool timingsInflated = false)
        {
            var b = MergedRuns(before);
            var a = MergedRuns(after);

            // "We did not look" and "there was nothing to see" must not produce the same screen. A run written before
            // hotspots were persisted, or one whose merge timed out, lands here.
            if (b.Count == 0 || a.Count == 0)
                return new Result(null, L.Tr(
                    "one of these measurements has no merged hotspot data, so there is nothing to compare call path by call path (re-measure to collect it)",
                    "其中一次测量没有归并出热点数据，因此无法逐调用路径对比（重新测量一次即可采集）"));

            // Every marker either side listed, with the two sides' per-run values gathered separately.
            var markers = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Collect(List<BenchmarkRun> runs)
            {
                foreach (var r in runs)
                {
                    if (r.hotspots == null) continue;
                    foreach (var h in r.hotspots)
                    {
                        if (h == null || string.IsNullOrEmpty(h.marker)) continue;
                        // Applied to BOTH sides here, not only at capture, so a session recorded before a marker
                        // joined the list is filtered the same way as one recorded after it. Otherwise widening the
                        // filter invents a result: the marker sits in the pinned baseline, is absent from the new
                        // measurement, and compares as "no longer among the most expensive markers" — presented as
                        // something the user's change did.
                        if (RuntimeSampler.IsFrameworkNoise(h.marker)) continue;
                        if (seen.Add(h.marker)) markers.Add(h.marker);
                    }
                }
            }
            Collect(b);
            Collect(a);

            var rows = new List<Row>();
            foreach (var marker in markers)
            {
                var bv = SelfMsPerRun(b, marker);
                var av = SelfMsPerRun(a, marker);

                // Present in only SOME of a side's runs: the value would be the mean of the runs that happened to list
                // it, which is biased upward by exactly the runs where it was cheap enough to fall off the list. Treated
                // as "that side did not consistently list it" rather than averaged anyway.
                bool onBefore = bv.Count == b.Count && bv.Count > 0;
                bool onAfter = av.Count == a.Count && av.Count > 0;
                if (!onBefore && !onAfter) continue;

                PoolHits(b, marker, out int bh, out int bf);
                PoolHits(a, marker, out int ah, out int af);

                string scriptPath = ScriptPathOf(a, marker) ?? ScriptPathOf(b, marker);

                if (onBefore && onAfter)
                {
                    string key = BenchmarkMetricKeys.HotspotKey(marker);
                    var cmp = VisibleInMilliseconds(
                        BenchmarkStats.CompareValues(key, bv, av, drift?.Percent(key) ?? 0), key);
                    rows.Add(new Row(marker, scriptPath, Presence.Both, cmp,
                        bh, bf, ah, af, HitChangeOf(bh, bf, ah, af),
                        Mean(bv), Mean(av), timingsInflated));
                }
                else if (onBefore)
                {
                    rows.Add(new Row(marker, scriptPath, Presence.DroppedOut, NotCompared(marker),
                        bh, bf, 0, 0, HitChange.NotRecorded, Mean(bv), 0, timingsInflated));
                }
                else
                {
                    rows.Add(new Row(marker, scriptPath, Presence.Appeared, NotCompared(marker),
                        0, 0, ah, af, HitChange.NotRecorded, 0, Mean(av), timingsInflated));
                }
            }

            // Most expensive first, using whichever side has a figure — the reader is looking for what eats the frame.
            rows.Sort((x, y) => Math.Max(y.BeforeSelfMs, y.AfterSelfMs).CompareTo(Math.Max(x.BeforeSelfMs, x.AfterSelfMs)));
            if (rows.Count > MaxRows) rows.RemoveRange(MaxRows, rows.Count - MaxRows);

            return rows.Count > 0
                ? new Result(rows, null)
                : new Result(null, L.Tr("no marker was listed consistently enough on either side to compare",
                                        "两侧都没有任何 marker 稳定上榜，无法对比"));
        }

        /// <summary>
        /// Whether the hit rate measurably changed, from non-overlapping 95% confidence intervals.
        ///
        /// Deliberately stricter than a two-proportion test: non-overlapping intervals is a conservative proxy that
        /// stays silent in the borderline cases. That is the correct direction of error here — the cost of missing a
        /// real change is a win going unclaimed, and the cost of announcing a false one is the whole feature.
        /// </summary>
        public static HitChange HitChangeOf(int beforeHits, int beforeFrames, int afterHits, int afterFrames)
        {
            if (beforeFrames <= 0 || afterFrames <= 0) return HitChange.NotRecorded;

            double bLo = Hotspot.WilsonBound(beforeHits, beforeFrames, -1);
            double bHi = Hotspot.WilsonBound(beforeHits, beforeFrames, +1);
            double aLo = Hotspot.WilsonBound(afterHits, afterFrames, -1);
            double aHi = Hotspot.WilsonBound(afterHits, afterFrames, +1);

            if (aHi < bLo) return HitChange.Fell;
            if (aLo > bHi) return HitChange.Rose;
            return HitChange.Unclear;
        }

        // ── Extraction ────────────────────────────────────────

        /// <summary>Runs whose hotspot merge actually ran. The others carry no information about hotspots, in either direction.</summary>
        static List<BenchmarkRun> MergedRuns(BenchmarkSession s)
        {
            var list = new List<BenchmarkRun>();
            if (s?.Runs == null) return list;
            foreach (var r in s.Runs)
                if (r != null && r.hotspotsMerged) list.Add(r);
            return list;
        }

        /// <summary>One self-time value per run that listed the marker. Shorter than the run count means it wasn't listed everywhere.</summary>
        static List<double> SelfMsPerRun(List<BenchmarkRun> runs, string marker)
        {
            var values = new List<double>(runs.Count);
            foreach (var r in runs)
            {
                var h = r.Hotspot(marker);
                if (h != null) values.Add(h.selfMsPerFrame);
            }
            return values;
        }

        /// <summary>
        /// Hit frames and examined frames summed across a side's runs.
        ///
        /// Pooling rather than averaging the rates: every representative frame is one observation of "was this marker
        /// running", and three runs of the same scene give three times as many of them. The denominator grows, so the
        /// confidence interval tightens — which is the whole reason repetitions are worth taking.
        /// </summary>
        static void PoolHits(List<BenchmarkRun> runs, string marker, out int hits, out int frames)
        {
            hits = 0; frames = 0;
            foreach (var r in runs)
            {
                var h = r.Hotspot(marker);
                if (h == null || h.sampledFrames <= 0) continue;
                hits += h.hitFrames;
                frames += h.sampledFrames;
            }
        }

        static string ScriptPathOf(List<BenchmarkRun> runs, string marker)
        {
            foreach (var r in runs)
            {
                var h = r.Hotspot(marker);
                if (h != null && !string.IsNullOrEmpty(h.scriptPath)) return h.scriptPath;
            }
            return null;
        }

        static double Mean(List<double> values)
        {
            if (values == null || values.Count == 0) return 0;
            double sum = 0;
            foreach (var v in values) sum += v;
            return sum / values.Count;
        }

        /// <summary>
        /// Withholds a direction when the milliseconds this row PRINTS are the same on both sides.
        ///
        /// The verdict is decided on the relative move against the row's own band, and a cheap call path is stable,
        /// so its band is narrow and a microsecond of wobble clears it. Real case: Tim hid one tree, cutting 17.5%
        /// of the triangles, and the report came back "Something moved the wrong way" — on the strength of
        /// Inl_On Record Render Graph at +0.0034 ms/frame (+1.7% against a ±0.7% band), rendered on screen as
        /// "0.21 -> 0.21 ms  more expensive". Three microseconds a frame is 0.02% of a 60 FPS budget, and nothing
        /// anyone can act on.
        ///
        /// This is the same rule BenchmarkStats already applies to the percentage — decide on the figures the report
        /// prints, because a verdict the displayed numbers do not support is worse than no verdict, the reader can
        /// see it is wrong and cannot tell which half to believe. It was simply never applied to the milliseconds.
        /// Applies in both directions, so it cannot manufacture optimism either.
        /// </summary>
        static BenchmarkStats.Comparison VisibleInMilliseconds(BenchmarkStats.Comparison cmp, string key)
        {
            if (cmp.Verdict != DeltaVerdict.Improved && cmp.Verdict != DeltaVerdict.Regressed) return cmp;

            // Two decimals: what PairText and every sentence built from it show.
            if (Math.Round(cmp.Before, 2) != Math.Round(cmp.After, 2)) return cmp;

            return new BenchmarkStats.Comparison(key, DeltaVerdict.WithinNoise, cmp.Before, cmp.After,
                cmp.DeltaPercent, cmp.NoiseBandPercent, cmp.Stability, null, cmp.BandFrom);
        }

        static BenchmarkStats.Comparison NotCompared(string marker) =>
            new BenchmarkStats.Comparison(BenchmarkMetricKeys.HotspotKey(marker), DeltaVerdict.Incomparable,
                double.NaN, double.NaN, double.NaN, 0, MetricStability.NoData,
                L.Tr("only one side listed this marker", "只有一侧列出了这个 marker"));
    }
}
