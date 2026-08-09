using System;

namespace PerfLint.Core
{
    /// <summary>
    /// What the user is trying to achieve, in the only terms this tool can honestly hold it to.
    ///
    /// Deliberately narrow: a target frame rate the user picked, and nothing else. There is no memory or build-size
    /// budget, and that is a finding rather than an omission — measured in the editor, Play Mode memory includes the
    /// editor process itself (~4.4 GB on a scene that ships far smaller), so an absolute "under 1 GB" could only ever
    /// be checked against a number that does not mean what it says. Frame budget, by contrast, is arithmetic on the
    /// user's own choice — 60 FPS is 16.67 ms, nothing invented.
    ///
    /// A target PLATFORM used to sit next to it, and was removed (2026-07-31) rather than fixed. It had exactly one
    /// behavioural property, IsMobileClass, and nothing in the codebase ever read it: no threshold, no ranking, no
    /// piece of advice changed with it. What it did do was print "Android &amp; iOS mid-tier" above numbers sampled in
    /// the editor on a desktop GPU, which invites reading them as that device's — the one claim this whole design is
    /// built to refuse. Tim, on seeing it: "当前测试的是 editor 中的性能和各个平台/机型有很大差距". A selector that
    /// changes nothing and implies something false is worse than no selector.
    ///
    /// See docs/goal-benchmark-loop-plan.md §3.2 for why absolute device claims are off the table until a real
    /// player build measures them.
    /// </summary>
    public readonly struct PerfGoal
    {
        /// <summary>Frames per second the user is aiming for. Their choice, not our recommendation.</summary>
        public readonly int TargetFps;

        public PerfGoal(int targetFps)
        {
            TargetFps = targetFps > 0 ? targetFps : 60;
        }

        public static PerfGoal Default => new PerfGoal(60);

        /// <summary>Milliseconds available per frame at the target rate. Pure arithmetic on the user's number.</summary>
        public double FrameBudgetMs => 1000.0 / TargetFps;
    }

    /// <summary>How the measured frame time stands against the goal.</summary>
    public enum FrameStatus
    {
        /// <summary>No usable measurement — nothing may be concluded about the frame budget.</summary>
        Unknown,
        /// <summary>
        /// Frame rate was held down by VSync or a target frame rate, so the reading is the cap, not the machine.
        /// Neither "meeting" nor "missing" the goal can be concluded from it.
        /// </summary>
        Capped,
        /// <summary>
        /// Recorded with Deep Profile on, so the frame time is mostly the profiler's own per-call cost. Kept apart
        /// from <see cref="Capped"/> because the fix is the opposite one — a capped reading needs VSync turned off,
        /// this one is a deliberate trade the user made to find out WHICH methods cost, at the price of knowing what
        /// they cost. Both are "no answer to the budget question", and neither may be shown as one.
        /// </summary>
        Inflated,
        Meeting,
        /// <summary>Sitting on the line — within measurement noise of the budget, either side of it.</summary>
        Marginal,
        Failing
    }

    /// <summary>Which side of the frame the time is going. Unknown is a real answer and must stay one.</summary>
    public enum Bottleneck
    {
        Unknown,
        Cpu,
        Gpu,
        Balanced
    }

    /// <summary>
    /// The measured facts a ranking is allowed to reason from, as plain numbers.
    ///
    /// Kept free of the sampling types so the ranking stays a pure Core function: the Runtime layer knows how to
    /// produce this, Core only reads it. <see cref="GpuReliable"/> is carried explicitly because a GPU reading that
    /// failed its integrity check must not silently become "the GPU is idle" — see RuntimeAnalyzer's GPU001 guard.
    /// </summary>
    public readonly struct PerfMeasurement
    {
        public readonly bool HasData;
        public readonly double FrameMsMedian;
        public readonly double GpuMsMedian;
        public readonly bool GpuReliable;
        public readonly double GcBytesPerFrame;
        public readonly double MemoryPeakBytes;
        /// <summary>Net growth in used memory across the sample (bytes). Positive means it kept climbing.</summary>
        public readonly double MemoryGrowthBytes;
        public readonly double DrawCalls;
        /// <summary>
        /// VSync or a target frame rate was limiting the frame rate while this was recorded, so the frame time is
        /// the cap rather than what the machine can do. Measured proof this matters: the same scene read 16.68 ms
        /// with VSync on and 5.4 ms with it off — a "you're missing 60 FPS" verdict off the first number would be
        /// flatly wrong about a machine doing 185 FPS.
        /// </summary>
        public readonly bool FrameRateCapped;

        /// <summary>
        /// Deep Profile was on while this was recorded. Every wall-clock figure is then several times too high —
        /// it is the profiler's per-call bookkeeping, not the game. Carried explicitly for the same reason
        /// <see cref="FrameRateCapped"/> is: the moment Deep Profile became a legitimate way to measure (it is the
        /// only way to get per-method markers), a panel that judged its frame time against a budget would tell a
        /// user running comfortably that they are missing 60 FPS by a factor of five.
        /// </summary>
        public readonly bool TimingsInflated;

        /// <summary>
        /// The frame time here is the WHOLE EDITOR FRAME, not the game's — so it cannot be compared to a frame budget.
        ///
        /// In the Editor the main thread runs EditorLoop and PlayerLoop in the same frame, and EditorLoop is the editor
        /// drawing its own windows: Inspector, Project, Scene view. None of that exists in a build. Measured on the
        /// reference project: 3.775 ms whole frame vs 2.084 ms game — 1.69 ms, 81% on top, of work no player ever runs.
        /// Judged against a 240 FPS budget those two numbers disagree about everything that follows: 90.6% of budget
        /// ("Marginal", CPU-bound, ranking led by shader/script rules) versus 50.0% ("Meeting", ranking led by build
        /// size). The entire verdict sat on 0.6% of headroom that belonged to the editor's own window drawing.
        ///
        /// Carried explicitly for the same reason <see cref="FrameRateCapped"/> and <see cref="TimingsInflated"/> are:
        /// the figure is real and worth showing, it just cannot answer this particular question. Defaults to false so
        /// every existing caller keeps its meaning — only a measurement that had to fall back to the editor-wide
        /// figure sets it.
        /// </summary>
        public readonly bool FrameTimeIsEditorWide;

        /// <summary>
        /// Tolerance around the budget, as a fraction. Editor frame time carries roughly 0.5% run-to-run noise
        /// (measured, see docs/goal-benchmark-loop-plan.md §4), so calling a 0.09% overshoot a failure is false
        /// precision — and reads absurdly once the display rounds it to "0.0 ms over".
        /// </summary>
        public const double BudgetTolerance = 0.05;

        public PerfMeasurement(double frameMsMedian, double gpuMsMedian, bool gpuReliable,
            double gcBytesPerFrame, double memoryPeakBytes, double memoryGrowthBytes, double drawCalls,
            bool frameRateCapped = false, bool timingsInflated = false, bool frameTimeIsEditorWide = false)
        {
            HasData = frameMsMedian > 0;
            FrameMsMedian = frameMsMedian;
            GpuMsMedian = gpuMsMedian;
            GpuReliable = gpuReliable;
            GcBytesPerFrame = gcBytesPerFrame;
            MemoryPeakBytes = memoryPeakBytes;
            MemoryGrowthBytes = memoryGrowthBytes;
            DrawCalls = drawCalls;
            FrameRateCapped = frameRateCapped;
            TimingsInflated = timingsInflated;
            FrameTimeIsEditorWide = frameTimeIsEditorWide;
        }

        public static PerfMeasurement None => default;

        public FrameStatus StatusAgainst(PerfGoal goal)
        {
            if (!HasData) return FrameStatus.Unknown;
            if (TimingsInflated) return FrameStatus.Inflated;
            if (FrameRateCapped) return FrameStatus.Capped;
            // An editor-wide frame time answers a different question than the one being asked. Unknown, not a guess in
            // either direction: judging a project by work no build performs is the opposite of the job.
            if (FrameTimeIsEditorWide) return FrameStatus.Unknown;

            double ratio = FrameMsMedian / goal.FrameBudgetMs;
            if (ratio > 1 + BudgetTolerance) return FrameStatus.Failing;
            if (ratio > 0.90) return FrameStatus.Marginal; // on the line, either side of it
            return FrameStatus.Meeting;
        }

        /// <summary>True when the frame budget question has an answer at all. Capped, inflated, editor-wide and no-data all mean it does not.</summary>
        public bool CanJudgeFrameBudget => HasData && !FrameRateCapped && !TimingsInflated && !FrameTimeIsEditorWide;

        /// <summary>
        /// Which side is spending the frame. Returns <see cref="Bottleneck.Unknown"/> whenever the GPU reading can't
        /// be trusted — guessing here is what sends someone optimizing the wrong half of their frame.
        /// </summary>
        public Bottleneck Side
        {
            get
            {
                if (!HasData || !GpuReliable || GpuMsMedian <= 0) return Bottleneck.Unknown;
                // Deep Profile inflates the CPU side only, so the CPU-vs-GPU ratio it produces is manufactured: a
                // GPU-bound scene reads as heavily CPU-bound purely from the instrumentation. That is the exact
                // failure this property exists to prevent, so it refuses rather than answering.
                if (TimingsInflated) return Bottleneck.Unknown;
                // A frame rate cap does the same thing by a different route: the wait for the next presentation
                // interval lands on the CPU frame time and nowhere else, so a capped frame reads as overwhelmingly
                // CPU-bound no matter what the GPU did. Measured on museum's hnmz-enterprise, which sets
                // Application.targetFrameRate = 60 in its own boot code: 16.6 ms of CPU frame against 0.2 ms of GPU
                // — a 96:1 ratio that is the cap, not the game. StatusAgainst already refuses a capped reading, and
                // this is the same refusal for the same reason; the two were only ever consistent by accident,
                // because no capped measurement had yet carried a GPU reading that passed its integrity check.
                if (FrameRateCapped) return Bottleneck.Unknown;
                // A third route to the same false CPU verdict: both thresholds below are multiples of the frame time,
                // so an editor-wide figure inflates them and biases the answer one way. On the reference project the
                // CPU threshold is 2.26 ms against the whole frame but 1.25 ms against the game's, and every GPU
                // reading between those two gets called "CPU-bound" purely because the editor was drawing its windows.
                if (FrameTimeIsEditorWide) return Bottleneck.Unknown;
                if (GpuMsMedian > FrameMsMedian * 1.15) return Bottleneck.Gpu;
                if (GpuMsMedian < FrameMsMedian * 0.6) return Bottleneck.Cpu;
                return Bottleneck.Balanced;
            }
        }
    }
}
