using System;
using System.Collections.Generic;
using PerfLint.L10n;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// WHAT to measure: one repeatable Play Mode measurement recipe.
    ///
    /// The whole point of a spec is that two measurements taken with the SAME spec are allowed to be
    /// subtracted from each other. Anything that can move a number between two runs either lives here
    /// (and is therefore pinned) or lives in <see cref="BenchmarkFingerprint"/> (and is therefore
    /// checked before a delta is ever shown). Nothing that affects the result may live in neither.
    ///
    /// Serialized with JsonUtility (public fields only), so it survives the two domain reloads a
    /// Play Mode round-trip costs us.
    /// </summary>
    [Serializable]
    public sealed class BenchmarkSpec
    {
        /// <summary>
        /// Identity of the scene this measurement is ABOUT — the one whose numbers the run reports, and the key every
        /// comparison, baseline and drift sample is filed under.
        ///
        /// On a project with a boot sequence this is <see cref="targetSceneGuid"/>, not the scene the editor had open:
        /// what gets measured is where the game ended up, so that is what the run has to be labelled as. It equals the
        /// start scene only when nothing is waited for.
        ///
        /// GUID is the durable key (a path rename must not silently invalidate history); path is kept for display.
        /// </summary>
        public string sceneGuid;
        public string scenePath;

        /// <summary>
        /// Scene to open before entering Play Mode. Empty means "start from whatever the editor already has open",
        /// which is the behaviour every measurement had before scene plans existed.
        ///
        /// Separate from <see cref="sceneGuid"/> because a game that boots through Init cannot be entered anywhere
        /// else: opening the level directly gives you a scene with no managers, no singletons and no save data, which
        /// either throws on the first frame or measures a level that never finished initialising.
        /// </summary>
        public string startSceneGuid;
        public string startScenePath;

        /// <summary>
        /// Scene sampling waits for before the warmup clock starts. Empty means "sample wherever Play Mode lands".
        ///
        /// The wait is the whole mechanism: the game is left to reach the scene by its own route — automatically, or
        /// with the user playing to it — and only once that scene is loaded does the measurement begin. Which is also
        /// why the wait has no meaningful upper bound in the way the other phases do; see the runner's AwaitScene.
        /// </summary>
        public string targetSceneGuid;
        public string targetScenePath;

        /// <summary>Whether this run has to wait for a scene the game loads by itself.</summary>
        public bool WaitsForScene => !string.IsNullOrEmpty(targetSceneGuid);

        /// <summary>
        /// Play Mode seconds discarded before sampling starts. Covers first-frame shader compilation, texture
        /// streaming settle, JIT and Awake/Start bursts — all of which are real costs, but one-off ones that would
        /// otherwise dominate a short window and swamp the steady-state signal we are trying to compare.
        /// </summary>
        public float warmupSeconds = 5f;

        /// <summary>Length of the sampling window, in Play Mode seconds.</summary>
        public float sampleSeconds = 20f;

        /// <summary>How many times to repeat the whole run. ≥2 is what makes a run-to-run noise band possible at all.</summary>
        public int repetitions = 3;

        /// <summary>
        /// Reserved for the scripted-camera flythrough (Stage 2). False = "enter the scene and hold still":
        /// no input, no camera motion, so the only variance left is the engine's own. That is deliberately the
        /// FLOOR of achievable noise — if the numbers are unstable even here, they will be worse in any mode
        /// where a human is driving, and the whole before/after premise is dead.
        /// </summary>
        public bool driveCamera = false;

        /// <summary>
        /// Also analyse the final run and store it as the panel's runtime session.
        ///
        /// This is what makes the runner usable as the "measure my scene properly" button rather than only as a
        /// study tool: it turns VSync off, waits out the warmup, samples a fixed window and restores everything —
        /// all the setup a user otherwise has to remember, and gets wrong (a VSync-capped sample reads as "you
        /// can't hit 60 FPS" on a machine doing 185).
        /// </summary>
        public bool saveRuntimeSession = false;

        public BenchmarkSpec Clone() => (BenchmarkSpec)MemberwiseClone();

        public string ToJson() => JsonUtility.ToJson(this);

        public static BenchmarkSpec FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<BenchmarkSpec>(json); }
            catch { return null; }
        }
    }

    /// <summary>
    /// The CONDITIONS a measurement was taken under. Recorded per run, compared before any delta is displayed.
    ///
    /// This type exists because the single most damaging thing this feature could do is subtract two numbers that
    /// were never comparable and present the difference as an improvement we caused. Every field here is something
    /// that has been observed to move frame time or memory on its own; a mismatch in any of them means we refuse
    /// to show a delta and say which field differed (see <see cref="Comparable"/>).
    /// </summary>
    [Serializable]
    public sealed class BenchmarkFingerprint
    {
        public string sceneGuid;
        public float warmupSeconds;
        public float sampleSeconds;
        public bool driveCamera;

        public string unityVersion;
        public string buildTarget;
        /// <summary>
        /// The active rendering API (for example Direct3D11 or Direct3D12). Switching APIs changes CPU/GPU
        /// scheduling, shader variants and driver behaviour enough that the resulting timings cannot be subtracted.
        /// Unlike additive fingerprint fields, an unrecorded value is not accepted against a recorded one: an older
        /// baseline whose API is unknown must be replaced rather than silently compared under a possibly different
        /// renderer.
        /// </summary>
        public string graphicsApi;
        public string renderPipeline;   // "Built-in" / the SRP asset type name — swapping pipelines changes everything
        public int qualityLevel;
        public string qualityName;

        /// <summary>Deep Profile massively inflates main-thread time. A run with it on is not comparable to one without — and is not a usable measurement in the first place.</summary>
        public bool deepProfile;

        /// <summary>VSync clamps frame time to the display refresh interval, which flattens exactly the improvement we are trying to detect.</summary>
        public int vSyncCount;
        public int targetFrameRate;

        /// <summary>Game View backbuffer size. Fill rate scales with it, so a resized Game View between runs is a confound, not a result.</summary>
        public int screenWidth;
        public int screenHeight;

        /// <summary>
        /// How many Game views were open. A second one renders the scene a second time, which costs frame time without
        /// changing the backbuffer size that <see cref="screenWidth"/> records — so it is invisible to every other
        /// field here. Observed: a project that measured 188 FPS read 174 FPS with a second Game view docked, and that
        /// 10% was banked as permanent "machine drift" rather than being refused as a changed condition.
        ///
        /// 0 means "not recorded" (a measurement taken before this field existed) and the check is skipped, so an
        /// older baseline is not invalidated by the addition.
        /// </summary>
        public int gameViewCount;

        /// <summary>
        /// Returns whether two runs may legitimately be subtracted. On false, <paramref name="reason"/> names the
        /// first offending field in plain language — the UI shows that instead of a number, and an agent reading the
        /// JSON gets the same honest refusal.
        /// </summary>
        /// <summary>
        /// The way out of a refusal, in the user's terms. Null when the mismatch has no single obvious remedy.
        ///
        /// It exists because naming a condition is not the same as giving somebody a way forward, and the panel was
        /// doing only the first: "Deep Profile was on for only one of the runs" is a complete diagnosis sitting above
        /// a button that offers to throw the baseline away and start again, when the actual fix is one toggle. Same
        /// rule as the executability audit for one-click actions — if we know exactly what is blocking, say what to
        /// do about it.
        /// </summary>
        public static string RemedyFor(BenchmarkFingerprint a, BenchmarkFingerprint b)
        {
            if (a == null || b == null) return null;

            if (a.deepProfile != b.deepProfile)
            {
                bool baselineHadIt = a.deepProfile;
                return baselineHadIt
                    ? "The baseline was recorded with Deep Profile on. Turn it back on (it needs a script reload) and measure again — or replace the baseline to carry on without it."
                    : "The baseline was recorded with Deep Profile off. Turn it off (it needs a script reload) and measure again — or replace the baseline to carry on with it on.";
            }

            if (a.vSyncCount != b.vSyncCount)
                return "Set VSync back to what the baseline used in Quality Settings, then measure again.";

            if (!StrEq(a.graphicsApi, b.graphicsApi))
                return string.IsNullOrEmpty(a.graphicsApi)
                    ? "The baseline did not record its graphics API. Replace it before comparing again."
                    : $"Restart Unity with {a.graphicsApi} (the baseline's graphics API), then measure again — or replace the baseline.";

            if (a.screenWidth != b.screenWidth || a.screenHeight != b.screenHeight)
                return $"Resize the Game view back to {a.screenWidth}x{a.screenHeight}, then measure again.";

            if (a.gameViewCount > 0 && b.gameViewCount > 0 && a.gameViewCount != b.gameViewCount)
                return a.gameViewCount < b.gameViewCount
                    ? "Close the extra Game view — each one renders the scene again — then measure again."
                    : "The baseline was taken with more Game views open. Reopen them, or replace the baseline.";

            if (!StrEq(a.sceneGuid, b.sceneGuid))
                return "Open the scene the baseline was taken in, then measure again.";

            if (a.qualityLevel != b.qualityLevel)
                return $"Switch the quality level back to \"{a.qualityName}\", then measure again.";

            return null;
        }

        public static bool Comparable(BenchmarkFingerprint a, BenchmarkFingerprint b, out string reason)
        {
            reason = null;
            if (a == null || b == null) { reason = "one of the runs has no fingerprint"; return false; }

            if (!StrEq(a.sceneGuid, b.sceneGuid)) return No(out reason, "different scene");
            if (!Approx(a.warmupSeconds, b.warmupSeconds)) return No(out reason, $"different warmup ({a.warmupSeconds:0.#}s vs {b.warmupSeconds:0.#}s)");
            if (!Approx(a.sampleSeconds, b.sampleSeconds)) return No(out reason, $"different sampling window ({a.sampleSeconds:0.#}s vs {b.sampleSeconds:0.#}s)");
            if (a.driveCamera != b.driveCamera) return No(out reason, "different camera mode");
            if (!StrEq(a.unityVersion, b.unityVersion)) return No(out reason, $"different Unity version ({a.unityVersion} vs {b.unityVersion})");
            if (!StrEq(a.buildTarget, b.buildTarget)) return No(out reason, $"different build target ({a.buildTarget} vs {b.buildTarget})");
            if (!StrEq(a.graphicsApi, b.graphicsApi))
                return No(out reason, $"different graphics API ({KnownOrUnknown(a.graphicsApi)} vs {KnownOrUnknown(b.graphicsApi)})");
            if (!StrEq(a.renderPipeline, b.renderPipeline)) return No(out reason, $"different render pipeline ({a.renderPipeline} vs {b.renderPipeline})");
            if (a.qualityLevel != b.qualityLevel) return No(out reason, $"different quality level ({a.qualityName} vs {b.qualityName})");
            if (a.deepProfile != b.deepProfile) return No(out reason, "Deep Profile was on for only one of the runs");
            if (a.vSyncCount != b.vSyncCount) return No(out reason, $"different VSync setting ({a.vSyncCount} vs {b.vSyncCount})");
            if (a.targetFrameRate != b.targetFrameRate) return No(out reason, $"different target frame rate ({a.targetFrameRate} vs {b.targetFrameRate})");
            if (a.screenWidth != b.screenWidth || a.screenHeight != b.screenHeight)
                return No(out reason, $"different Game View size ({a.screenWidth}x{a.screenHeight} vs {b.screenWidth}x{b.screenHeight})");

            // Skipped when either side predates the field: an older baseline should not become uncomparable just
            // because we learned to record one more condition.
            if (a.gameViewCount > 0 && b.gameViewCount > 0 && a.gameViewCount != b.gameViewCount)
                return No(out reason, $"a different number of Game views was open ({a.gameViewCount} vs {b.gameViewCount}) — each one renders the scene again");

            return true;
        }

        static bool No(out string reason, string why) { reason = why; return false; }
        static bool StrEq(string x, string y) => string.Equals(x ?? "", y ?? "", StringComparison.Ordinal);
        static string KnownOrUnknown(string value) => string.IsNullOrEmpty(value) ? "not recorded" : value;
        static bool Approx(float x, float y) => Mathf.Abs(x - y) < 0.01f;

        /// <summary>
        /// Conditions that make a run unusable as a measurement regardless of what it is compared against. Empty = usable.
        ///
        /// Deep Profile used to be in here and deliberately is not any more. It ruins wall-clock time, which is why
        /// <see cref="TimingsInflatedWarning"/> exists — but it is also the ONLY way to get per-method markers, and
        /// which call paths ran, and in how many frames, is not affected by instrumentation at all. Refusing the whole
        /// measurement threw away the half that was valid along with the half that wasn't.
        /// </summary>
        public string UsabilityWarning()
        {
            if (vSyncCount > 0)
                return "VSync was on — frame time is clamped to the display refresh interval, which hides improvements until they cross a whole refresh step.";
            return null;
        }

        /// <summary>
        /// Conditions under which the wall-clock figures are real numbers of the wrong thing. Null when timings are
        /// trustworthy.
        ///
        /// The split matters because the two halves of a Deep Profile measurement have opposite standing. Times are
        /// inflated several-fold by the profiler's per-call bookkeeping, and a code change alters the call count, so
        /// even the DELTA is distorted rather than merely scaled. Presence is untouched: a marker either ran in a
        /// frame or it did not, and no amount of instrumentation changes that answer — so the sampled-frame hit rate
        /// is the one figure that is worth exactly as much under Deep Profile as without it.
        /// </summary>
        public string TimingsInflatedWarning()
        {
            if (deepProfile)
                return "Deep Profile was on, so the frames here are several times longer than your game's. Every millisecond includes the profiler's own per-call cost, and every figure measured PER FRAME inflates with the frame — allocation per frame reads about three times its real value. Both are left out rather than caveated. What this measurement can tell you is which call paths ran, in how many frames, and what each allocated.";
            return null;
        }

        /// <summary>Whether wall-clock figures from this run are inflated by instrumentation. See <see cref="TimingsInflatedWarning"/>.</summary>
        public bool TimingsInflated => deepProfile;
    }

    /// <summary>Aggregated statistics for one counter within one run. Mirrors the fields of <see cref="MetricStats"/> that survive persistence.</summary>
    [Serializable]
    public sealed class BenchmarkMetric
    {
        public string key;
        public bool hasData;
        public int sampleCount;
        public double avg;
        public double min;
        public double max;
        /// <summary>P50. The headline for frame time: a single hitch inflates avg and p95 but barely moves the median, so it is the honest "how does it usually feel" number.</summary>
        public double median;
        public double p95;

        /// <summary>
        /// Averages of the first and second half of the window. Their difference is the only honest way to say
        /// "this kept growing": max−min is the peak-to-trough SWING, which a normal GC sawtooth makes large even
        /// when nothing leaked at all. RUN.MEM001 already reports growth this way — persisting the halves is what
        /// lets everything downstream agree with it instead of quoting a number twice as big.
        /// </summary>
        public double firstHalfAvg;
        public double secondHalfAvg;

        /// <summary>Net trend across the window. Positive = climbing.</summary>
        public double TrendDelta => secondHalfAvg - firstHalfAvg;

        public static BenchmarkMetric From(string key, MetricStats s)
        {
            if (s == null || !s.HasData) return new BenchmarkMetric { key = key, hasData = false };
            return new BenchmarkMetric
            {
                key = key,
                hasData = true,
                sampleCount = s.SampleCount,
                avg = s.Avg,
                min = s.Min,
                max = s.Max,
                median = s.Median,
                p95 = s.P95,
                firstHalfAvg = s.FirstHalfAvg,
                secondHalfAvg = s.SecondHalfAvg
            };
        }

        /// <summary>Same as <see cref="From"/> but rescaled — used to store nanosecond counters as milliseconds, which is the unit every consumer actually wants.</summary>
        public static BenchmarkMetric FromScaled(string key, MetricStats s, double factor)
        {
            var m = From(key, s);
            if (!m.hasData) return m;
            m.avg *= factor; m.min *= factor; m.max *= factor; m.median *= factor; m.p95 *= factor;
            m.firstHalfAvg *= factor; m.secondHalfAvg *= factor;
            return m;
        }
    }

    /// <summary>
    /// One CPU hotspot as it survives persistence — the marker, what it cost per frame, and how many of the
    /// representative frames it was actually running in.
    ///
    /// Stored per run because a hotspot is the one figure in this whole loop that describes the USER'S CODE rather
    /// than this machine. Wall-clock frame time is a property of the CPU it was measured on and drifts ±13% between
    /// identical runs; "this call path was on the main thread in 22 of 24 frames, costing 3.1 ms of each" is a
    /// statement about the code, and it is what a before/after can compare without the machine getting a vote.
    /// </summary>
    [Serializable]
    public sealed class BenchmarkHotspot
    {
        public string marker;
        public string scriptPath;
        public double selfMsPerFrame;
        public double peakMsPerFrame;
        public double sharePercent;
        /// <summary>Representative frames the marker did main-thread work in, and how many were examined. See <see cref="Hotspot.HitFrames"/>.</summary>
        public int hitFrames;
        public int sampledFrames;

        public static BenchmarkHotspot From(Hotspot h) => h == null ? null : new BenchmarkHotspot
        {
            marker = h.Marker,
            scriptPath = h.ScriptPath,
            selfMsPerFrame = h.SelfMsPerFrame,
            peakMsPerFrame = h.PeakMsPerFrame,
            sharePercent = h.SharePercent,
            hitFrames = h.HitFrames,
            sampledFrames = h.SampledFrames
        };

        public Hotspot ToHotspot() =>
            new Hotspot(marker, selfMsPerFrame, peakMsPerFrame, sharePercent, scriptPath, hitFrames, sampledFrames);
    }

    /// <summary>
    /// What a measured figure is a property OF.
    ///
    /// The whole honesty story of the before/after rests on this distinction, and it used to be a sentence repeated
    /// on every affected row — three copies of "this figure does not carry to a device" in one table. As an enum it
    /// can become a single group heading instead, which is both less to read and one place to be right.
    /// </summary>
    public enum MetricScope
    {
        /// <summary>
        /// A property of this machine right now: wall-clock time, or a rate normalised by it. A reduction is real and
        /// worth reporting; the absolute number says nothing about what a device will do.
        /// </summary>
        Machine,

        /// <summary>
        /// The content's figure plus the editor process it was measured inside. A genuine reduction does carry to a
        /// device, but the editor's own share moves on its own schedule — measured: the managed heap read 6.0-6.3%
        /// lower across three comparisons in which nothing was edited at all.
        /// </summary>
        ContentPlusEditor,

        /// <summary>A property of the content and the code. What the editor measures here is what ships.</summary>
        Content
    }

    /// <summary>Stable metric keys. Kept as constants so the study, the store, the UI and the JSON contract cannot drift apart.</summary>
    public static class BenchmarkMetricKeys
    {
        public const string FrameTimeMs = "frameTimeMs";
        public const string GpuFrameTimeMs = "gpuFrameTimeMs";
        public const string GcPerFrameBytes = "gcPerFrameBytes";

        /// <summary>
        /// Allocation inside PlayerLoop per frame — the GAME's, where <see cref="GcPerFrameBytes"/> is the whole
        /// editor process.
        ///
        /// A separate key rather than a redefinition of the old one, because a baseline recorded before this existed
        /// holds process figures, and silently comparing those against game figures would manufacture an enormous
        /// improvement out of a change in what was measured. Runs lacking it simply omit the row; once both sides
        /// have it, it is the one that leads.
        ///
        /// Why it had to exist: the counter is dominated by the editor. Inside one Play Mode session on urp3dsample
        /// it read 55 KB, 308 KB and 456 KB per frame — running an eval moved it eightfold — while PlayerLoop's own
        /// subtree held a flat 2778 B. A user who fixed real allocating code saw the counter move 0.4%, well inside
        /// the noise band, because the thing they fixed was 5% of what was being measured. A before/after metric that
        /// cannot see the change it exists to verify is not a metric.
        /// </summary>
        public const string GameGcPerFrameBytes = "gameGcPerFrameBytes";

        /// <summary>
        /// Main-thread milliseconds inside PlayerLoop — the GAME's frame time, where <see cref="FrameTimeMs"/> is the
        /// whole editor frame. A separate key for the same reason as the GC pair: a baseline recorded before this
        /// existed holds whole-frame figures, and comparing those against game-side ones would manufacture a large
        /// improvement out of a change in what was measured.
        ///
        /// Measured on urp3dsample: 8.42 ms whole frame = 5.41 game + 2.75 editor. The editor's share is roughly
        /// constant (~3 ms), so the LIGHTER the game the larger the fraction of a verdict that belongs to work no
        /// build will ever do.
        ///
        /// Still Machine-scoped: it is milliseconds on this CPU, and a different machine gives a different number.
        /// Excluding the editor does not make it a device figure.
        /// </summary>
        public const string GameFrameTimeMs = "gameFrameTimeMs";

        /// <summary>
        /// DERIVED, not sampled: GC bytes per frame × frames per second. Never present in a stored run — computed on
        /// demand by <see cref="BenchmarkComparison"/>.
        ///
        /// It exists because "GC per frame" silently depends on the frame rate, and comparing two measurements taken
        /// at different frame rates by that figure alone is misleading. Measured proof: the same project allocated
        /// 73.7 KB/frame with VSync on and 8.2 KB/frame with it off — a 9× "improvement" from a display setting,
        /// because the same per-second allocation was spread over 3× the frames. Reporting both figures is the only
        /// honest option, because which one is invariant depends on what allocates (per-Update work is per-frame; a
        /// timer or coroutine is per-second) and the measurement cannot tell us which.
        /// </summary>
        public const string GcPerSecondBytes = "gcPerSecondBytes";

        /// <summary>
        /// DERIVED like <see cref="GcPerSecondBytes"/>, but from a different STATISTIC of a stored counter rather than
        /// from two counters: the 95th percentile of frame time instead of its median.
        ///
        /// Worth its own row because it is the number a player actually complains about. The median says how it
        /// usually feels; p95 says how bad the worst twentieth of frames are, which is what "it stutters" means. A
        /// change can move one and not the other, and hiding that behind a single figure loses the distinction.
        /// </summary>
        public const string FrameTimeP95Ms = "frameTimeP95Ms";

        /// <summary>
        /// Prefix for a per-hotspot key ("hotspot:BehaviourUpdate").
        ///
        /// Hotspots get keys of the same shape as counters so their self-time goes through the SAME verdict rules and
        /// the SAME drift calibration — a null comparison records how far each hotspot moved with nothing changed, in
        /// exactly the way it already does for frame time. The sketch's claim is that hotspot self-time barely drifts;
        /// giving it a key is how that becomes measured on the user's machine rather than assumed on ours.
        /// </summary>
        public const string HotspotKeyPrefix = "hotspot:";

        public static string HotspotKey(string marker) => HotspotKeyPrefix + (marker ?? "");
        public static bool IsHotspotKey(string key) =>
            key != null && key.StartsWith(HotspotKeyPrefix, StringComparison.Ordinal);
        public static string MarkerOf(string key) =>
            IsHotspotKey(key) ? key.Substring(HotspotKeyPrefix.Length) : null;

        public const string TotalMemoryBytes = "totalMemoryBytes";
        public const string TotalReservedBytes = "totalReservedBytes";
        public const string GcUsedBytes = "gcUsedBytes";
        public const string GfxUsedBytes = "gfxUsedBytes";
        public const string DrawCalls = "drawCalls";
        public const string SetPassCalls = "setPassCalls";
        public const string Batches = "batches";
        public const string Triangles = "triangles";
        public const string Vertices = "vertices";

        /// <summary>
        /// Display order for reports. Frame time first because it is what the user asked about.
        ///
        /// STORED keys only — <see cref="GcPerSecondBytes"/> is deliberately absent because no run contains it, and a
        /// noise study that iterated this list would report it as permanently missing data.
        /// </summary>
        public static readonly string[] All =
        {
            FrameTimeMs, GameFrameTimeMs, GpuFrameTimeMs, GcPerFrameBytes, GameGcPerFrameBytes,
            TotalMemoryBytes, GcUsedBytes, GfxUsedBytes, TotalReservedBytes,
            DrawCalls, SetPassCalls, Batches, Triangles, Vertices
        };

        /// <summary>Human label + unit for reports. Unknown keys fall back to the raw key.</summary>
        public static string Label(string key)
        {
            // A hotspot key reaches here through the drift readings list, where printing the raw "hotspot:Camera.Render"
            // would leak an internal encoding into the one place the user goes to spot a contaminated sample.
            if (IsHotspotKey(key))
                return L.Tr($"{MarkerOf(key)} (hotspot, ms/frame)", $"{MarkerOf(key)}（热点，ms/帧）");
            return LabelOfCounter(key);
        }

        static string LabelOfCounter(string key) => key switch
        {
            FrameTimeMs => "Frame time, whole editor frame (ms)",
            GameFrameTimeMs => "Frame time, your game (ms)",
            FrameTimeP95Ms => "Frame time p95 (ms)",
            GpuFrameTimeMs => "GPU frame time (ms)",
            GcPerFrameBytes => "GC alloc / frame, whole editor process (B)",
            GameGcPerFrameBytes => "GC alloc / frame, your game (B)",
            GcPerSecondBytes => "GC alloc / second (B)",
            TotalMemoryBytes => "Total used memory (B)",
            TotalReservedBytes => "Total reserved memory (B)",
            GcUsedBytes => "Managed heap used (B)",
            GfxUsedBytes => "Graphics memory used (B)",
            DrawCalls => "Draw calls",
            SetPassCalls => "SetPass calls",
            Batches => "Batches",
            Triangles => "Triangles",
            Vertices => "Vertices",
            _ => key
        };

        /// <summary>
        /// Label without the unit suffix, for places where the value carries its own unit ("5.31 -> 5.22 ms"). Keeping
        /// "(ms)" there would print the unit twice on every row.
        /// </summary>
        public static string ShortLabel(string key)
        {
            if (IsHotspotKey(key)) return MarkerOf(key);
            return ShortLabelOfCounter(key);
        }

        static string ShortLabelOfCounter(string key) => key switch
        {
            FrameTimeMs => L.Tr("Frame time (editor frame)", "帧时间（编辑器整帧）"),
            GameFrameTimeMs => L.Tr("Frame time (your game)", "帧时间（你的游戏）"),
            FrameTimeP95Ms => L.Tr("Worst 5% of frames", "最卡的 5% 帧"),
            GpuFrameTimeMs => L.Tr("GPU frame time", "GPU 帧时间"),
            GcPerFrameBytes => L.Tr("GC alloc / frame (editor process)", "GC 分配 / 帧（编辑器进程）"),
            GameGcPerFrameBytes => L.Tr("GC alloc / frame (your game)", "GC 分配 / 帧（你的游戏）"),
            GcPerSecondBytes => L.Tr("GC alloc / second", "GC 分配 / 秒"),
            TotalMemoryBytes => L.Tr("Total used memory", "已用内存"),
            TotalReservedBytes => L.Tr("Total reserved", "保留内存"),
            GcUsedBytes => L.Tr("Managed heap", "托管堆"),
            GfxUsedBytes => L.Tr("Graphics memory", "图形内存"),
            DrawCalls => L.Tr("Draw calls", "Draw Call"),
            SetPassCalls => L.Tr("SetPass calls", "SetPass"),
            Batches => L.Tr("Batches", "批次"),
            Triangles => L.Tr("Triangles", "三角形"),
            Vertices => L.Tr("Vertices", "顶点"),
            _ => key
        };

        /// <summary>
        /// What the figure is a property of. The single source of truth behind both the report's grouping and
        /// <see cref="TransfersToDevice"/>.
        /// </summary>
        public static MetricScope Scope(string key)
        {
            // A hotspot's self time is milliseconds on THIS CPU, so it is scoped like frame time and not like content
            // — the default branch below would otherwise quietly promote it. The transferable half of a hotspot is its
            // sampled-frame hit rate, which is a property of the code path and is compared separately.
            if (IsHotspotKey(key)) return MetricScope.Machine;
            return ScopeOfCounter(key);
        }

        static MetricScope ScopeOfCounter(string key) => key switch
        {
            // Wall-clock on this CPU/GPU, and the one rate normalised by it.
            FrameTimeMs => MetricScope.Machine,
            // Excluding the editor does not make it transferable — it is still milliseconds on this CPU.
            GameFrameTimeMs => MetricScope.Machine,
            FrameTimeP95Ms => MetricScope.Machine,
            GpuFrameTimeMs => MetricScope.Machine,
            GcPerSecondBytes => MetricScope.Machine,

            // Process-wide totals: the game's figure plus the editor hosting it.
            TotalMemoryBytes => MetricScope.ContentPlusEditor,
            TotalReservedBytes => MetricScope.ContentPlusEditor,
            GcUsedBytes => MetricScope.ContentPlusEditor,
            GfxUsedBytes => MetricScope.ContentPlusEditor,

            // This used to fall through to Content, on the reasoning that "allocation per frame is a rate the running
            // scene produces, not a process total" — and that reasoning has since been measured and is false.
            // "GC Allocated In Frame" is a process counter: inside one Play Mode session on urp3dsample it read
            // 55 KB, 308 KB and 456 KB per frame while PlayerLoop's own subtree held a flat 2778 B, and running an
            // eval was enough to move it eightfold. It belongs with the other process totals.
            //
            // What the old comment described — a rate the scene produces — is real, and is now measured separately
            // as GameGcPerFrameBytes, which falls through to Content below because that is what it actually is.
            GcPerFrameBytes => MetricScope.ContentPlusEditor,

            _ => MetricScope.Content
        };

        /// <summary>Group heading and the one sentence that explains it, replacing the note that used to repeat per row.</summary>
        public static string ScopeTitle(MetricScope s) => s switch
        {
            MetricScope.Machine => L.Tr("Only true on this machine", "只在这台机器上成立"),
            MetricScope.ContentPlusEditor => L.Tr("Includes the editor's own memory", "含编辑器自身内存"),
            _ => L.Tr("Your code and content", "你的代码和内容")
        };

        public static string ScopeWhy(MetricScope s) => s switch
        {
            MetricScope.Machine => L.Tr("a reduction is real, but the number itself is not a device figure",
                                        "减少量是真的，但这个数字本身不代表设备表现"),
            MetricScope.ContentPlusEditor => L.Tr("which grows and is collected on its own schedule, so it drifts",
                                                  "它会按自己的节奏增长与回收，所以会自己漂"),
            _ => L.Tr("a change here changes what ships, so it carries to your device",
                      "这里的变化就是发布内容的变化，会带到设备上")
        };

        /// <summary>
        /// Whether an IMPROVEMENT in this metric is expected to hold on the target device. Allocation volume and
        /// per-frame call counts are properties of the content and the code, so a reduction measured in the editor
        /// carries over. Wall-clock frame time is a property of THIS machine's CPU/GPU and never does — it is only
        /// ever reported as a relative editor-side change, never as a device prediction.
        ///
        /// This is about DELTAS, not absolute values. No metric's absolute reading is a device figure: memory
        /// sampled in Play Mode includes the editor process itself (measured at ~4.4 GB on a scene that ships far
        /// smaller), so an absolute memory budget such as "under 1 GB" CANNOT be checked against it — that check
        /// needs the static asset-memory estimate or a real player build. See docs/goal-benchmark-loop-plan.md §4.
        /// </summary>
        public static bool TransfersToDevice(string key) => Scope(key) != MetricScope.Machine;

        /// <summary>
        /// Whether this figure still means something when the sample was disturbed by the editor.
        ///
        /// A different axis from <see cref="Scope"/>, which is about what carries to a device. This one asks what a
        /// two-second stall inside a twenty-second window does to the figure:
        ///
        ///   * Anything averaged PER FRAME is destroyed by it. Frame time obviously, but also the renderer counters —
        ///     draw calls and triangles are averages over the frames that happened, so collecting a quarter as many
        ///     frames averages a different quarter of the scene. In a scene whose camera moves (Cinemachine, an
        ///     animated shot, gameplay), it is a different part of the scene entirely.
        ///   * A RESIDENCY level survives. Graphics memory, the managed heap and the process totals are levels
        ///     sampled over time, not rates: the editor blocking for two seconds does not change what is resident.
        ///
        /// Measured on URP 3D Sample: after re-importing 24 textures, one repetition collected 254 frames where the
        /// baseline collected 997, with a single 2,012 ms frame. Every per-frame figure was junk — triangles read +6%
        /// because the roaming camera had covered different ground. Graphics memory read 1145.1 and 1145.4 MB across
        /// the two disturbed runs against a baseline of 1221.5 / 1212.7 / 1212.7 — a 67 MB drop, eight times the
        /// baseline's own spread. That figure was the real result and the only one worth keeping.
        /// </summary>
        public static bool SurvivesADisturbedSample(string key) =>
            key == TotalMemoryBytes || key == TotalReservedBytes || key == GcUsedBytes || key == GfxUsedBytes;

        /// <summary>
        /// Whether this figure still means what it says when Deep Profile is on.
        ///
        /// Every Machine-scoped row goes, which is where the milliseconds live — that part was always here. What was
        /// missing is that a per-frame figure is only as meaningful as the frame it is divided by, and Deep Profile
        /// makes the frame several times longer. Anything that ACCUMULATES over the frame's duration inflates with
        /// it. Measured on GardenScene, the same scene minutes apart: GC alloc / frame 52.0 KB with Deep Profile off,
        /// 150.4 KB with it on. Nothing in the project allocated more; the frames were 2.9x longer.
        ///
        /// So allocation per frame is dropped for the same reason the milliseconds are, and for a sharper one: it was
        /// the only inflated row left on screen, it is Content-scoped, and it is flagged as carrying to a device — so
        /// it read as "this is what your game allocates on a phone" while being three times that.
        ///
        /// Draw calls, SetPass, triangles and vertices are per-frame too and are KEPT: they are counts of what the
        /// renderer submits each frame, and a longer frame does not draw the scene more times.
        ///
        /// What Deep Profile is actually for survives all of this — which call paths ran, in how many frames, and
        /// what each allocated — because that lives in the hotspot rows rather than here.
        /// </summary>
        public static bool SurvivesDeepProfile(string key) =>
            Scope(key) != MetricScope.Machine && key != GcPerFrameBytes && key != GameGcPerFrameBytes;
    }

    /// <summary>One completed measurement. Written to disk the moment it is collected — the domain reload on exiting Play Mode would otherwise eat it.</summary>
    [Serializable]
    public sealed class BenchmarkRun
    {
        public string sessionId;
        public int index;
        public long startedAtUtcTicks;
        public double durationSeconds;
        public int frameCount;
        public BenchmarkFingerprint fingerprint;
        public BenchmarkMetric[] metrics;

        /// <summary>
        /// Snapshot of the edit-mode scene's dirty flag when this benchmark session began.
        ///
        /// Kept on the run because runs are the durable unit written across Play Mode reloads. The companion
        /// recorded flag distinguishes a clean new run from an older JSON file that predates this field.
        /// </summary>
        public bool sceneDirtyStateRecorded;
        public bool sceneWasDirtyAtStart;

        /// <summary>
        /// The scene that was actually running when this repetition sampled — read from Play Mode at both ends of the
        /// sampling window, not from the spec.
        ///
        /// These exist because the label and the truth can disagree, and until now only the label was kept. A run is
        /// filed under <see cref="BenchmarkSpec.sceneGuid"/>, which is the scene the editor had open (or the planned
        /// target). On the common shape of game — an entry scene that loads a loading scene that loads the level —
        /// the warmup is long enough for the game to leave the entry scene by itself, so the sample is taken in the
        /// level while the run is filed under the entry scene. The information needed to notice that was already
        /// being collected one layer down (<c>RuntimeSampler</c> records the scene at sample start, and its comment
        /// describes this exact sequence) and then thrown away, because the spec's name won.
        ///
        /// BOTH ends, because one end cannot tell a clean measurement of another scene from a measurement that
        /// straddled a scene change. The first is usable — the whole sampling window was one scene, so the numbers
        /// describe it and only the name is wrong. The second describes nothing: half the frames are a loading
        /// screen. <see cref="BenchmarkSceneTruth"/> is where that distinction is made.
        ///
        /// The recorded flag distinguishes "this run was taken before these fields existed" from "the scene had no
        /// asset path" — an unknown must never be reported as a disagreement.
        /// </summary>
        public bool sampledSceneRecorded;
        public string sampledSceneGuidAtStart;
        public string sampledScenePathAtStart;
        public string sampledSceneGuidAtEnd;
        public string sampledScenePathAtEnd;

        /// <summary>
        /// Scene this run was filed under before its label was corrected, or empty when it never was.
        ///
        /// Set when <see cref="BenchmarkSceneTruth"/> re-files a session under the scene it was actually taken in.
        /// Kept rather than discarded so the correction can be stated to the user afterwards — a run that silently
        /// changed which scene it claims to be about is a run whose history nobody can audit.
        /// </summary>
        public string relabelledFromScenePath;

        /// <summary>
        /// Whether the hotspot merge ran for this run — NOT whether it found anything.
        ///
        /// The distinction is the whole reason this field exists. Merging replays frame data and takes seconds, so it
        /// can be skipped or time out; a run written without it has an empty hotspot list that means "we did not look",
        /// and a comparison that read that as "the hotspot is gone" would announce a win nobody earned. False is also
        /// what every run written before hotspots were persisted deserializes to, which is exactly right.
        /// </summary>
        public bool hotspotsMerged;

        /// <summary>Top markers by main-thread self time, as merged for this run. Empty unless <see cref="hotspotsMerged"/>.</summary>
        public BenchmarkHotspot[] hotspots;

        public DateTime StartedAtUtc => new DateTime(startedAtUtcTicks, DateTimeKind.Utc);

        /// <summary>Hotspots as the runtime type, or an empty list. Never null, so callers cannot mistake absence for zero.</summary>
        public IReadOnlyList<Hotspot> Hotspots()
        {
            if (hotspots == null || hotspots.Length == 0) return Array.Empty<Hotspot>();
            var list = new List<Hotspot>(hotspots.Length);
            foreach (var h in hotspots) if (h != null) list.Add(h.ToHotspot());
            return list;
        }

        /// <summary>This run's entry for one marker, or null when it wasn't among the ones listed.</summary>
        public BenchmarkHotspot Hotspot(string marker)
        {
            if (hotspots == null || string.IsNullOrEmpty(marker)) return null;
            foreach (var h in hotspots)
                if (h != null && string.Equals(h.marker, marker, StringComparison.Ordinal)) return h;
            return null;
        }

        public BenchmarkMetric Get(string key)
        {
            if (metrics == null) return null;
            foreach (var m in metrics)
                if (m != null && string.Equals(m.key, key, StringComparison.Ordinal)) return m;
            return null;
        }

        /// <summary>
        /// Value of a DERIVED metric for this run — one computed from stored counters rather than sampled. Returns
        /// NaN when the inputs are missing, so a caller cannot mistake "couldn't compute it" for "it was zero".
        ///
        /// Uses the same headline statistics as everything else (mean allocation, median frame time), so the derived
        /// figure and the two counters it comes from can never tell contradictory stories.
        /// </summary>
        public double Derived(string key)
        {
            if (!string.Equals(key, BenchmarkMetricKeys.GcPerSecondBytes, StringComparison.Ordinal)) return double.NaN;

            var gc = Get(BenchmarkMetricKeys.GcPerFrameBytes);
            var ft = Get(BenchmarkMetricKeys.FrameTimeMs);
            if (gc == null || !gc.hasData || ft == null || !ft.hasData || ft.median <= 0) return double.NaN;
            return gc.avg * (1000.0 / ft.median); // bytes/frame × frames/second
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static BenchmarkRun FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<BenchmarkRun>(json); }
            catch { return null; }
        }

        /// <summary>Projects a <see cref="RuntimeProfileResult"/> into the persisted metric set. Nanosecond counters are converted to milliseconds here, once.</summary>
        public static BenchmarkMetric[] MetricsFrom(RuntimeProfileResult r)
        {
            const double NsToMs = 1.0 / 1_000_000.0;
            if (r == null) return Array.Empty<BenchmarkMetric>();
            return new[]
            {
                BenchmarkMetric.FromScaled(BenchmarkMetricKeys.FrameTimeMs, r.FrameTimeNs, NsToMs),
                // Empty when the merge could not read it; a run without it drops the row rather than comparing
                // against a differently-scoped number.
                BenchmarkMetric.FromScaled(BenchmarkMetricKeys.GameFrameTimeMs, r.GameFrameTimeNs, NsToMs),
                // Despite arriving from FrameTimingManager in milliseconds, RuntimeSampler.Stop stores this
                // counter in nanoseconds like the CPU one (it multiplies by 1e6 on the way in) — so it takes
                // the same conversion, not a pass-through.
                BenchmarkMetric.FromScaled(BenchmarkMetricKeys.GpuFrameTimeMs, r.GpuFrameTimeNs, NsToMs),
                BenchmarkMetric.From(BenchmarkMetricKeys.GcPerFrameBytes, r.GcPerFrameBytes),
                // Only present when the hotspot merge could read it; From() yields an empty metric otherwise, and a
                // run without it drops the row rather than comparing against a differently-defined number.
                BenchmarkMetric.From(BenchmarkMetricKeys.GameGcPerFrameBytes, r.GameGcPerFrameBytes),
                BenchmarkMetric.From(BenchmarkMetricKeys.TotalMemoryBytes, r.TotalMemoryBytes),
                BenchmarkMetric.From(BenchmarkMetricKeys.TotalReservedBytes, r.TotalReservedBytes),
                BenchmarkMetric.From(BenchmarkMetricKeys.GcUsedBytes, r.GcUsedBytes),
                BenchmarkMetric.From(BenchmarkMetricKeys.GfxUsedBytes, r.GfxUsedBytes),
                BenchmarkMetric.From(BenchmarkMetricKeys.DrawCalls, r.DrawCalls),
                BenchmarkMetric.From(BenchmarkMetricKeys.SetPassCalls, r.SetPassCalls),
                BenchmarkMetric.From(BenchmarkMetricKeys.Batches, r.Batches),
                BenchmarkMetric.From(BenchmarkMetricKeys.Triangles, r.Triangles),
                BenchmarkMetric.From(BenchmarkMetricKeys.Vertices, r.Vertices)
            };
        }

        /// <summary>Projects a merged result's hotspot list into the persisted form. Empty when the merge did not run or found nothing.</summary>
        public static BenchmarkHotspot[] HotspotsFrom(RuntimeProfileResult r)
        {
            if (r?.Hotspots == null || r.Hotspots.Count == 0) return Array.Empty<BenchmarkHotspot>();
            var list = new List<BenchmarkHotspot>(r.Hotspots.Count);
            foreach (var h in r.Hotspots) if (h != null) list.Add(BenchmarkHotspot.From(h));
            return list.ToArray();
        }
    }

    /// <summary>A completed set of repetitions taken under one spec. The unit a baseline or an "after" measurement is made of.</summary>
    public sealed class BenchmarkSession
    {
        public string Id { get; }
        public BenchmarkSpec Spec { get; }
        public IReadOnlyList<BenchmarkRun> Runs { get; }

        public BenchmarkSession(string id, BenchmarkSpec spec, IReadOnlyList<BenchmarkRun> runs)
        {
            Id = id;
            Spec = spec;
            Runs = runs ?? Array.Empty<BenchmarkRun>();
        }

        public bool HasRuns => Runs.Count > 0;
        public BenchmarkFingerprint Fingerprint => Runs.Count > 0 ? Runs[0].fingerprint : null;

        /// <summary>
        /// Gets the scene dirty state captured with this measurement. False means this session predates the snapshot,
        /// so callers may fall back to the current editor state for compatibility.
        /// </summary>
        public bool TryGetSceneDirtyAtStart(out bool dirty)
        {
            dirty = false;
            if (Runs.Count == 0 || Runs[0] == null || !Runs[0].sceneDirtyStateRecorded) return false;
            dirty = Runs[0].sceneWasDirtyAtStart;
            return true;
        }

        /// <summary>
        /// A frame this far above the run's own median was not the game — it was the editor blocking.
        ///
        /// Deliberately relative to the run's MEDIAN rather than to the baseline, so this asks "was this sample
        /// disturbed" and not "did it get slower". Those are different questions, and conflating them would suppress
        /// genuine regressions — the opposite of the point. A regression lowers the rate smoothly; a disturbance
        /// produces outliers. Measured on URP 3D Sample the two are not close: three clean baseline runs sat at 1.8x,
        /// 1.9x and 1.8x, while the two runs taken while the editor was still finishing 24 texture re-imports came in
        /// at 96x and 93x — with their MEDIANS almost unchanged (19.8 -> 21.2 ms), which is the tell. The absolute
        /// floor stops a very fast scene (a 2 ms median) tripping on an ordinary 40 ms hitch.
        /// </summary>
        public const double StallRatio = 20.0;
        public const double StallFloorMs = 250.0;

        /// <summary>The worst frame in a run as a multiple of its own median. 0 when frame time was not recorded, or when the worst frame is too small to be a stall at all.</summary>
        public static double StallFactorOf(BenchmarkRun run)
        {
            if (run?.metrics == null) return 0;
            foreach (var m in run.metrics)
                if (m != null && m.key == BenchmarkMetricKeys.FrameTimeMs && m.hasData && m.median > 0)
                    return m.max < StallFloorMs ? 0 : m.max / m.median;
            return 0;
        }

        /// <summary>Whether any repetition here was interrupted by something outside the game.</summary>
        public bool Disturbed
        {
            get
            {
                foreach (var r in Runs) if (StallFactorOf(r) >= StallRatio) return true;
                return false;
            }
        }

        /// <summary>The worst stall seen here, as a multiple of that run's median. 0 when none.</summary>
        public double WorstStallFactor
        {
            get
            {
                double worst = 0;
                foreach (var r in Runs) { double f = StallFactorOf(r); if (f > worst) worst = f; }
                return worst;
            }
        }

        /// <summary>Fewest frames any repetition collected — what the per-frame averages were actually built from.</summary>
        public int FewestFrames
        {
            get
            {
                int fewest = int.MaxValue;
                foreach (var r in Runs) if (r.frameCount < fewest) fewest = r.frameCount;
                return fewest == int.MaxValue ? 0 : fewest;
            }
        }
    }
}
