using System;
using System.Collections.Generic;
using System.IO;
using PerfLint.L10n;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Which scene a measurement was really taken in, as opposed to the one it was filed under.
    ///
    /// The two are the same on a project that measures one open scene, and routinely different on a project that
    /// boots through an entry scene: the editor holds Init, the warmup is five seconds, and the game has loaded the
    /// level well before sampling starts. The run is then filed under Init, becomes the baseline under Init, and is
    /// invalidated the moment the user discovers the scene plan and points measuring at the level — which is the
    /// first thing they would do, and it throws away the measurement they already paid two minutes for.
    ///
    /// Every judgement here is made from what Play Mode reported at both ends of each sampling window
    /// (<see cref="BenchmarkRun.sampledSceneGuidAtStart"/> and …AtEnd). Nothing is inferred from scene NAMES, build
    /// settings, or how a project "usually" looks: a guess about the user's boot flow that relabels their baseline
    /// would be worse than the mislabelling it replaces.
    ///
    /// Three outcomes, and the middle one is the reason both ends are recorded:
    ///
    ///   Agrees       the sample happened where the run says it did. Nothing to say.
    ///   Relabelled   every repetition sampled, start to end, in ONE other scene. The numbers describe that scene —
    ///                only the name was wrong — so the run is re-filed under it and kept.
    ///   Unusable     a repetition straddled a scene change, or the repetitions did not agree with each other. Half
    ///                a loading screen and half a level is not a measurement of either, and three repetitions taken
    ///                in different scenes are not repetitions. Nothing is relabelled and nothing may be pinned.
    ///
    /// Pure and Unity-free so it can be tested exhaustively: everything it needs is already on the runs.
    /// </summary>
    public static class BenchmarkSceneTruth
    {
        public enum Verdict
        {
            /// <summary>Nothing was recorded — a session from before these fields existed, or scenes with no asset path.</summary>
            Unknown,
            /// <summary>The label and the truth match.</summary>
            Agrees,
            /// <summary>Taken cleanly in one other scene. Usable, once re-filed under it.</summary>
            Relabelled,
            /// <summary>Cannot be attributed to any one scene.</summary>
            Unusable
        }

        public readonly struct Reading
        {
            public readonly Verdict Verdict;
            /// <summary>The scene the numbers actually describe. Empty unless <see cref="Verdict"/> is Relabelled.</summary>
            public readonly string SceneGuid;
            public readonly string ScenePath;
            /// <summary>Scene the run was filed under. Empty when the session has no spec.</summary>
            public readonly string FiledUnderPath;
            /// <summary>Every distinct scene the sampling windows touched, in the order first seen. Populated for Unusable.</summary>
            public readonly IReadOnlyList<string> ScenesTouched;

            public Reading(Verdict verdict, string sceneGuid, string scenePath, string filedUnderPath,
                           IReadOnlyList<string> scenesTouched)
            {
                Verdict = verdict;
                SceneGuid = sceneGuid ?? "";
                ScenePath = scenePath ?? "";
                FiledUnderPath = filedUnderPath ?? "";
                ScenesTouched = scenesTouched ?? Array.Empty<string>();
            }

            public string SceneName => NameOf(ScenePath);
            public string FiledUnderName => NameOf(FiledUnderPath);

            /// <summary>Scene names the sampling touched, for a sentence that has to list them.</summary>
            public IReadOnlyList<string> SceneNamesTouched
            {
                get
                {
                    var names = new List<string>(ScenesTouched.Count);
                    foreach (var p in ScenesTouched) names.Add(NameOf(p));
                    return names;
                }
            }
        }

        public static string NameOf(string scenePath) =>
            string.IsNullOrEmpty(scenePath) ? "" : Path.GetFileNameWithoutExtension(scenePath);

        /// <summary>
        /// What this session's repetitions say about where they were taken.
        ///
        /// A session that has already been re-filed reports Relabelled again, from
        /// <see cref="BenchmarkRun.relabelledFromScenePath"/> — the correction has to stay visible after it is
        /// applied, or the screen that would explain it has nothing left to read.
        /// </summary>
        public static Reading Read(BenchmarkSession session)
        {
            var runs = session?.Runs;
            if (runs == null || runs.Count == 0) return new Reading(Verdict.Unknown, null, null, null, null);

            string filedGuid = session.Spec?.sceneGuid ?? "";
            string filedPath = session.Spec?.scenePath ?? "";

            // Already corrected: the run's own record of where it came from outranks a comparison that would now
            // trivially agree with itself.
            foreach (var r in runs)
                if (r != null && !string.IsNullOrEmpty(r.relabelledFromScenePath))
                    return new Reading(Verdict.Relabelled, filedGuid, filedPath, r.relabelledFromScenePath, null);

            string agreedGuid = null, agreedPath = null;
            var touched = new List<string>();
            bool anyRecorded = false, straddled = false, disagreed = false;

            foreach (var r in runs)
            {
                if (r == null || !r.sampledSceneRecorded) continue;

                string a = r.sampledSceneGuidAtStart ?? "";
                string b = r.sampledSceneGuidAtEnd ?? "";
                // A scene with no asset path (created at runtime, or never saved) is an unknown, not a disagreement.
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) continue;

                anyRecorded = true;
                Touch(touched, r.sampledScenePathAtStart);
                Touch(touched, r.sampledScenePathAtEnd);

                if (!string.Equals(a, b, StringComparison.Ordinal)) { straddled = true; continue; }

                if (agreedGuid == null) { agreedGuid = a; agreedPath = r.sampledScenePathAtStart; }
                else if (!string.Equals(agreedGuid, a, StringComparison.Ordinal)) disagreed = true;
            }

            if (!anyRecorded) return new Reading(Verdict.Unknown, null, null, filedPath, null);
            if (straddled || disagreed) return new Reading(Verdict.Unusable, null, null, filedPath, touched);

            // Nothing recorded a usable scene, which is the "every repetition was in an unsaved scene" case.
            if (agreedGuid == null) return new Reading(Verdict.Unknown, null, null, filedPath, null);

            // No spec to disagree with — take the truth as the label rather than inventing a conflict.
            if (string.IsNullOrEmpty(filedGuid) ||
                string.Equals(filedGuid, agreedGuid, StringComparison.Ordinal))
                return new Reading(Verdict.Agrees, agreedGuid, agreedPath, filedPath, touched);

            return new Reading(Verdict.Relabelled, agreedGuid, agreedPath, filedPath, touched);
        }

        static void Touch(List<string> into, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (var p in into) if (string.Equals(p, path, StringComparison.Ordinal)) return;
            into.Add(path);
        }

        /// <summary>
        /// Whether a session may be pinned as a baseline at all, or a sentence saying why not.
        ///
        /// Only Unusable refuses. It sits beside the refusals <see cref="BenchmarkBaseline.Pin"/> already makes for
        /// VSync and Deep Profile, and for the same reason: those are not slow measurements, they are broken
        /// instruments, and a "before" taken with one poisons every verdict measured against it afterwards.
        /// </summary>
        public static string RefusalFor(BenchmarkSession session)
        {
            var reading = Read(session);
            if (reading.Verdict != Verdict.Unusable) return null;

            var names = reading.SceneNamesTouched;
            string list = names.Count > 0 ? string.Join(" → ", names) : "";
            return string.IsNullOrEmpty(list)
                ? L.Tr("The repetitions were not all taken in the same scene, so there is no one scene these numbers describe.",
                            "各轮测量并非都在同一个场景里完成，因此这组数字不描述任何一个场景。")
                : L.Tr($"Sampling ran across more than one scene ({list}), so there is no one scene these numbers describe. Set which scene to measure and take it again — the measurement will then wait until the game has loaded that scene before it starts.",
                            $"采样跨越了不止一个场景（{list}），因此这组数字不描述任何一个场景。请指定要测量的场景后重测——之后测量会等游戏加载出那个场景才开始。");
        }
    }
}
