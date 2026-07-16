using System;
using UnityEditor;
using UnityEngine;
using PerfLint.L10n;

namespace PerfLint.Core
{
    /// <summary>
    /// Keeps the persisted report live after ASSET edits (texture/audio/material/… import-setting or content changes),
    /// which — unlike script edits — do NOT trigger a domain reload, so the window's on-reload consume path never runs
    /// for them. The change tracker calls <see cref="Notify"/> after recording changed paths; we debounce to the next
    /// editor tick (so the import has fully settled before we load assets) and then bring the baseline up to date.
    ///
    /// Authority split (load-bearing): the Fix/Action instances on a Finding are NOT serializable, so a report LOADED
    /// from disk has none. An OPEN window holds the richer live result (with Fix instances). Therefore:
    ///   · window open  → hand off to the window's live incremental refresh (keeps Fix instances, re-renders);
    ///   · window closed→ update the on-disk baseline directly (RescanFile the changed files, save).
    /// If we instead overwrote the store from a loaded (Fix-less) baseline while the window was open, the window would
    /// lose its one-click fixes on reload. The window registers <see cref="WindowRefresh"/> while open to signal both
    /// "a window is open" and "how to refresh it".
    /// </summary>
    [InitializeOnLoad]
    public static class PerfLintAutoRescan
    {
        /// <summary>
        /// Set by the open window to its live incremental-refresh method (cleared to null on close). Non-null ⇒ a window
        /// is open and owns the live result; the background pump defers to it instead of touching the persisted store.
        /// </summary>
        public static Action WindowRefresh;

        private static bool _scheduled;

        static PerfLintAutoRescan() { }

        /// <summary>Schedules a debounced incremental catch-up for the next editor tick. Safe to call repeatedly within one import batch — it coalesces to a single pump.</summary>
        public static void Notify()
        {
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += Pump;
        }

        private static void Pump()
        {
            _scheduled = false;

            // A window is open → it owns the live result (with Fix instances); let it consume pending + re-render.
            var windowRefresh = WindowRefresh;
            if (windowRefresh != null)
            {
                try { windowRefresh(); }
                catch (Exception ex) { Debug.LogWarning("[PerfLint] " + L.Tr($"Incremental window refresh failed: {ex.Message}", $"增量刷新窗口失败：{ex.Message}")); }
                return;
            }

            // No window open → update the persisted baseline directly so it stays live for the next open.
            if (!ScanResultStore.Exists()) { PerfLintPendingRescan.Consume(); return; } // no baseline to update → drop the queue
            var restored = ScanResultStore.Load();
            if (restored?.Result == null) return;

            var updated = PerfLintIncrementalRescan.Apply(restored.Result, out bool changed);
            if (changed) ScanResultStore.Save(updated);
        }
    }
}
