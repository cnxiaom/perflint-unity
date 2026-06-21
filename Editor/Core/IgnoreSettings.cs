using System;
using System.Collections.Generic;
using UnityEditor;

namespace PerfLint.Core
{
    /// <summary>
    /// Ignore-path configuration (EditorPrefs, machine-local). Each line is a "path segment"; any asset path
    /// containing at least one segment is ignored by all scanners.
    /// Defaults to /Plugins/ (Unity's conventional third-party directory). Users may add entries such as
    /// Dependencies/, SDK, logging-wrapper files, etc.
    /// </summary>
    public static class IgnoreSettings
    {
        private const string Key = "PerfLint.IgnorePatterns";
        public const string Default = "/Plugins/";

        public static string Raw
        {
            get => EditorPrefs.GetString(Key, Default);
            set => EditorPrefs.SetString(Key, value ?? "");
        }

        public static IReadOnlyList<string> Patterns()
        {
            var list = new List<string>();
            foreach (var line in Raw.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>Returns whether the given asset path should be ignored (contains any segment, case-insensitive).</summary>
        public static bool ShouldIgnore(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string norm = path.Replace('\\', '/');
            foreach (var p in Patterns())
                if (norm.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
