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
    /// lose its one-click fixes on reload. The window registers <see cref="WindowRefresh"/> while open, and that hook
    /// answers whether it TOOK the work — being open is not the same as owning a live result, and reading the one as
    /// the other left the store stale whenever the panel sat in a hidden tab.
    /// </summary>
    [InitializeOnLoad]
    public static class PerfLintAutoRescan
    {
        /// <summary>
        /// Set by the open window to its live incremental-refresh method (cleared to null on close). Non-null ⇒ a window
        /// is open and owns the live result; the background pump defers to it instead of touching the persisted store.
        /// </summary>
        /// <summary>
        /// Returns whether the window actually TOOK the work, not merely that a window exists.
        ///
        /// It used to be an Action, and "registered" was read as "handled". Registration happens in OnEnable, which
        /// runs after a domain reload whatever tab the user is on; the window's live result and its visual tree only
        /// exist after CreateGUI, which waits until the tab is SHOWN. So a main panel sitting in a hidden tab claimed
        /// the work, its incremental refresh returned early with nothing to refresh, and the pump had already given
        /// up its own path — leaving the pending queue undrained and the persisted baseline stale.
        ///
        /// Nothing is lost when it declines: the reason the pump defers at all is that overwriting the store from a
        /// Fix-less on-disk baseline would cost the window its one-click fixes, and a window with no live result has
        /// no fixes to lose. That is exactly the case that now falls through.
        ///
        /// The stale store is not only a redraw: MCP and the CLI read it whenever PerfLintLiveResult has no live
        /// provider, which is the same condition. So an agent driving a project whose main panel was open but not
        /// visible got answers from a baseline that had stopped catching up.
        /// </summary>
        public static Func<bool> WindowRefresh;

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

            // A window that OWNS the live result (with Fix instances) takes it; one that merely exists does not.
            var windowRefresh = WindowRefresh;
            if (windowRefresh != null)
            {
                try { if (windowRefresh()) return; }
                catch (Exception ex) { Debug.LogWarning("[PerfLint] " + L.Tr($"Incremental window refresh failed: {ex.Message}", $"增量刷新窗口失败：{ex.Message}")); }
            }

            // Nobody took it → update the persisted baseline directly so it stays live for the next open, and for
            // the agent reading it right now.
            if (!ScanResultStore.Exists()) { PerfLintPendingRescan.Consume(); return; } // no baseline to update → drop the queue
            var restored = ScanResultStore.Load();
            if (restored?.Result == null) return;

            var updated = PerfLintIncrementalRescan.Apply(restored.Result, out bool changed);
            if (changed) ScanResultStore.Save(updated);
        }
    }
}
