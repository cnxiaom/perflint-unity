using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace PerfLint.L10n
{
    public enum Lang
    {
        English = 0,
        Chinese = 1
    }

    /// <summary>
    /// Lightweight localization. Uses call-site inline <c>L.Tr("English text", "中文")</c> — no centralized
    /// key table to maintain, keeping incremental migration cost minimal for a solo project. English is the
    /// default; the user picks the language under Tools ▸ PerfLint ▸ Language (see <see cref="PerfLintLanguageMenu"/>).
    ///
    /// <para><b>Every translation must stay lazy.</b> <see cref="Tr"/> reads <see cref="Current"/> at call time, so
    /// copy evaluated per-render or per-finding follows a switch for free. A <c>static readonly</c> field that calls
    /// <c>L.Tr</c> in its initializer does not: it bakes whatever language was current at type load and never
    /// re-evaluates, which is how Chinese used to leak into an English UI. Static tables therefore hold
    /// <c>Func&lt;string&gt;</c>, not <c>string</c> — see <c>MigrationScanner.ApiRule</c>, <c>PackagesScanner.ModuleSig</c>
    /// and <c>MigrateRecipe.Summary</c>. <c>LazyTranslationTests</c> guards the rule against regressions.</para>
    /// </summary>
    public static class L
    {
        private const string Key = "PerfLint.Lang";

        /// <summary>
        /// The UI language, persisted in EditorPrefs. That store is machine-global rather than per-project, so one
        /// choice covers every project this editor opens — and, now that the menu exists, someone who lands in a
        /// language they can't read always has a way back. (Before the menu, release deliberately ignored this key:
        /// a dev-only switch could strand a release editor in Chinese with no way to flip it.)
        /// </summary>
        public static Lang Current
        {
            get => (Lang)EditorPrefs.GetInt(Key, (int)Lang.English);
            set
            {
                if (value == Current) return; // don't rebuild windows / re-notify for a no-op re-selection
                EditorPrefs.SetInt(Key, (int)value);
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Raised after <see cref="Current"/> actually changes. Static, so it is cleared by every domain reload —
        /// subscribers must re-register from <c>[InitializeOnLoad]</c> rather than from a window's lifecycle, or the
        /// refresh silently stops happening after the next recompile.
        /// </summary>
        public static event Action Changed;

        public static string Tr(string en, string zh) => Current == Lang.Chinese ? zh : en;

        /// <summary>
        /// Dev-only inline language switch injector, kept for local debugging: it puts an EN/中 dropdown straight in
        /// the panel being eyeballed, which beats a trip to the menu bar. Release: null — the menu is the shipped
        /// entry point. Set ONLY by the never-shipped <c>PerfLintL10nDev.cs</c> (PERFLINT_DEV + export-ignore),
        /// mirroring <see cref="Licensing.LicenseService.DevUnlockHook"/>; this is why the panels carry no
        /// PERFLINT_DEV compile branch and just call <see cref="InjectDevLangSwitch"/>, a no-op in release.
        /// </summary>
        /// <remarks>(parent, onChanged) — the impl appends an EN/中 control to <c>parent</c> and calls <c>onChanged</c> after a flip so the panel rebuilds in the new language.</remarks>
        internal static Action<VisualElement, Action> DevLangSwitchInjector;

        /// <summary>No-op in release; in a PERFLINT_DEV editor it adds an EN/中 switch to <paramref name="parent"/> that flips <see cref="Current"/> and calls <paramref name="onChanged"/>.</summary>
        public static void InjectDevLangSwitch(VisualElement parent, Action onChanged)
            => DevLangSwitchInjector?.Invoke(parent, onChanged);
    }
}
