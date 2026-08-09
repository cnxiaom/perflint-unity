using System;
using System.Reflection;
using PerfLint.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.L10n
{
    /// <summary>
    /// Tools ▸ PerfLint ▸ Language — the shipped English/中文 switch. The bilingual copy has been in place since
    /// 0.19.0; until now only a PERFLINT_DEV editor could reach it (see <see cref="L.DevLangSwitchInjector"/>),
    /// because a switch with no way back would have stranded a release editor in a language its user can't read:
    /// the preference is machine-global (EditorPrefs), so it follows the editor into every project it opens.
    /// A menu item is exactly the way back that was missing, so the preference is now honored in release.
    ///
    /// Two things this has to get right beyond writing the pref:
    /// <list type="bullet">
    /// <item>Open windows must repaint in the new language — a switch that appears to do nothing reads as broken.</item>
    /// <item>Findings already on disk keep the wording they were generated with, because scanners bake their copy
    /// into each <see cref="Finding"/> at scan time. That's stated out loud rather than left for the user to
    /// discover, and only when there actually is a stored result to be stale.</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfLintLanguageMenu
    {
        private const string English = "Tools/PerfLint/Language/English";

        // The ASCII tail is deliberate: an editor whose menu font lacks CJK glyphs would otherwise draw this entry as
        // boxes — and this is the one menu entry a user in the wrong language has to be able to identify.
        private const string Chinese = "Tools/PerfLint/Language/中文 (Chinese)";

        // Sits with "Getting Started" (2000) rather than with the working tools: same group, no separator.
        private const int MenuPriority = 2001;

        static PerfLintLanguageMenu()
        {
            // Registered from [InitializeOnLoad], not from a window: the event is static and therefore cleared by
            // every domain reload, and the windows that need refreshing may not have been built yet. Hooking it here
            // also means the dev-only inline dropdown gets the same refresh as the menu, rather than rebuilding only
            // the one panel it lives in.
            L.Changed -= OnLanguageChanged;
            L.Changed += OnLanguageChanged;
        }

        [MenuItem(English, priority = MenuPriority)]
        private static void SelectEnglish() => L.Current = Lang.English;

        [MenuItem(English, validate = true)]
        private static bool ValidateEnglish()
        {
            Menu.SetChecked(English, L.Current == Lang.English);
            return true;
        }

        [MenuItem(Chinese, priority = MenuPriority + 1)]
        private static void SelectChinese() => L.Current = Lang.Chinese;

        [MenuItem(Chinese, validate = true)]
        private static bool ValidateChinese()
        {
            Menu.SetChecked(Chinese, L.Current == Lang.Chinese);
            return true;
        }

        private static void OnLanguageChanged()
        {
            // Nothing here applies to a headless run: there are no panels to repaint, and a modal dialog would block
            // an unattended batchmode/CI invocation forever. The preference itself is already written either way.
            if (Application.isBatchMode) return;

            RebuildOpenWindows();
            NoteThatStoredFindingsKeepTheirLanguage();
        }

        /// <summary>
        /// Rebuilds every open PerfLint window so its labels re-evaluate. UIElements panels are rebuilt through
        /// <c>CreateGUI</c>; IMGUI windows need nothing but a repaint, since <c>OnGUI</c> re-runs every
        /// <c>L.Tr</c> on the next frame anyway.
        /// </summary>
        private static void RebuildOpenWindows()
        {
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null) continue;

                Type type = window.GetType();
                if (type.Namespace == null || !type.Namespace.StartsWith("PerfLint", StringComparison.Ordinal)) continue;

                try { RebuildOne(window, type); }
                catch (Exception e)
                {
                    // One window that can't rebuild must not strand the rest in the old language.
                    Debug.LogError($"PerfLint: could not rebuild {type.Name} in the new language — close and reopen it. {e}");
                }
            }
        }

        private static void RebuildOne(EditorWindow window, Type type)
        {
            MethodInfo createGui = type.GetMethod("CreateGUI",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (createGui == null) { window.Repaint(); return; } // IMGUI window: OnGUI re-reads L.Tr on its own

            // A window whose tab has never been shown has no UI yet — CreateGUI runs when it first becomes visible
            // and will pick up the new language by itself. Building it here would construct a panel nobody is
            // looking at, on a code path (hidden tab, post-domain-reload) this repo has been bitten by before.
            VisualElement root = window.rootVisualElement;
            if (root == null || root.childCount == 0) return;

            root.Clear(); // CreateGUI appends rather than clears; without this the panel stacks a second copy of itself
            createGui.Invoke(window, null);
            window.Repaint();
        }

        /// <summary>
        /// Findings carry the wording they were generated with — scanners resolve their copy through <c>L.Tr</c> at
        /// scan time and store the resulting strings — so a stored result stays in the previous language until it is
        /// re-scanned. Said once, in the language just selected, and gated on a stored result actually existing:
        /// telling someone their results are stale when they have never scanned would be noise.
        /// </summary>
        private static void NoteThatStoredFindingsKeepTheirLanguage()
        {
            if (!ScanResultStore.Exists()) return;

            EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Language changed", "PerfLint — 已切换语言"),
                L.Tr(
                    "The interface is now in English.\n\nResults from your last scan keep the wording they were written with — each finding stores its own text. Scan again to regenerate them in English.",
                    "界面已切换为中文。\n\n上次扫描的结果仍保持生成时的语言 —— 每条 finding 的正文在扫描时就已写定。重新扫描即可用中文重新生成。"),
                L.Tr("Got it", "知道了"));
        }
    }
}
