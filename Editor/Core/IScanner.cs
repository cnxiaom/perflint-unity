using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// A diagnostic scanner. Each IScanner is responsible for one category of rules
    /// (e.g. texture import settings, script GC allocations, deprecated APIs).
    /// Implementation contract:
    /// - Purely deterministic and side-effect-free (read-only access to the project; must not modify anything).
    /// - Must not depend on Play Mode (W1-V1 are all static analysis; dynamic Profiler analysis is V2).
    /// - Support mid-scan cancellation via ScanContext.CancellationToken and report progress via ReportProgress.
    /// ScanRunner discovers all implementations automatically via reflection; no manual registration required.
    /// </summary>
    public interface IScanner
    {
        /// <summary>Display name, e.g. "Texture Import Settings".</summary>
        string Name { get; }

        Domain Domain { get; }

        /// <summary>Execute the scan and produce zero or more Findings.</summary>
        IEnumerable<Finding> Scan(ScanContext context);
    }
}
