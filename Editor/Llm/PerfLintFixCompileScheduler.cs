using UnityEditor;

namespace PerfLint.Llm
{
    /// <summary>
    /// Debounced background-compilation trigger fired after an AI script fix is written to disk.
    ///
    /// Background: <see cref="ScriptFixService.Apply"/> does NOT trigger compilation immediately when writing files
    /// (to avoid a domain-reload stall after every individual fix). Verification and failure rollback are handled by
    /// <see cref="PerfLintScriptFixVerifier"/>, which piggybacks on Unity's next natural compilation pass. The problem
    /// is that this pass may never arrive in a timely manner (e.g. the user is viewing the diff in an external editor,
    /// or Auto Refresh is disabled) — leaving a bad fix sitting on disk without being rolled back.
    ///
    /// This class provides the missing "trigger": after Apply succeeds it registers a pending request, then after
    /// <see cref="DelaySeconds"/> seconds of silence it fires one <see cref="AssetDatabase.Refresh"/> call (needed
    /// because .cs files written externally must be Refreshed before Unity imports and recompiles them). Multiple
    /// consecutive fixes are coalesced into a single compilation pass (debounce), avoiding a stall per fix.
    /// Compilation failure → verifier rolls back; success → verifier cleans up and incrementally re-scans the window.
    ///
    /// AI batch-fix wraps each run with <see cref="Suspend"/>/<see cref="Resume"/>: fixes are applied one-by-one
    /// asynchronously, and compilation must not be allowed to interrupt the loop mid-batch with a domain reload;
    /// Resume fires a single unified trigger after the entire batch completes.
    ///
    /// Toggle: <see cref="LlmSettings.AutoVerifyFix"/> (on by default). When disabled, this class goes fully silent
    /// and falls back to pure lazy verification.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfLintFixCompileScheduler
    {
        private const double DelaySeconds = 2.5;

        private static bool _pending;
        private static double _fireAt;
        private static bool _suspended;

        static PerfLintFixCompileScheduler()
        {
            EditorApplication.update += Tick;
        }

        /// <summary>Call after a fix is successfully written to disk: registers a pending "verify soon" request. Multiple calls are coalesced into one (the timer is reset each time).</summary>
        public static void RequestSoon()
        {
            if (!LlmSettings.AutoVerifyFix) return;
            _pending = true;
            _fireAt = EditorApplication.timeSinceStartup + DelaySeconds;
        }

        /// <summary>AI batch start: suspend the scheduler to prevent compilation from interrupting the per-fix loop.</summary>
        public static void Suspend() => _suspended = true;

        /// <summary>AI batch end: lift the suspension; if any fixes were applied during the batch (_pending is set), restart the timer from this moment and fire a single unified trigger after the delay.</summary>
        public static void Resume()
        {
            _suspended = false;
            if (_pending) _fireAt = EditorApplication.timeSinceStartup + DelaySeconds;
        }

        private static void Tick()
        {
            if (!_pending || _suspended) return;
            if (EditorApplication.timeSinceStartup < _fireAt) return;
            // Already compiling/importing → wait for the next tick to avoid stacking requests.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            _pending = false;
            // .cs files written externally require a Refresh before Unity imports and recompiles them;
            // once compilation finishes, the verifier takes over via
            // assemblyCompilationFinished (failure → rollback) / DidReloadScripts (success → cleanup + incremental re-scan).
            AssetDatabase.Refresh();
        }
    }
}
