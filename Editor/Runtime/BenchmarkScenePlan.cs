using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Which scene Play Mode starts from, and which scene the measurement is actually about.
    ///
    /// These are not the same question, and treating them as one is why every measurement before this was filed
    /// against the wrong scene on any project with a boot sequence. The ordinary shape is Init → menu → the level:
    /// the editor has Init open because that is the only scene the game will boot from, the game loads its way to
    /// the level, and the level is the thing anyone actually wants numbers about. The runner recorded the scene the
    /// SPEC named — Init — so a run sampled entirely inside the level came back labelled as a measurement of the
    /// boot scene, and a baseline pinned from it could never be matched against one taken any other way.
    ///
    /// Observed on the museum project, which opens Init and is inside hnmz-overview by the time sampling begins.
    /// The note about it sat in BenchmarkRunner as a known wrinkle for months; this type is the fix rather than the
    /// note.
    ///
    /// Both fields are optional, and both empty is exactly the old behaviour — start from whatever is open, and the
    /// measurement is about that scene. Nothing existing has to be reconfigured.
    ///
    /// Stored per PROJECT, under Library/ beside the baseline. Deliberately NOT EditorPrefs: those are shared by
    /// every project on the machine and every editor version installed, so a scene GUID written there would be read
    /// back by a different project that has no such asset. Living beside the baseline also means the two are
    /// discarded together — a plan that outlived the baseline it was measured under would silently re-point an
    /// existing comparison at a different scene.
    /// </summary>
    public static class BenchmarkScenePlan
    {
        /// <summary>A resolved plan. Paths are looked up from the GUIDs on load, so renaming a scene does not break it.</summary>
        public sealed class Plan
        {
            /// <summary>Scene to open before entering Play Mode. Empty means "start from whatever is already open".</summary>
            public string StartGuid { get; }
            /// <summary>Scene the run is about — sampling waits for it to load. Empty means "the scene we started from".</summary>
            public string TargetGuid { get; }

            public Plan(string startGuid, string targetGuid)
            {
                StartGuid = startGuid ?? "";
                TargetGuid = targetGuid ?? "";
            }

            public static readonly Plan Empty = new Plan("", "");

            public bool HasStart => !string.IsNullOrEmpty(StartGuid);
            public bool HasTarget => !string.IsNullOrEmpty(TargetGuid);
            public bool IsEmpty => !HasStart && !HasTarget;

            /// <summary>Current asset path of the start scene, or empty when unset or the asset is gone.</summary>
            public string StartPath => PathOf(StartGuid);
            /// <summary>Current asset path of the target scene, or empty when unset or the asset is gone.</summary>
            public string TargetPath => PathOf(TargetGuid);

            /// <summary>
            /// A configured scene whose asset no longer exists. Reported rather than ignored: silently falling back
            /// to "measure whatever is open" would keep producing runs under a plan the user believes is in force.
            /// </summary>
            public bool StartMissing => HasStart && string.IsNullOrEmpty(StartPath);
            public bool TargetMissing => HasTarget && string.IsNullOrEmpty(TargetPath);
            public bool AnyMissing => StartMissing || TargetMissing;
        }

        static string Dir
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint");
            }
        }

        static string FilePath => Path.Combine(Dir, "benchmark-scenes.json");

        // Read on nearly every repaint of two windows, so the parse is cached and invalidated on write. The file is
        // tiny, but the panels ask for it inside layout.
        static Plan _cached;
        static bool _loaded;

        public static Plan Current
        {
            get
            {
                if (_loaded && _cached != null) return _cached;
                _cached = LoadFromDisk();
                _loaded = true;
                return _cached;
            }
        }

        public static void Save(string startGuid, string targetGuid)
        {
            var dto = new Dto { startGuid = startGuid ?? "", targetGuid = targetGuid ?? "" };
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonUtility.ToJson(dto, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PerfLint] could not save the benchmark scene plan: {e.Message}");
            }
            _cached = new Plan(dto.startGuid, dto.targetGuid);
            _loaded = true;
        }

        public static void Clear() => Save("", "");

        /// <summary>Drops the in-memory copy. For tests, and for anything that rewrites the file behind our back.</summary>
        public static void InvalidateCache() { _cached = null; _loaded = false; }

        static Plan LoadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath)) return Plan.Empty;
                var dto = JsonUtility.FromJson<Dto>(File.ReadAllText(FilePath));
                return dto == null ? Plan.Empty : new Plan(dto.startGuid, dto.targetGuid);
            }
            catch { return Plan.Empty; }
        }

        /// <summary>Asset path for a GUID, or empty when the GUID is unset or names an asset that no longer exists.</summary>
        public static string PathOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "";
            try
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                return string.IsNullOrEmpty(p) || !File.Exists(p) ? "" : p;
            }
            catch { return ""; }
        }

        /// <summary>Scene name from an asset path ("Assets/Levels/B.unity" → "B"), or empty.</summary>
        public static string NameOf(string scenePath) =>
            string.IsNullOrEmpty(scenePath) ? "" : Path.GetFileNameWithoutExtension(scenePath);

        /// <summary>Why a measurement cannot be built right now. The wording is left to the caller, which knows its own screen.</summary>
        public enum LaunchProblem
        {
            None = 0,
            /// <summary>No saved scene to start from — an unsaved scene has no identity to file a measurement under.</summary>
            NoScene,
            /// <summary>A scene the plan names has been deleted or moved.</summary>
            PlanSceneMissing
        }

        /// <summary>
        /// The spec a measurement started right now should run, with the plan resolved against the open scene.
        ///
        /// One implementation because two panels offer this button, and a project cannot have two answers to "which
        /// scene is this measurement about". Two answers would surface as baselines that refuse to compare with each
        /// other and nothing on screen explaining why — the failure mode this codebase keeps re-learning, most
        /// recently as three copies of the refusal policy of which one was stale.
        /// </summary>
        public static BenchmarkSpec BuildSpec(float warmupSeconds, float sampleSeconds, int repetitions,
            bool saveRuntimeSession, out LaunchProblem problem)
        {
            problem = LaunchProblem.None;
            var plan = Current;
            if (plan.AnyMissing) { problem = LaunchProblem.PlanSceneMissing; return null; }

            var open = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            // An unset start resolves to the open scene HERE rather than being left blank, so the run records which
            // scene it actually booted from — but the SPEC keeps it blank, which is how the runner knows not to open
            // anything on a project that never configured a plan.
            string startPath = plan.HasStart ? plan.StartPath : (open.IsValid() ? open.path : "");
            if (string.IsNullOrEmpty(startPath)) { problem = LaunchProblem.NoScene; return null; }

            string startGuid = AssetDatabase.AssetPathToGUID(startPath);

            return new BenchmarkSpec
            {
                // What the run is ABOUT: the target when one is set, otherwise the scene we boot into.
                scenePath = plan.HasTarget ? plan.TargetPath : startPath,
                sceneGuid = plan.HasTarget ? plan.TargetGuid : startGuid,
                startScenePath = plan.HasStart ? startPath : "",
                startSceneGuid = plan.HasStart ? startGuid : "",
                targetScenePath = plan.HasTarget ? plan.TargetPath : "",
                targetSceneGuid = plan.HasTarget ? plan.TargetGuid : "",
                warmupSeconds = warmupSeconds,
                sampleSeconds = sampleSeconds,
                repetitions = Mathf.Max(1, repetitions),
                driveCamera = false,
                saveRuntimeSession = saveRuntimeSession
            };
        }

        /// <summary>
        /// Whether starting this spec would open a scene over unsaved work.
        ///
        /// Asked by the panels before they confirm, because the runner refuses outright in this state — deliberately,
        /// so the question reaches somebody who can answer it rather than being raised from inside the state machine.
        /// </summary>
        public static bool WouldDiscardUnsavedWork(BenchmarkSpec spec)
        {
            var open = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!open.isDirty) return false;
            string start = spec?.startScenePath;
            return !string.IsNullOrEmpty(start) && !string.Equals(start, open.path, StringComparison.Ordinal);
        }

        [Serializable]
        sealed class Dto
        {
            public string startGuid;
            public string targetGuid;
        }
    }
}
