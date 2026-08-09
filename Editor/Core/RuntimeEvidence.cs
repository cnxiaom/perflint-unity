using System;
using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// Measured runtime numbers, pre-formatted for a report.
    ///
    /// Deliberately plain, pre-rendered strings rather than the live sampling types: <see cref="HtmlReport"/> is a
    /// pure formatter in Core with no Unity API use (that is what makes it unit-testable), and the sampling types
    /// live in PerfLint.Runtime, which already depends on Core. Passing this instead keeps the dependency pointing
    /// one way — Runtime knows how to describe itself, Core only lays it out.
    ///
    /// What this turns the export into is the point: a scan report is a list of complaints, which nobody shares.
    /// A report carrying "this is what the game actually did, measured locally" is evidence.
    /// </summary>
    public sealed class RuntimeEvidence
    {
        public readonly struct Row
        {
            public readonly string Label;
            public readonly string Value;
            public Row(string label, string value) { Label = label; Value = value; }
        }

        /// <summary>Local-time string for when the sample was taken. Formatted by the caller so Core stays culture-agnostic.</summary>
        public string CapturedAtLocal { get; }
        public double DurationSeconds { get; }
        public int FrameCount { get; }
        /// <summary>Scene(s) that were loaded during sampling — a measurement only describes the scene it was taken in.</summary>
        public string Scenes { get; }
        /// <summary>
        /// Set when Deep Profile was active. Such a session inflates main-thread time several-fold; the report must
        /// carry that caveat rather than present the numbers as a frame rate.
        /// </summary>
        public bool WasDeepProfile { get; }
        public IReadOnlyList<Row> Rows { get; }

        public RuntimeEvidence(string capturedAtLocal, double durationSeconds, int frameCount,
            string scenes, bool wasDeepProfile, IReadOnlyList<Row> rows)
        {
            CapturedAtLocal = capturedAtLocal ?? "";
            DurationSeconds = durationSeconds;
            FrameCount = frameCount;
            Scenes = scenes ?? "";
            WasDeepProfile = wasDeepProfile;
            Rows = rows ?? Array.Empty<Row>();
        }

        public bool HasRows => Rows.Count > 0;
    }
}
