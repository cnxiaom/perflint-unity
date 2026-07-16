using PerfLint.Capture;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Injects the in-player shader-variant recorder into scene 0 of every DEVELOPMENT build made while
    /// Graphics Settings → "Log Shader Compilation" is on (the same switch that makes the player log variants at all,
    /// so there is nothing to record without it and nothing extra to gate on).
    ///
    /// Why inject instead of relying on the recorder's RuntimeInitializeOnLoadMethod alone: a scene reference is the
    /// one mechanism that GUARANTEES the runtime assembly ships and the component runs — it can't be lost to managed
    /// stripping, to RuntimeInitializeOnLoad registration quirks, or to GraphicsSettings reading differently inside
    /// the player. The self-boot path stays as a fallback for additive-only/bundle-scene setups; the recorder's Awake
    /// deduplicates when both fire.
    ///
    /// Only the in-build scene data is modified — the scene asset on disk is never touched. Release builds are gated
    /// out twice: this injector skips them, and the recorder assembly isn't even compiled into them.
    /// </summary>
    public sealed class PlayerVariantBuildInjector : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            bool logOn;
            try { logOn = GraphicsSettings.logWhenShaderIsCompiled; }
            catch { return; }

            if (!ShouldInject(
                    hasReport: report != null, // null = play-mode scene load in the editor, not a build
                    developmentBuild: report != null && (report.summary.options & BuildOptions.Development) != 0,
                    logShaderCompilationOn: logOn,
                    sceneBuildIndex: scene.buildIndex)) return;

            var go = new GameObject("PerfLintVariantRecorder");
            if (go.scene != scene) SceneManager.MoveGameObjectToScene(go, scene); // active scene can differ from the one being processed
            var recorder = go.AddComponent<PlayerVariantRecorder>();
            recorder.bootedByInjection = true; // build-time scene edit — Awake runs in the player, not here
            Debug.Log("PerfLint: added the shader-variant recorder to this development build (Log Shader Compilation is on). " +
                      "It streams compiled variants to the PerfLint Shaders panel and keeps a capture file under persistentDataPath.");
        }

        /// <summary>The whole gate as a pure function: real build + development + logging on + first scene only.</summary>
        internal static bool ShouldInject(bool hasReport, bool developmentBuild, bool logShaderCompilationOn, int sceneBuildIndex)
            => hasReport && developmentBuild && logShaderCompilationOn && sceneBuildIndex == 0;
    }
}
