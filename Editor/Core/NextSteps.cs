using System;
using System.Collections.Generic;
using System.Linq;
using PerfLint.L10n;

namespace PerfLint.Core
{
    /// <summary>What a finding, if resolved, would actually move.</summary>
    public enum PerfAxis
    {
        CpuFrameTime,
        GpuFrameTime,
        /// <summary>Hitches rather than sustained cost — GC spikes, one-off freezes.</summary>
        Stutter,
        Memory,
        BuildSize,
        /// <summary>Correctness or hygiene: real work, but it does not move a performance number.</summary>
        None
    }

    public static class PerfAxisInfo
    {
        /// <summary>
        /// Whether a Play Mode sample can see this axis at all.
        ///
        /// Build size cannot be: it is decided by a build, and no amount of sampling a running scene will show it.
        /// This is what keeps "apply and re-measure" from being offered for work whose result the measurement is
        /// structurally unable to report — a re-measurement after compressing 254 meshes would come back "no
        /// measurable change" and be read as "it didn't help", when nothing was ever going to move.
        /// </summary>
        public static bool MeasurableInPlayMode(PerfAxis a) =>
            a == PerfAxis.CpuFrameTime || a == PerfAxis.GpuFrameTime || a == PerfAxis.Stutter || a == PerfAxis.Memory;

        public static bool AnyMeasurableInPlayMode(System.Collections.Generic.IEnumerable<PerfAxis> axes)
        {
            if (axes == null) return false;
            foreach (var a in axes) if (MeasurableInPlayMode(a)) return true;
            return false;
        }
    }

    /// <summary>One recommended action, with the four things a non-expert needs in order to decide.</summary>
    public sealed class NextStep
    {
        public Finding Finding { get; }
        public double Score { get; }
        public IReadOnlyList<PerfAxis> Axes { get; }
        /// <summary>Why this matters for THIS project right now — derived from the measurement, not from canned per-rule prose.</summary>
        public string WhyNow { get; }
        public string Expected { get; }
        public string Risk { get; }
        public string Undo { get; }
        /// <summary>True when the measurement says this is not currently on the critical path. Such a step is still listed, but honestly labelled.</summary>
        public bool OffCriticalPath { get; }

        /// <summary>
        /// Whether <see cref="Expected"/> carries an actual figure rather than a refusal to invent one.
        ///
        /// Most findings have no honest estimate, so Expected is usually a sentence saying so — which is right in a
        /// four-field card and wrong as a list item's one-line description, where it renders as "No reliable estimate
        /// for this one" three times in a row while the informative sentence sits unused in <see cref="WhyNow"/>.
        /// </summary>
        public bool HasEstimate { get; }

        public NextStep(Finding finding, double score, IReadOnlyList<PerfAxis> axes,
            string whyNow, string expected, string risk, string undo, bool offCriticalPath,
            bool hasEstimate = false)
        {
            HasEstimate = hasEstimate;
            Finding = finding;
            Score = score;
            Axes = axes;
            WhyNow = whyNow;
            Expected = expected;
            Risk = risk;
            Undo = undo;
            OffCriticalPath = offCriticalPath;
        }
    }

    /// <summary>
    /// How much of a round's work a Play Mode measurement could see at all.
    ///
    /// <see cref="PerfAxisInfo.MeasurableInPlayMode"/> existed for a year with zero callers, and this is the line
    /// that was never connected. Measured on URP 3D Sample / GardenScene: the round's applicable pool was
    /// PERF.MSH001 (memory) and PERF.MSH002 (build size, 239 places, one click), and directly under it sat
    /// "I have done these — measure and compare". Applying the second and then measuring can only come back "no
    /// measurable change" — which reads as "it didn't help" for work that was never going to move a runtime figure.
    ///
    /// So the round counts what a sample can see BEFORE it offers to take one. Counting rather than filtering,
    /// because the same round usually also holds work a measurement CAN speak for; suppressing the button outright
    /// would trade one wrong answer for another.
    /// </summary>
    public readonly struct RoundVisibility
    {
        /// <summary>Items in the round.</summary>
        public int Total { get; }
        /// <summary>Of those, the ones no Play Mode sample can report on, whatever the user does to them.</summary>
        public int Blind { get; }

        public RoundVisibility(int total, int blind) { Total = total; Blind = blind; }

        /// <summary>Items a measurement could speak for.</summary>
        public int Visible => Total - Blind;

        /// <summary>Every item in this round is invisible to a measurement — the case where offering one is a trap.</summary>
        public bool NothingVisible => Total > 0 && Blind >= Total;

        /// <summary>Part of it can be seen and part cannot: the comparison is worth taking, but it cannot speak for all of it.</summary>
        public bool PartlyBlind => Blind > 0 && Blind < Total;

        public static RoundVisibility Of(params IEnumerable<NextStep>[] lists)
        {
            int total = 0, blind = 0;
            if (lists != null)
                foreach (var list in lists)
                {
                    if (list == null) continue;
                    foreach (var s in list)
                    {
                        if (s == null) continue;
                        total++;
                        if (!PerfAxisInfo.AnyMeasurableInPlayMode(s.Axes)) blind++;
                    }
                }
            return new RoundVisibility(total, blind);
        }
    }

    /// <summary>
    /// Turns a wall of findings into "do this next".
    ///
    /// The ranking exists because severity ordering is actively misleading once you have a measurement. Real case
    /// (urp-viking-village, 2026-07-26): RUN.GPU002 flagged 14M triangles per frame as Critical while the frame was
    /// CPU-bound — sorting by severity puts the one thing that would change nothing at the top of the list. What
    /// makes a finding matter is not how alarming it is in the abstract, but whether it moves the number that is
    /// currently missing the target.
    ///
    /// So: score = relevance to the failing metric × strength of the evidence × how executable it is × how firm the
    /// estimate is. Anything whose impact we cannot classify is damped rather than promoted — a wrong "do this
    /// first" costs more than a missing one.
    ///
    /// Pure function over plain inputs; no Unity API, no sampling types. Unit-tested.
    /// </summary>
    public static class NextSteps
    {
        /// <summary>
        /// Rules that describe the situation rather than propose work. These are the headline, not a step — listing
        /// "the CPU is your bottleneck" as an action to take would be nonsense.
        /// </summary>
        static readonly HashSet<string> ContextOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "RUN.GPU001", // the CPU-vs-GPU verdict itself
            "RUN.HOT000", // "no hotspots stood out"
        };

        /// <summary>
        /// Which performance axis each rule family moves. Prefix-matched, longest prefix wins.
        ///
        /// Incomplete on purpose: an unrecognised rule falls back to its domain and is damped (see
        /// <see cref="AxesOf"/>), so a rule added later can never be promoted to "do this first" on the strength of
        /// a guess about what it does.
        /// </summary>
        static readonly (string Prefix, PerfAxis[] Axes)[] RuleAxes =
        {
            // Runtime, measured
            ("RUN.FPS003", new[] { PerfAxis.Stutter }),
            ("RUN.FPS",    new[] { PerfAxis.CpuFrameTime }),
            ("RUN.GC",     new[] { PerfAxis.CpuFrameTime, PerfAxis.Stutter }),
            // An invitation to re-measure with more instrumentation, not work that moves a number — and turning Deep
            // Profile on makes the frame SLOWER. Inheriting CpuFrameTime from the RUN.HOT prefix put it in the same
            // group as the allocation finding, so the screen said "2 of these 3 move one number (CPU frame time),
            // work on #1 and you are moving the number the other one is about" about a diagnostic step. Same family
            // as RUN.HOT000 ("no hotspots stood out"), which is already excluded as context rather than a step.
            //
            // Relevance for None is 0, so it ranks last and stops taking a slot in the round.
            //
            // This used to note a second effect — that it also retired a duplicate, since the allocation finding
            // carried the same "Turn on Deep Profile" button. That is no longer true and the note would mislead:
            // RUN.GC001 is attributed from allocation callstacks now and never offers the toggle. The reason above
            // stands on its own, which is why the entry does not move.
            ("RUN.HOT003", new[] { PerfAxis.None }),
            ("RUN.HOT",    new[] { PerfAxis.CpuFrameTime }),
            ("RUN.MEM",    new[] { PerfAxis.Memory }),
            ("RUN.DRAW",   new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime }),
            ("RUN.SETPASS",new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime }),
            ("RUN.GPU002", new[] { PerfAxis.GpuFrameTime }),
            ("RUN.GPU004", new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime }),
            ("RUN.GPU",    new[] { PerfAxis.GpuFrameTime }),

            // Static: scripts
            ("PERF.GC",    new[] { PerfAxis.CpuFrameTime, PerfAxis.Stutter }),
            ("PERF.UPD",   new[] { PerfAxis.CpuFrameTime }),
            ("PERF.LOG",   new[] { PerfAxis.CpuFrameTime }),

            // Static: rendering / batching
            ("PERF.INST",  new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime }),
            ("PERF.SBATCH",new[] { PerfAxis.Memory }),          // trades draw calls FOR memory — the payoff is memory
            ("PERF.MAT",   new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime }),
            ("MAT",        new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime }),

            // Static: assets
            ("PERF.TEXSTR",new[] { PerfAxis.Memory }),
            ("PERF.TEX",   new[] { PerfAxis.Memory }),
            ("PERF.MSH",   new[] { PerfAxis.Memory }),
            // MSH002 is the exception to the MSH prefix, and the longest match wins. Mesh Compression quantizes the
            // stored vertex data — it shrinks the file, and the mesh is expanded again on load, so runtime memory is
            // not what moves. The scanner has always said so ("inflating build size", "reduces disk usage"); only the
            // axis disagreed, and the axis is what generates the card, so a project was told that compressing 254
            // meshes would "reduce how much the game holds in memory". MSH001 (Read/Write enabled) really is memory —
            // it keeps a second CPU-side copy — which is why this is a per-rule exception and not a prefix change.
            ("PERF.MSH002",new[] { PerfAxis.BuildSize }),
            ("PERF.AUD",   new[] { PerfAxis.Memory }),
            ("ASSET.DUP",  new[] { PerfAxis.BuildSize, PerfAxis.Memory }),
            ("ASSET.AADUP",new[] { PerfAxis.BuildSize }),
            ("ASSET.ABDUP",new[] { PerfAxis.BuildSize }),
            ("ASSET.UNREF",new[] { PerfAxis.BuildSize }),
            ("ASSET",      new[] { PerfAxis.BuildSize }),

            // Shaders: variants are build size and load time; the rest is GPU work
            ("SHDR",       new[] { PerfAxis.BuildSize, PerfAxis.GpuFrameTime }),

            // Migration / project settings move correctness, not a performance number
            ("MIG",        new[] { PerfAxis.None }),
        };

        static readonly PerfAxis[] NoAxis = { PerfAxis.None };

        /// <summary>Axes a finding would move. Unknown rules fall back to their domain, and <see cref="PerfAxis.None"/> when even that says nothing.</summary>
        /// <summary>
        /// Axes a RULE moves, without needing a finding. The journal records what a round changed as rule ids and
        /// nothing else, so a report that wants to know what the work was aimed at has only this to go on.
        /// </summary>
        public static IReadOnlyList<PerfAxis> AxesOfRule(string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId)) return NoAxis;
            PerfAxis[] best = null;
            int bestLen = -1;
            foreach (var (prefix, axes) in RuleAxes)
                if (ruleId.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > bestLen)
                { best = axes; bestLen = prefix.Length; }
            return best ?? NoAxis;
        }

        public static IReadOnlyList<PerfAxis> AxesOf(Finding f)
        {
            if (f == null || string.IsNullOrEmpty(f.RuleId)) return NoAxis;

            PerfAxis[] best = null;
            int bestLen = -1;
            foreach (var (prefix, axes) in RuleAxes)
            {
                if (f.RuleId.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > bestLen)
                {
                    best = axes; bestLen = prefix.Length;
                }
            }
            if (best != null) return best;

            return f.Domain switch
            {
                Domain.Performance => new[] { PerfAxis.CpuFrameTime, PerfAxis.GpuFrameTime },
                Domain.Assets => new[] { PerfAxis.BuildSize, PerfAxis.Memory },
                Domain.Runtime => new[] { PerfAxis.CpuFrameTime },
                _ => NoAxis
            };
        }

        /// <summary>
        /// True when the rule was matched explicitly rather than guessed from its domain.
        ///
        /// Public because "we do not recognise this rule" and "this rule moves nothing" are different statements that
        /// <see cref="AxesOfRule"/> cannot tell apart — both come back as <see cref="PerfAxis.None"/>. Anything that
        /// reasons from an axis being unmeasurable has to ask this first, or an unknown rule id gets described as
        /// build-size work on the strength of a fallback.
        /// </summary>
        public static bool IsKnownRuleId(string ruleId) =>
            !string.IsNullOrEmpty(ruleId) &&
            RuleAxes.Any(r => ruleId.StartsWith(r.Prefix, StringComparison.Ordinal));

        static bool IsKnownRule(Finding f) => f != null && IsKnownRuleId(f.RuleId);

        /// <summary>
        /// How much this axis matters given what the measurement says is actually wrong. 0 = would change nothing
        /// that is currently a problem.
        /// </summary>
        public static double Relevance(PerfAxis axis, PerfGoal goal, PerfMeasurement m)
        {
            var status = m.StatusAgainst(goal);
            var side = m.Side;

            switch (axis)
            {
                case PerfAxis.None:
                    return 0.0;

                case PerfAxis.CpuFrameTime:
                    // Capped is not "fine" — it is "unmeasured". Treated like no data rather than like meeting the
                    // budget, because a VSync-limited reading proves nothing either way.
                    if (status == FrameStatus.Unknown || status == FrameStatus.Capped) return 0.6;
                    if (status == FrameStatus.Meeting) return 0.25;          // already inside budget
                    return side == Bottleneck.Gpu ? 0.3                      // the GPU is the one spending the frame
                         : side == Bottleneck.Cpu ? 1.0                      // measured: this is the critical path
                         : 0.8;                                              // balanced or unknown side

                case PerfAxis.GpuFrameTime:
                    if (status == FrameStatus.Unknown || status == FrameStatus.Capped) return 0.6;
                    if (status == FrameStatus.Meeting) return 0.25;
                    return side == Bottleneck.Cpu ? 0.2                      // GPU is idle — this is not what's costing you
                         : side == Bottleneck.Gpu ? 1.0
                         : 0.8;

                case PerfAxis.Stutter:
                    // A hitch is felt regardless of average frame time, so it never fully drops out.
                    return status == FrameStatus.Meeting ? 0.6 : 0.9;

                case PerfAxis.Memory:
                    // Editor memory can't be checked against an absolute budget (it includes the editor), so this is
                    // driven by observed growth — a heap that keeps climbing ends as a crash on the device.
                    // Deliberately capped BELOW a failing frame-time axis: the user set a frame-rate goal, and
                    // answering it with an import setting is how "goal-driven" quietly stops being goal-driven.
                    return m.HasData && m.MemoryGrowthBytes > 64L * 1024 * 1024 ? 0.75 : 0.45;

                case PerfAxis.BuildSize:
                    // Build size is the one axis no measurement here can speak to, so it cannot be driven by a reading
                    // the way the frame-time axes are. What it CAN read is whether the frame-rate goal still wants the
                    // attention: while the target is missed (or unmeasured, or sitting on the line) this stays below a
                    // failing frame-time axis, for the same reason Memory does — answering a frame-rate goal with an
                    // import setting is how "goal-driven" quietly stops being goal-driven.
                    //
                    // Once the goal IS met, that argument is spent, and holding build size at a fixed 0.4 had a cost:
                    // on a project hitting its target, the ranking went on nominating CPU items it had itself scored at
                    // 0.25, while hundreds of duplicate-packed assets never surfaced. Meeting the frame budget is
                    // exactly when shipping size becomes the thing worth working on.
                    //
                    // Deliberately still under Stutter (0.6): a hitch is felt by the player, and no amount of build
                    // size is. Deliberately NOT scaled by how many bytes the scan estimates — those figures are
                    // approximations we refuse to present as exact, and a ranking that multiplies by them would be
                    // making a precise claim out of them.
                    return status == FrameStatus.Meeting ? 0.55 : 0.4;

                default:
                    return 0.4;
            }
        }

        /// <summary>
        /// How executable the finding is. A recommendation nobody can act on is worth less than one that is a click —
        /// but only somewhat: "this is what's costing you, here's how to find the exact line" is still the right next
        /// step, and halving it let an unrelated one-click import setting outrank the measured bottleneck.
        /// </summary>
        static double Actionability(Finding f) =>
            f.CanAutoFix || f.WasAutoFixable ? 1.0 :
            f.HasAction || f.WasActionable ? 0.85 :
            f.AiFixable ? 0.75 :
            0.65;

        /// <summary>Measured beats inferred: a runtime finding watched the problem happen, a static one reasoned it should.</summary>
        static double Evidence(Finding f) => f.Domain == Domain.Runtime ? 1.0 : 0.65;

        /// <summary>A ceiling estimate ("up to X, depending") must not outrank a firm one.</summary>
        static double Confidence(Finding f) => f.SavingsAreCeiling ? 0.6 : 1.0;

        /// <summary>
        /// How much this rule is worth, in orders of magnitude — the one thing the score did not read.
        ///
        /// Measured on the reference project, with every other factor identical between these three:
        ///
        ///   PERF.MSH002     0 B      rank 3    (one-click, so Actionability 1.0)
        ///   ASSET.AADUP001  178.5 MB rank 1    (has an Action,  0.85)
        ///   ASSET.AARES001  4.5 GB   rank 16   (no button,      0.65)
        ///
        /// A rule saving nothing outranked one saving 4.5 GB by thirteen places, and the entire gap was
        /// <see cref="Actionability"/> — 0.85/0.65 = 1.3077, exactly the ratio of their scores. That factor is
        /// supposed to weigh "can PerfLint click this for you"; between asset rules, where the other four factors are
        /// equal, it had become the only thing being weighed at all.
        ///
        /// ORDERS OF MAGNITUDE, never the raw byte count. The previous decision here — recorded in the BuildSize
        /// comment as "deliberately NOT scaled by how many bytes the scan estimates" — was half right: multiplying by
        /// an approximation does dress it up as precision. But 4.5 GB and 0 B do not differ by precision, they differ
        /// by five orders of magnitude, and refusing to see that is not caution, it is discarding the number. Buckets
        /// this wide cannot be moved by estimate error; only a genuinely different scale crosses one.
        ///
        /// Applied ONLY when the rule is being recommended for its bytes — i.e. its highest-relevance axis is Memory
        /// or BuildSize. A CPU rule has no byte estimate because bytes are not its unit, and damping it for that would
        /// punish every script and shader finding for a category error. Same reason a shader rule counted as GPU work
        /// while the frame is GPU-bound is left alone: it is not on this screen because of its size.
        /// </summary>
        static double Payoff(PerfAxis topAxis, long mem, long build)
        {
            if (topAxis != PerfAxis.Memory && topAxis != PerfAxis.BuildSize) return 1.0;

            // Max, not sum: the two are different resources measured in the same unit, and adding them would invent a
            // quantity that is neither.
            long bytes = Math.Max(mem, build);

            if (bytes >= 1L << 30) return 1.30;      // a gigabyte or more
            if (bytes >= 100L << 20) return 1.15;    // hundreds of megabytes
            if (bytes >= 1L << 20) return 1.00;      // megabytes — the neutral case
            return 0.85;                             // under a megabyte, including "the scan estimated zero"
        }

        /// <summary>
        /// Ranks findings into "do this next", one entry per rule (not per instance — 200 uncompressed textures are
        /// one decision, not 200).
        /// </summary>
        public static List<NextStep> Rank(IReadOnlyList<Finding> findings, PerfGoal goal, PerfMeasurement measurement, int take = 3)
        {
            var steps = new List<NextStep>();
            if (findings == null) return steps;

            foreach (var group in findings.Where(f => f != null && !ContextOnly.Contains(f.RuleId))
                                          .GroupBy(f => f.RuleId, StringComparer.Ordinal))
            {
                // Represent the rule by its most severe instance, and prefer one that is actually fixable so the
                // card's action reflects what the user can do.
                var rep = group.OrderByDescending(f => f.CanAutoFix || f.HasAction)
                               .ThenByDescending(f => f.Severity)
                               .First();

                // What the RULE is worth, not what its representative is worth.
                //
                // The card says "366 places" and then quoted the representative finding's own estimate: 2.3 MB for a
                // rule totalling 178.5 MB, 619 KB for one totalling 249.9 MB — off by 78x and 400x, next to a count
                // that promised the whole set. Third time this exact shape has appeared (the row count that read 292
                // paths for 107 findings; the manual tier, which had to sum the rule itself), so the total is computed
                // here once, where the grouping already exists, instead of at each place that needs it.
                long ruleMem = 0, ruleBuild = 0;
                foreach (var f in group)
                {
                    ruleMem += f.EstimatedMemorySavingsBytes;
                    ruleBuild += f.EstimatedBuildSavingsBytes;
                }

                var axes = AxesOf(rep);
                // Which axis wins, not just how much it wins by: Payoff has to know whether this rule is on the
                // screen for its bytes or for its frame time before deciding whether bytes may weigh on it.
                double relevance = 0;
                var topAxis = PerfAxis.None;
                foreach (var a in axes)
                {
                    double r = Relevance(a, goal, measurement);
                    if (r > relevance) { relevance = r; topAxis = a; }
                }
                if (relevance <= 0) continue; // moves nothing that is currently a problem

                double score = relevance * Evidence(rep) * Actionability(rep) * Confidence(rep)
                             * Payoff(topAxis, ruleMem, ruleBuild);

                // Severity still counts — just as a modifier, not as the sort key.
                score *= rep.Severity == Severity.Critical ? 1.15 : rep.Severity == Severity.Warning ? 1.0 : 0.85;

                // An unrecognised rule is damped so it can never take the top slot on a guess about what it moves.
                if (!IsKnownRule(rep)) score *= 0.5;

                bool offPath = measurement.HasData && relevance <= 0.3;

                steps.Add(new NextStep(
                    rep, score, axes,
                    WhyNow(rep, axes, goal, measurement, group.Count(), ruleMem, ruleBuild),
                    Expected(rep, axes, ruleMem, ruleBuild),
                    Risk(rep),
                    Undo(rep),
                    offPath,
                    rep.EstimatedMemorySavingsBytes > 0 || rep.EstimatedBuildSavingsBytes > 0));
            }

            return steps.OrderByDescending(s => s.Score)
                        .ThenBy(s => s.Finding.RuleId, StringComparer.Ordinal)
                        .Take(Math.Max(1, take))
                        .ToList();
        }

        /// <summary>
        /// One sentence stating where the project stands against the goal. This is the context every step is judged
        /// against, so it says plainly when there is no measurement rather than implying one.
        /// </summary>
        public static string Headline(PerfGoal goal, PerfMeasurement m)
        {
            if (!m.HasData)
                return L.Tr($"No runtime measurement yet — sample your scene in Play Mode to find out whether you're hitting {goal.TargetFps} FPS and what's stopping you.",
                            $"还没有运行时实测——在 Play Mode 里采样一段，才能知道你有没有达到 {goal.TargetFps} FPS、以及卡在哪。");

            // Deep Profile inflates main-thread time several-fold, so this figure is the profiler's cost. The header
            // already refuses to grade it; this line was still printing "18.0 ms against an 8.3 ms budget — you're
            // over it" from the same number, which is the same wrong claim in a quieter font.
            if (m.TimingsInflated)
                return L.Tr($"Measured with Deep Profile on ({m.FrameMsMedian:0.0} ms per frame), so this is the profiler's own cost and says nothing about whether you can hit {goal.TargetFps} FPS. Deep Profile is for finding out WHICH methods run and how often; turn it off and sample again for a frame time.",
                            $"这次采样开着 Deep Profile（每帧 {m.FrameMsMedian:0.0} ms），所以这是 Profiler 自身的开销，说明不了你能否达到 {goal.TargetFps} FPS。Deep Profile 用来查**哪些方法在跑、跑多勤**；要看帧时间请关掉它重新采样。");

            // A capped reading is the cap, not the machine. Reporting it against the target would claim to know
            // something the measurement cannot support in either direction.
            if (m.FrameRateCapped)
                return L.Tr($"Frame rate was capped while sampling ({m.FrameMsMedian:0.0} ms per frame), so this says nothing about whether you can hit {goal.TargetFps} FPS. Turn off VSync in Quality Settings (and leave Target Frame Rate uncapped), then sample again to see your real headroom.",
                            $"采样时帧率被钳制（每帧 {m.FrameMsMedian:0.0} ms），这个数字无法说明你能否达到 {goal.TargetFps} FPS。请在 Quality Settings 里关掉 VSync（并让 Target Frame Rate 保持不限制）后重新采样，才能看到真实余量。");

            // The reading is the editor's whole frame, which includes the editor drawing its own windows — work no
            // build performs. Says so and stops, rather than grading a project by it. Must come BEFORE the verdict
            // switch below: that switch has no Unknown arm and would announce "you're over it".
            if (m.FrameTimeIsEditorWide)
                return L.Tr($"This sample only captured the whole editor frame ({m.FrameMsMedian:0.0} ms), which includes the editor drawing its own windows — work no build performs — so it cannot say whether you can hit {goal.TargetFps} FPS. Sample again, for longer, to get your game's own frame time.",
                            $"这次只测到含编辑器窗口的整帧（每帧 {m.FrameMsMedian:0.0} ms），里面含编辑器画自己界面的开销——构建里没有这部分——所以判不了能否达到 {goal.TargetFps} FPS。重新采样一段（长一些）才能拿到游戏自身的帧时间。");

            double budget = goal.FrameBudgetMs;
            // "here in the Editor, your game's part of the frame" — never just "your frame time". This figure is the
            // game side of an EDITOR frame on THIS machine; it is not the target device and must never read as it.
            string core = L.Tr($"your game's part of the frame measured {m.FrameMsMedian:0.0} ms here in the Editor, against a {budget:0.0} ms budget for {goal.TargetFps} FPS",
                               $"本机 Editor 中你的游戏部分每帧 {m.FrameMsMedian:0.0} ms，{goal.TargetFps} FPS 的预算是 {budget:0.0} ms");

            // "Sustained", not just "inside it".
            //
            // The verdict is a MEDIAN, and a median is silent about hitches by construction: a scene can sit at half
            // its budget and still drop a 150 ms frame. Saying "you're inside it" invites the reader to conclude the
            // frame-rate question is closed, when what was answered is only the steady-state half of it.
            //
            // Deliberately does NOT quote a p95 here. Two reasons, and they point the same way: the game-side series
            // is merged frames (38 on the reference project) where a p95 is exactly the statistic that is not stable —
            // RuntimeAnalyzer says so where it takes its own median — and the counter-side p95 that IS stable is the
            // editor-wide one this file just stopped judging by. Hitches have a rule of their own (RUN.FPS003, on the
            // Stutter axis, which outranks build size even when the goal is met), so the honest move is to stop
            // overclaiming here and let that rule speak, not to import a shaky number to look thorough.
            string verdict = m.StatusAgainst(goal) switch
            {
                FrameStatus.Meeting => L.Tr("sustained frame time is inside it", "稳态帧时间已达标"),
                FrameStatus.Marginal => L.Tr("you're sitting right on the line", "正好卡在线上"),
                _ => L.Tr("you're over it", "超了")
            };

            string side = m.Side switch
            {
                Bottleneck.Cpu => L.Tr(" · the CPU is spending it", " · 时间花在 CPU 上"),
                Bottleneck.Gpu => L.Tr(" · the GPU is spending it", " · 时间花在 GPU 上"),
                Bottleneck.Balanced => L.Tr(" · CPU and GPU are about even", " · CPU 与 GPU 大致持平"),
                _ => L.Tr(" · which side is spending it couldn't be measured", " · 无法判定时间花在哪一侧")
            };

            return L.Tr($"{core} — {verdict}{side}.", $"{core}——{verdict}{side}。");
        }

        // ── Card copy, generated from the diagnosis rather than written per rule ──

        /// <param name="ruleMem">The RULE's total memory estimate; picks which asset axis describes it. See Rank.</param>
        /// <param name="ruleBuild">The RULE's total build-size estimate.</param>
        static string WhyNow(Finding f, IReadOnlyList<PerfAxis> axes, PerfGoal goal, PerfMeasurement m, int instanceCount,
                             long ruleMem, long ruleBuild)
        {
            string scale = instanceCount > 1
                ? L.Tr($" ({instanceCount} places)", $"（{instanceCount} 处）")
                : "";

            // A measured finding IS the observation. A static one merely shares an axis with it, and saying
            // "memory climbed 142 MB" next to an audio import setting reads as cause and effect when nothing
            // established that. Static findings get contribution wording, not attribution.
            bool measured = f.Domain == Domain.Runtime;

            if (!m.HasData)
                return L.Tr($"No measurement yet, so this is ranked on the static scan alone{scale}. Sample in Play Mode to confirm it's really what's holding you back.",
                            $"还没有实测数据，这条只依据静态扫描排序{scale}。在 Play Mode 采样一次才能确认它是不是真的拖累你。");

            var side = m.Side;
            bool cpu = axes.Contains(PerfAxis.CpuFrameTime);
            bool gpu = axes.Contains(PerfAxis.GpuFrameTime);

            if (gpu && !cpu && side == Bottleneck.Cpu)
                return L.Tr($"Not what's costing you right now{scale}: the GPU is only using {m.GpuMsMedian:0.0} ms of your {m.FrameMsMedian:0.0} ms frame. Worth doing before you add scene complexity, not first.",
                            $"这条现在不是瓶颈{scale}：GPU 只用掉 {m.FrameMsMedian:0.0} ms 帧时间里的 {m.GpuMsMedian:0.0} ms。等场景变复杂前再做，不必现在排第一。");

            // Two facts, side by side — not a claim about the link between them.
            //
            // "On the critical path" asserted that THIS finding is part of what is costing the frame. Nothing
            // establishes that: the input is a rule's axis label (MAT* is mapped to CPU/GPU for the whole family)
            // multiplied by one whole-frame bottleneck verdict. No asset residency, no render hit, no call path, no
            // time contribution. "653 places" is a scan count, not 653 pieces of runtime evidence — and it was the
            // sentence directly under the product's single loudest conclusion.
            //
            // What survives is what was actually measured, and what is actually known about the rule. A reader can
            // draw the connection; the tool must not draw it for them. "critical path" is retired as a term here: it
            // is borrowed from scheduling, a frame has no such thing, and it was the word doing all the asserting.
            if (cpu && side == Bottleneck.Cpu)
                return L.Tr($"This rule affects CPU frame time{scale}, and the frame is currently CPU-bound at {m.FrameMsMedian:0.0} ms. Whether these particular ones are part of that is not measured.",
                            $"这条规则影响 CPU 帧时间{scale}，而当前帧受限于 CPU（{m.FrameMsMedian:0.0} ms）。这些具体条目是否属于那部分开销，并未实测。");

            if (gpu && side == Bottleneck.Gpu)
                return L.Tr($"This rule affects GPU frame time{scale}, and the GPU is spending {m.GpuMsMedian:0.0} ms of a {m.FrameMsMedian:0.0} ms frame. Whether these particular ones are part of that is not measured.",
                            $"这条规则影响 GPU 帧时间{scale}，而 GPU 用掉了 {m.FrameMsMedian:0.0} ms 帧时间里的 {m.GpuMsMedian:0.0} ms。这些具体条目是否属于那部分开销，并未实测。");

            if (axes.Contains(PerfAxis.Stutter))
            {
                // "this one allocates every frame" is true of the allocation rules and of nothing else on this axis.
                // It was being said about every finding carrying Stutter, which includes RUN.FPS003 — a MEASURED
                // spike, where it asserted both a cause (allocation) and a mechanism (a collection pause) that the
                // measurement did not establish. A 148 ms hitch at 27x the average is just as easily an asset load
                // or a shader compile, and the same paragraph three lines up is the rule this broke: an observation
                // gets described, not explained by whatever else shares its axis.
                bool aboutAllocation = f.RuleId != null &&
                    (f.RuleId.StartsWith("RUN.GC", StringComparison.Ordinal) ||
                     f.RuleId.StartsWith("PERF.GC", StringComparison.Ordinal));

                return aboutAllocation
                    ? L.Tr($"Hitches are felt even when the average looks fine{scale}, and this one allocates every frame — that is what periodically forces a collection pause.",
                           $"即使平均帧时间好看，卡顿依然会被感知到{scale}；这条每帧都在分配，正是周期性触发 GC 暂停的来源。")
                    : L.Tr($"Hitches are felt even when the average looks fine{scale}. The spike is measured; what caused it is not — so this one is worth a look before it is worth a change.",
                           $"即使平均帧时间好看，卡顿依然会被感知到{scale}。尖峰是实测的，成因不是——所以这条先值得看一眼，再谈改。");
            }

            // A rule carrying BOTH asset axes is described by the one its numbers are actually in.
            //
            // ASSET.DUP001 is mapped to {BuildSize, Memory} and this branch used to win on order alone, so the card
            // read "Reduces how much the game holds in memory" directly above "~0.6 MB build size" — two sentences
            // about two different axes, one of which (its memory estimate) is exactly zero. Deciding by which side
            // the estimate is on costs nothing and cannot disagree with the figure printed under it.
            bool memIsWhereTheMoneyIs = !(axes.Contains(PerfAxis.BuildSize) && ruleBuild > 0 && ruleMem == 0);

            if (axes.Contains(PerfAxis.Memory) && memIsWhereTheMoneyIs)
            {
                bool climbing = m.MemoryGrowthBytes > 64L * 1024 * 1024;
                double mb = m.MemoryGrowthBytes / (1024.0 * 1024.0);

                if (climbing && measured)
                    return L.Tr($"Measured: memory climbed {mb:0} MB over the sample{scale} — on a device that ends as a crash, not a slowdown.",
                                $"实测：采样期间内存增长了 {mb:0} MB{scale}——在真机上这不是变慢，是崩溃。");
                if (climbing)
                    // Same axis as the observed climb, but nothing here says this is the cause of it.
                    return L.Tr($"Memory climbed {mb:0} MB during the sample. This isn't shown to be the cause, but it's memory this project holds that it doesn't have to{scale}.",
                                $"采样期间内存增长了 {mb:0} MB。没有证据表明这条就是原因，但它确实是本工程可以不占的内存{scale}。");

                return L.Tr($"Reduces how much the game holds in memory{scale}. Editor figures include the editor itself, so judge this by the change, not the absolute number.",
                            $"降低游戏常驻内存{scale}。编辑器数字含编辑器自身，请看变化量而非绝对值。");
            }

            if (axes.Contains(PerfAxis.BuildSize))
                return L.Tr($"Shrinks what ships{scale}. Independent of frame rate — do it when download size matters to you.",
                            $"减小最终发布体积{scale}。与帧率无关——当你在意下载体积时再做。");

            return L.Tr($"Ranked from the current measurement{scale}.", $"依据当前实测排序{scale}。");
        }

        /// <param name="mem">The RULE's total memory estimate, not the representative finding's. See Rank.</param>
        /// <param name="build">The RULE's total build-size estimate.</param>
        static string Expected(Finding f, IReadOnlyList<PerfAxis> axes, long mem, long build)
        {
            if (mem > 0 || build > 0)
            {
                // The unit word carries the "about" already ("内存约 X"), so the wrapper must not add a second one.
                // It did: "约 包体约 2.3 MB（估算）" — two 约 in five characters, on the line whose whole job is to
                // state a figure credibly.
                var parts = new List<string>(2);
                if (mem > 0) parts.Add(L.Tr($"~{Mb(mem)} MB memory", $"内存约 {Mb(mem)} MB"));
                if (build > 0) parts.Add(L.Tr($"~{Mb(build)} MB build size", $"包体约 {Mb(build)} MB"));
                string joined = string.Join(L.Tr(" · ", "、"), parts);
                return f.SavingsAreCeiling
                    ? L.Tr($"Up to {joined} — a ceiling, the real figure depends on the scene.", $"最多 {joined}——这是上限，实际值取决于场景。")
                    : L.Tr($"About {joined} (est.).", $"{joined}（估算）。");
            }

            // No honest number available. Say that, rather than inventing one.
            if (axes.Contains(PerfAxis.CpuFrameTime) || axes.Contains(PerfAxis.Stutter))
                return L.Tr("No reliable estimate — measure again after the change and compare.",
                            "没有可靠的预估值——改完再测一次、对比前后。");
            return L.Tr("No reliable estimate for this one.", "这条没有可靠的预估值。");
        }

        static string Risk(Finding f)
        {
            string caution = OptimizePlan.CautionFor(f.RuleId);
            if (!string.IsNullOrEmpty(caution)) return caution;
            if (f.CanAutoFix || f.WasAutoFixable)
                return L.Tr("Low — changes import settings only.", "低——只改导入设置。");
            if (f.AiFixable)
                return L.Tr("Edits your code. You review the diff before anything is written.",
                            "会改你的代码。写入前你要先逐条看 diff 确认。");
            return L.Tr("Read the finding before acting — this one isn't a one-click change.",
                        "动手前先读一下该 finding——这条不是一键改动。");
        }

        static string Undo(Finding f)
        {
            if (OptimizePlan.IsIrreversible(f.RuleId))
                return L.Tr("NOT undoable — deletes files and rewrites references. Commit to version control first.",
                            "不可撤销——会删文件并重写引用。请先提交版本控制。");
            if (f.CanAutoFix || f.WasAutoFixable)
                // Not Edit ▸ Undo: every IFix mutates an AssetImporter and calls SaveAndReimport, and nothing in the
                // package registers that with Unity's undo stack. Recoverable, though — which is what to say.
                return L.Tr("Revert from version control, or change the setting back in the Inspector — not Edit ▸ Undo.",
                            "从版本控制恢复，或在 Inspector 里把设置改回来——用不了 Edit ▸ Undo。");
            return L.Tr("Depends on what you change; commit first if unsure.", "取决于你改了什么；拿不准就先提交版本控制。");
        }

        static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#");
    }
}
