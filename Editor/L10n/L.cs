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
    /// key table to maintain, keeping incremental migration cost minimal for a solo project. The default
    /// language is English; the Chinese strings remain in place for internal use but are not auto-selected
    /// and the UI exposes no language switch.
    ///
    /// Current status: infrastructure + settings window are bilingual; remaining UI and per-scanner detail
    /// copy are still in Chinese and pending migration (see docs/progress-ledger.md backlog items).
    /// </summary>
    public static class L
    {
        private const string Key = "PerfLint.Lang";

        // Release ships English-only and IGNORES the persisted pref: the EditorPrefs key is machine-global
        // (cross-project), so once the dev switch wrote Chinese it would otherwise leak into a release/no-dev
        // editor with no UI to switch back. Only a PERFLINT_DEV editor — where the never-shipped dev file has set
        // DevLangSwitchInjector — honors the stored language. DevUnlockHook-style gating, no #if in shipped code.
        public static Lang Current
        {
            get => DevLangSwitchInjector != null
                ? (Lang)EditorPrefs.GetInt(Key, (int)Lang.English)
                : Lang.English;
            set => EditorPrefs.SetInt(Key, (int)value);
        }

        public static string Tr(string en, string zh) => Current == Lang.Chinese ? zh : en;

        /// <summary>
        /// Dev-only UI-language switch injector. Release: null — no switch is shown, so the UI stays English-only.
        /// Set ONLY by the never-shipped <c>PerfLintL10nDev.cs</c> (gated by PERFLINT_DEV + export-ignore), mirroring
        /// <see cref="Licensing.LicenseService.DevUnlockHook"/>. This is why the three panels carry no
        /// PERFLINT_DEV compile branch: they just call <see cref="InjectDevLangSwitch"/>, which is a no-op in release.
        /// </summary>
        /// <remarks>(parent, onChanged) — the impl appends an EN/中 control to <c>parent</c> and calls <c>onChanged</c> after a flip so the panel rebuilds in the new language.</remarks>
        internal static Action<VisualElement, Action> DevLangSwitchInjector;

        /// <summary>No-op in release; in a PERFLINT_DEV editor it adds an EN/中 switch to <paramref name="parent"/> that flips <see cref="Current"/> and calls <paramref name="onChanged"/>.</summary>
        public static void InjectDevLangSwitch(VisualElement parent, Action onChanged)
            => DevLangSwitchInjector?.Invoke(parent, onChanged);
    }
}
