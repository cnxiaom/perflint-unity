using System;
using System.Collections.Generic;
using System.IO;
using PerfLint.L10n;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// The measurement a later one is compared against: "this is how the scene ran before I changed anything."
    ///
    /// A benchmark session lives under a tick-stamped directory and is one of many; a baseline is the single one the
    /// user has chosen to hold on to, so it gets its own fixed location and survives the housekeeping that eventually
    /// clears the rest. Copying rather than referencing is deliberate — a baseline that could disappear when a
    /// session directory is cleaned up would take the only "before" number with it.
    ///
    /// The rule that shapes this type: a baseline made of a SINGLE run cannot support a verdict. With one run there
    /// is no run-to-run spread, so <see cref="BenchmarkStats.Compare"/> can only answer
    /// <see cref="DeltaVerdict.NoNoiseBand"/> — a difference exists, but nothing says whether it is bigger than
    /// doing nothing. <see cref="Pinned.HasNoiseBand"/> reports that plainly rather than letting the UI quietly
    /// present a one-run delta as proof.
    /// </summary>
    public static class BenchmarkBaseline
    {
        /// <summary>A pinned baseline as restored from disk.</summary>
        public sealed class Pinned
        {
            public BenchmarkSession Session { get; }
            public DateTime PinnedAtUtc { get; }
            /// <summary>Scene the baseline was taken in, for display. The GUID inside the fingerprint is what actually gates comparison.</summary>
            public string ScenePath { get; }
            /// <summary>Id of the benchmark session this was copied from, so the original runs can still be found.</summary>
            public string SourceSessionId { get; }

            public Pinned(BenchmarkSession session, DateTime pinnedAtUtc, string scenePath, string sourceSessionId)
            {
                Session = session;
                PinnedAtUtc = pinnedAtUtc;
                ScenePath = scenePath ?? "";
                SourceSessionId = sourceSessionId ?? "";
            }

            public int RunCount => Session?.Runs.Count ?? 0;

            /// <summary>
            /// Whether this baseline can support a verdict at all. False means every comparison against it will be
            /// <see cref="DeltaVerdict.NoNoiseBand"/>: a number without an answer to "is that more than noise?".
            /// </summary>
            public bool HasNoiseBand => RunCount >= 2;

            /// <summary>
            /// Figures this baseline's own repetitions disagree about too much to judge anything by.
            ///
            /// The premise underneath the whole before/after loop is that repeating a measurement with nothing
            /// changed varies only by machine noise. That premise fails on any scene that animates itself.
            /// Measured on GardenScene, whose Cinemachine chain tours the level with no input at all: three
            /// repetitions of one baseline read 435,595 / 436,682 / 686,850 triangles, at an unchanged 3840x2160.
            /// Which shot the sampling window landed on decided how much geometry was in frame.
            ///
            /// Nothing was wrong with what the comparison then did — a band that wide swallows every delta, so it
            /// reported no change and claimed nothing. What was wrong is that this is knowable the moment the
            /// baseline is pinned, and was not said until after the user had done the work. It cost two wasted
            /// measurements and a wasted fix on the day it was found.
            ///
            /// No new threshold, and no new judgement: <see cref="BenchmarkStats.Spread.Stability"/> already grades
            /// this, and the CLI legend already spells out what each grade means — MARGINAL is "only large changes
            /// are distinguishable", UNSTABLE is "do NOT report before/after deltas". The GUI simply never said it.
            ///
            /// Both grades are reported, because the useful thing to know is not "is this baseline broken" but
            /// "which of these figures will fail to notice my work". The distinction is real and was measured: the
            /// baseline above graded MARGINAL on triangles at cv 0.114, and it went on to prove a 25.4% cut in them
            /// — exactly what "only large changes are distinguishable" predicts. Calling that baseline unusable
            /// would have been the opposite error.
            ///
            /// Per figure, never for the baseline as a whole: the same three repetitions agreed about allocation
            /// per frame to 0.1%, and the memory result measured off that baseline was the clearest of the day.
            /// </summary>
            public IReadOnlyList<string> UnstableMetricKeys => KeysGradedAtLeast(MetricStability.Unstable);

            /// <summary>Figures whose run-to-run spread leaves them able to show only large changes, or nothing.</summary>
            public IReadOnlyList<string> ImpreciseMetricKeys => KeysGradedAtLeast(MetricStability.Marginal);

            List<string> KeysGradedAtLeast(MetricStability worst)
            {
                var hit = new List<string>();
                if (Session?.Runs == null || Session.Runs.Count < 2) return hit;
                foreach (var key in BenchmarkComparison.ComparedKeys)
                {
                    var s = BenchmarkStats.SpreadOf(Session.Runs, key);
                    if (!s.HasData) continue;
                    if (s.Stability == MetricStability.Unstable ||
                        (worst == MetricStability.Marginal && s.Stability == MetricStability.Marginal))
                        hit.Add(key);
                }
                return hit;
            }

            /// <summary>
            /// One sentence naming the figures this baseline cannot answer for, or null when it can answer for all
            /// of them. Says what to do about it, because the reader's options are real: measure a moment of the
            /// scene that holds still, or judge by the figures that survived.
            /// </summary>
            public string StabilityWarning
            {
                get
                {
                    var imprecise = ImpreciseMetricKeys;
                    if (imprecise.Count == 0) return null;

                    var names = new List<string>(imprecise.Count);
                    foreach (var k in imprecise) names.Add(BenchmarkMetricKeys.ShortLabel(k));
                    string list = string.Join(", ", names);

                    // The two grades say different things and must not be flattened into one alarm. "Only large
                    // changes will register" is a caution; "nothing will register" is a refusal.
                    bool anyHopeless = UnstableMetricKeys.Count > 0;
                    string cause = L.Tr(" Usually the scene moves on its own between repetitions — a camera tour, a timeline, an idle animation. Measuring a part of the scene that holds still sharpens these.",
                                        " 通常是场景自己在动——运镜、Timeline、待机动画。改测场景中静止的一段，这几项就会变准。");

                    return anyHopeless
                        ? L.Tr($"This baseline's {RunCount} repetitions disagree about {list} by more than any change you could make, so nothing will be provable in {(imprecise.Count == 1 ? "that figure" : "those figures")}. The other figures are unaffected.",
                               $"这条基线的 {RunCount} 轮重复在 {list} 上的分歧，比你能做出的任何改动都大，因此{(imprecise.Count == 1 ? "这一项" : "这些项")}之后证明不了任何事。其余指标不受影响。") + cause
                        : L.Tr($"This baseline's {RunCount} repetitions vary enough in {list} that only a large change will show there — a small win will read as no measurable change. The other figures are unaffected.",
                               $"这条基线的 {RunCount} 轮重复在 {list} 上的波动较大，只有幅度大的改动才看得见——小的改善会被报成「无可测出的变化」。其余指标不受影响。") + cause;
                }
            }

            public string SceneName =>
                string.IsNullOrEmpty(ScenePath) ? "" : Path.GetFileNameWithoutExtension(ScenePath);

            /// <summary>Headline frame time of the baseline (median across runs), or NaN when it wasn't measured.</summary>
            public double FrameMsMedian
            {
                get
                {
                    var s = BenchmarkStats.SpreadOf(Session?.Runs, BenchmarkMetricKeys.FrameTimeMs);
                    return s.HasData ? s.Mean : double.NaN;
                }
            }
        }

        /// <summary>Minimum repetitions a baseline needs before a comparison against it can say anything.</summary>
        public const int MinRunsForNoiseBand = 2;

        static string Dir
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint", "baseline");
            }
        }

        static string MetaPath => Path.Combine(Dir, "meta.json");

        public static bool Exists()
        {
            try { return File.Exists(MetaPath); }
            catch { return false; }
        }

        /// <summary>
        /// Copies a completed session in as the baseline. Returns null on success, or a human-readable refusal.
        ///
        /// The refusal is the point: a baseline recorded under a VSync cap is not a slow "before" that later gets
        /// beaten — it is a broken ruler, and every delta measured against it would be an artefact of the ruler
        /// rather than of the user's work.
        ///
        /// Deep Profile is no longer refused here. It is a broken ruler for TIME, and the comparison drops every
        /// wall-clock row when either side has it on; but it is the only ruler that reads per-method markers, and a
        /// hit rate does not care about instrumentation. Refusing the whole baseline threw the valid half away too.
        /// </summary>
        public static string Pin(BenchmarkSession session, string scenePath = null)
        {
            if (session == null || !session.HasRuns)
                return L.Tr("that measurement has no completed runs", "这次测量没有完成任何一轮");

            string warning = session.Fingerprint?.UsabilityWarning();
            if (!string.IsNullOrEmpty(warning))
                return L.Tr("that measurement can't serve as a baseline — ", "这次测量不能作为基线——") + warning;

            // Same class of refusal as VSync and Deep Profile above: not a slow measurement, a measurement of
            // nothing in particular. A sampling window that straddled a scene load is part loading screen and part
            // level, and repetitions taken in different scenes are not repetitions of anything — pinning either as
            // the "before" would poison every verdict measured against it afterwards.
            string mixed = BenchmarkSceneTruth.RefusalFor(session);
            if (!string.IsNullOrEmpty(mixed))
                return L.Tr("that measurement can't serve as a baseline — ", "这次测量不能作为基线——") + mixed;

            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
                Directory.CreateDirectory(Dir);

                if (session.Spec != null)
                    File.WriteAllText(Path.Combine(Dir, "spec.json"), session.Spec.ToJson());

                for (int i = 0; i < session.Runs.Count; i++)
                    File.WriteAllText(Path.Combine(Dir, $"run-{i:000}.json"), session.Runs[i].ToJson());

                var meta = new MetaDto
                {
                    pinnedAtUtcTicks = DateTime.UtcNow.Ticks,
                    sourceSessionId = session.Id ?? "",
                    scenePath = scenePath ?? session.Spec?.scenePath ?? "",
                    runCount = session.Runs.Count
                };
                File.WriteAllText(MetaPath, JsonUtility.ToJson(meta, true));
                return null;
            }
            catch (Exception e)
            {
                return L.Tr("could not write the baseline: ", "写入基线失败：") + e.Message;
            }
        }

        /// <summary>Restores the pinned baseline, or null when there is none / it cannot be read.</summary>
        public static Pinned Load()
        {
            try
            {
                if (!File.Exists(MetaPath)) return null;

                var meta = JsonUtility.FromJson<MetaDto>(File.ReadAllText(MetaPath));
                if (meta == null) return null;

                BenchmarkSpec spec = null;
                string sp = Path.Combine(Dir, "spec.json");
                if (File.Exists(sp)) spec = BenchmarkSpec.FromJson(File.ReadAllText(sp));

                var runs = new List<BenchmarkRun>();
                var files = Directory.GetFiles(Dir, "run-*.json");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    var r = BenchmarkRun.FromJson(File.ReadAllText(f));
                    if (r != null) runs.Add(r);
                }

                // A meta file with no readable runs is a half-written baseline, not an empty one. Reporting "no
                // baseline" is the safe reading: it prompts the user to make one instead of silently comparing
                // against nothing.
                if (runs.Count == 0) return null;

                return new Pinned(
                    new BenchmarkSession(meta.sourceSessionId, spec, runs),
                    new DateTime(meta.pinnedAtUtcTicks, DateTimeKind.Utc),
                    meta.scenePath,
                    meta.sourceSessionId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"Could not read the pinned baseline (treating as none): {e.Message}",
                    $"无法读取已固定的基线（按未设置处理）：{e.Message}"));
                return null;
            }
        }

        public static void Clear()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"Could not clear the baseline: {e.Message}", $"清除基线失败：{e.Message}"));
            }
        }

        [Serializable]
        sealed class MetaDto
        {
            public long pinnedAtUtcTicks;
            public string sourceSessionId;
            public string scenePath;
            public int runCount;
        }
    }
}
