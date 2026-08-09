using System;
using System.IO;
using UnityEditor;

namespace PerfLint.Core
{
    /// <summary>
    /// Which build of PerfLint produced a stored result — so a panel can tell the reader when what they are looking
    /// at was written by a different one.
    ///
    /// This exists because findings are generated once, at sampling time, and persisted verbatim; loading a session
    /// does not re-run the analyzer. That is deliberate (a finding carries the advice that was true when it was
    /// produced, and re-deriving it later would silently rewrite history) but it has a consequence nobody is told
    /// about: update PerfLint, reopen the panel, and the wording, the thresholds and the advice are all still the
    /// old ones until the next sample. Observed the hard way — an analyzer's text was changed, the panel reopened,
    /// and the old sentence was still on screen, which reads exactly like the change not working.
    ///
    /// Two stamps, because one of them is blind to the case that matters most during development:
    ///
    /// * <see cref="Version"/> — the package version. Moves when a user updates through UPM, which is the situation
    ///   this feature exists for. Does NOT move while someone edits the package in place: editing a .cs file does not
    ///   touch package.json, so a developer's own changes are invisible to it.
    /// * <see cref="AssemblyWrittenAtUtc"/> — the mtime of the compiled editor assembly. Moves on every recompile and
    ///   on nothing else. In particular it does NOT move on domain reload, which rules out the obvious alternative
    ///   (assembly load time) — that changes every time Play Mode is entered and would flag every session as stale.
    ///
    /// Either one differing means the stored result came from a different build.
    /// </summary>
    public static class PerfLintBuildStamp
    {
        /// <summary>Package version, or "" when PerfLint is not installed as a package (embedded/Assets copy).</summary>
        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PerfLintBuildStamp).Assembly);
                    _version = pkg?.version ?? "";
                }
                catch { _version = ""; }
                return _version;
            }
        }
        static string _version;

        /// <summary>
        /// Ticks of the compiled assembly's last write time, or 0 when it cannot be read.
        ///
        /// Read once and cached: within one domain the assembly file cannot change under us, and a recompile brings a
        /// new domain with it.
        /// </summary>
        public static long AssemblyWrittenAtUtcTicks
        {
            get
            {
                if (_asmTicks.HasValue) return _asmTicks.Value;
                try
                {
                    string path = typeof(PerfLintBuildStamp).Assembly.Location;
                    _asmTicks = string.IsNullOrEmpty(path) || !File.Exists(path)
                        ? 0
                        : File.GetLastWriteTimeUtc(path).Ticks;
                }
                catch { _asmTicks = 0; }
                return _asmTicks.Value;
            }
        }
        static long? _asmTicks;

        /// <summary>
        /// Whether a result stamped with these values came from a build other than the one running now.
        ///
        /// Conservative on both sides. A stamp of 0/"" is a result stored before stamping existed — unknown, not
        /// different, so it says false rather than nagging about every old session. And a stamp that cannot be read
        /// NOW (ticks 0) also says false: a banner is worth showing when we know, never on a guess.
        /// </summary>
        public static bool DiffersFrom(string version, long assemblyTicks)
        {
            if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(Version) &&
                !string.Equals(version, Version, StringComparison.Ordinal))
                return true;
            return assemblyTicks != 0 && AssemblyWrittenAtUtcTicks != 0 && assemblyTicks != AssemblyWrittenAtUtcTicks;
        }
    }
}
