using System.Collections.Generic;

namespace PerfLint.Capture
{
    /// <summary>
    /// Pure accumulator behind the in-player recorder: dedupes captured variants and tracks which ones haven't been
    /// streamed to the editor yet. No Unity API — unit-testable in the editor, thread-agnostic (the recorder locks
    /// around every call because log messages arrive on worker threads).
    ///
    /// "Pending" holds everything not yet successfully sent — including records seeded from a previous session's
    /// capture file — so the first flush after an editor attaches delivers the full cumulative set, not just the
    /// variants that happened to compile after the connection came up.
    /// </summary>
    internal sealed class VariantCaptureSession
    {
        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly List<VariantRecord> _all = new List<VariantRecord>();
        private readonly List<VariantRecord> _pending = new List<VariantRecord>();

        public int Count => _all.Count;
        public int PendingCount => _pending.Count;

        /// <summary>Add a record; true when it was new (false for duplicates and blank shader names).</summary>
        public bool Add(VariantRecord r)
        {
            if (r == null || string.IsNullOrEmpty(r.shader)) return false;
            if (!_seen.Add(r.Key())) return false;
            _all.Add(r);
            _pending.Add(r);
            return true;
        }

        /// <summary>Take (and clear) the not-yet-sent batch.</summary>
        public List<VariantRecord> DrainPending()
        {
            var copy = new List<VariantRecord>(_pending);
            _pending.Clear();
            return copy;
        }

        /// <summary>Put a drained batch back after a failed send so it rides the next flush (records stay deduped).</summary>
        public void Requeue(List<VariantRecord> batch)
        {
            if (batch != null) _pending.AddRange(batch);
        }

        /// <summary>Everything captured so far — the cumulative file write.</summary>
        public List<VariantRecord> Snapshot() => new List<VariantRecord>(_all);
    }
}
