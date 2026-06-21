using System.Threading;

namespace PerfLint.Core
{
    /// <summary>
    /// Context for a single scan run. Passed to every IScanner; carries the cancellation token, progress reporting, and configuration.
    /// Future additions here: per-scanner toggles, ignored-rule lists, target Unity version (used by the migration domain), etc.
    /// </summary>
    public sealed class ScanContext
    {
        public CancellationToken CancellationToken { get; }

        /// <summary>Progress reporting: called by scanners inside long loops; used by the driver to refresh the progress bar.</summary>
        public System.Action<string, float> ReportProgress { get; }

        /// <summary>Target Unity version referenced by the migration domain (e.g. "6000.0"). null means the current version.</summary>
        public string TargetUnityVersion { get; }

        /// <summary>
        /// Build platform name targeted by diagnostics (the name used for importer override queries, e.g. "WebGL"/"Standalone"/"Android").
        /// null/empty means use the currently active build platform. Primary uses: multi-platform diagnostics and **unit-test injection** (no need to actually switch activeBuildTarget
        /// — switching triggers a full project reimport). Scanners that read this: AudioImportScanner / TextureImportScanner.
        /// </summary>
        public string TargetPlatform { get; }

        public ScanContext(
            CancellationToken cancellationToken = default,
            System.Action<string, float> reportProgress = null,
            string targetUnityVersion = null,
            string targetPlatform = null)
        {
            CancellationToken = cancellationToken;
            ReportProgress = reportProgress ?? ((_, __) => { });
            TargetUnityVersion = targetUnityVersion;
            TargetPlatform = targetPlatform;
        }
    }
}
