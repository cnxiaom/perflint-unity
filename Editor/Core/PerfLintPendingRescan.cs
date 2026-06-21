using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace PerfLint.Core
{
    /// <summary>
    /// Registry for "files pending incremental rescan by the window" (SessionState that survives domain reloads). Two sources write here:
    ///   1. Files modified by an AI fix after passing compilation verification (<see cref="PerfLint.Llm.PerfLintScriptFixVerifier"/>).
    ///   2. Scripts manually edited/deleted/moved by the user (<see cref="PerfLintChangeTracker"/>) — without this, the report would keep showing stale findings after a manual code change.
    /// After a window domain reload, <see cref="Consume"/> these paths during construction and call <c>RescanFile</c> on each to bring findings up to date, replacing an 86-second full rescan.
    ///
    /// Cap protection: if more than <see cref="Cap"/> files accumulate at once (e.g. branch switch or large batch reimport), skip per-file incremental scanning — clear the list and call
    /// <see cref="SetStale"/> so the window can prompt "too many changes, a full rescan is recommended", avoiding serially rescanning hundreds of files on reload which would slow down opening.
    /// </summary>
    public static class PerfLintPendingRescan
    {
        private const string KFiles = "PerfLint.PendingRescan.Files";
        private const string KStale = "PerfLint.PendingRescan.Stale";
        private const int Cap = 100;

        /// <summary>Merges a batch of files into the pending-rescan set (deduplicated). If the cap is exceeded, falls back to the "globally stale" marker. Empty input is a no-op.</summary>
        public static void Record(IEnumerable<string> assetPaths)
        {
            if (assetPaths == null) return;
            var set = LoadSet();
            bool any = false;
            foreach (var p in assetPaths)
                if (!string.IsNullOrEmpty(p)) { set.Add(p); any = true; }
            if (!any) return;

            if (set.Count > Cap) { EditorPrefsErase(); SetStale(true); return; }
            SessionState.SetString(KFiles, string.Join("\n", set));
        }

        /// <summary>Retrieves and clears the pending-rescan file list (consumed once after a window reload). Returns an empty array if there is nothing pending.</summary>
        public static string[] Consume()
        {
            string raw = SessionState.GetString(KFiles, "");
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            SessionState.EraseString(KFiles);
            return raw.Split('\n').Where(s => s.Length > 0).ToArray();
        }

        /// <summary>Sets or clears the "globally stale" marker (used when there are too many changes to rescan incrementally one by one).</summary>
        public static void SetStale(bool value)
        {
            if (value) SessionState.SetBool(KStale, true);
            else SessionState.EraseBool(KStale);
        }

        /// <summary>Retrieves and clears the "globally stale" marker.</summary>
        public static bool ConsumeStale()
        {
            bool v = SessionState.GetBool(KStale, false);
            if (v) SessionState.EraseBool(KStale);
            return v;
        }

        private static HashSet<string> LoadSet()
        {
            var set = new HashSet<string>();
            string raw = SessionState.GetString(KFiles, "");
            if (!string.IsNullOrEmpty(raw))
                foreach (var p in raw.Split('\n')) if (p.Length > 0) set.Add(p);
            return set;
        }

        private static void EditorPrefsErase() => SessionState.EraseString(KFiles);
    }
}
