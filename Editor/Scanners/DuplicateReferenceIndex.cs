using System;
using System.Collections.Generic;

namespace PerfLint.Scanners
{
    /// <summary>
    /// A one-pass index of who references the copies in an ASSET.DUP001 duplicate group, built by
    /// <see cref="DuplicateAssetMerger.BuildReferenceIndex"/>. Holds, per copy: how many project-wide references point
    /// at it (for the chooser's "N references" + the default pick) and **which physical files** contain those
    /// references (so the merge rewrites only those files instead of sweeping the whole project a second time).
    ///
    /// Built once when the merge chooser opens and reused for the merge — this turns the old "scan twice" (count, then
    /// redirect) into "scan once", and the redirect touches only the handful of referencing files.
    /// </summary>
    public sealed class DuplicateReferenceIndex
    {
        private readonly Dictionary<string, int> _countByPath;          // group asset path -> total reference occurrences
        private readonly Dictionary<string, List<string>> _filesByGuid; // guid -> physical files referencing it (excl. its own .meta)
        private readonly Dictionary<string, string> _guidByPath;        // group asset path -> guid

        public DuplicateReferenceIndex(
            Dictionary<string, int> countByPath,
            Dictionary<string, List<string>> filesByGuid,
            Dictionary<string, string> guidByPath)
        {
            _countByPath = countByPath ?? new Dictionary<string, int>();
            _filesByGuid = filesByGuid ?? new Dictionary<string, List<string>>();
            _guidByPath = guidByPath ?? new Dictionary<string, string>();
        }

        public IReadOnlyDictionary<string, int> CountByPath => _countByPath;

        public int ReferenceCount(string assetPath)
            => _countByPath.TryGetValue(assetPath, out int v) ? v : 0;

        public string GuidOf(string assetPath)
            => _guidByPath.TryGetValue(assetPath, out string g) ? g : null;

        public IReadOnlyList<string> FilesReferencing(string guid)
            => guid != null && _filesByGuid.TryGetValue(guid, out var list) ? list : Array.Empty<string>();
    }
}
