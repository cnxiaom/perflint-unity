using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Last-resort recovery of compiler errors from the Console window, for the one case
    /// <see cref="CompileErrorCollector"/> cannot cover: the compilation finished before any managed subscriber
    /// existed (project opened already broken), so <c>assemblyCompilationFinished</c> never reached us. The errors
    /// are sitting in the Console the whole time — "details pending" while the user is looking at the exact error
    /// two panels down is a bad answer, and telling them to trigger a recompile is asking them to do the editor's
    /// bookkeeping (2026-08-12, Convai SDK on 6000.5).
    ///
    /// Everything here goes through reflection into <c>UnityEditor.LogEntries</c> / <c>UnityEditor.LogEntry</c>,
    /// which are internal: any missing member degrades to "no harvest" and the pending finding, never an exception.
    /// The Console is held locked between StartGettingEntries and EndGettingEntries, so the loop stays minimal.
    /// </summary>
    internal static class ConsoleCompileErrorHarvest
    {
        // "Assets\Foo\Bar.cs(20,20): error CS0619: 'X' is obsolete: '…'" — Roslyn's message shape, always English.
        // Matching the head line (rather than trusting a mode bit) keeps this independent of internal enum values,
        // and excludes warnings by construction: `error` is part of the pattern.
        private static readonly Regex CompileErrorHead =
            new Regex(@"^\s*(?<file>.+?)\((?<line>\d+),\d+\):\s*error\s+CS\d+", RegexOptions.Compiled);

        private const int MaxEntriesScanned = 2000;   // a spammed Console must not turn a scan into a crawl

        /// <summary>
        /// Pure parse of one Console entry (unit-testable; no editor state). Returns null when the entry is not a
        /// C# compiler error — runtime exceptions, Debug.Log output and asset-import errors all land in the same
        /// Console. The entry's own file/line win when populated; otherwise they come from the message head.
        /// </summary>
        internal static CollectedError ParseConsoleEntry(string message, string entryFile, int entryLine)
        {
            if (string.IsNullOrEmpty(message)) return null;

            int nl = message.IndexOf('\n');
            string head = (nl >= 0 ? message.Substring(0, nl) : message).TrimEnd('\r');

            var m = CompileErrorHead.Match(head);
            if (!m.Success) return null;

            string file = !string.IsNullOrEmpty(entryFile) ? entryFile : m.Groups["file"].Value;
            if (string.IsNullOrEmpty(file)) return null;

            int line = entryLine > 0
                ? entryLine
                : int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);

            return new CollectedError { file = file.Replace('\\', '/'), line = line, message = head.Trim() };
        }

        /// <summary>
        /// Reads the Console and returns the C# compiler errors in it, deduplicated by file+line+message.
        /// False when the internal API is unavailable or anything at all goes wrong — the caller then keeps
        /// whatever it had (in practice: the "details pending" finding).
        /// </summary>
        internal static bool TryHarvest(out List<CollectedError> errors)
        {
            errors = null;
            try
            {
                var asm = typeof(UnityEditor.Editor).Assembly;
                var entriesType = asm.GetType("UnityEditor.LogEntries");
                var entryType = asm.GetType("UnityEditor.LogEntry");
                if (entriesType == null || entryType == null) return false;

                const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var start = entriesType.GetMethod("StartGettingEntries", Statics);
                var end = entriesType.GetMethod("EndGettingEntries", Statics);
                var getEntry = entriesType.GetMethod("GetEntryInternal", Statics, null, new[] { typeof(int), entryType }, null);
                var fMessage = entryType.GetField("message");
                var fFile = entryType.GetField("file");
                var fLine = entryType.GetField("line");
                if (start == null || end == null || getEntry == null || fMessage == null) return false;

                var entry = Activator.CreateInstance(entryType);
                var found = new List<CollectedError>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                int count = Convert.ToInt32(start.Invoke(null, null));
                try
                {
                    int scanned = Math.Min(count, MaxEntriesScanned);
                    for (int i = 0; i < scanned; i++)
                    {
                        getEntry.Invoke(null, new[] { i, entry });
                        var e = ParseConsoleEntry(
                            fMessage.GetValue(entry) as string,
                            fFile?.GetValue(entry) as string,
                            fLine != null ? Convert.ToInt32(fLine.GetValue(entry)) : 0);
                        if (e == null) continue;
                        if (seen.Add(e.file + ":" + e.line + ":" + e.message)) found.Add(e);
                    }
                }
                finally { end.Invoke(null, null); }

                if (found.Count == 0) return false;
                errors = found;
                return true;
            }
            catch
            {
                return false;   // internal API drift must never break a scan
            }
        }
    }
}
