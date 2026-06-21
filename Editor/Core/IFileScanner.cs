using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// A diagnostic scanner that supports incremental re-scanning of individual files. After an AI Fix is applied,
    /// only the modified file is re-scanned and its old findings are replaced with the newly computed ones,
    /// avoiding a full re-scan (see ScanRunner.RescanFile). Implementors must ensure that ScanFile produces
    /// results consistent with a full Scan for the same file.
    /// </summary>
    public interface IFileScanner : IScanner
    {
        /// <summary>Whether this scanner is responsible for the given file (e.g., a script scanner only accepts .cs files outside the Editor directory).</summary>
        bool Handles(string assetPath);

        /// <summary>Scans a single file only and produces findings for that file.</summary>
        IEnumerable<Finding> ScanFile(string assetPath, ScanContext context);
    }
}
