using UnityEditor;

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

        // Default to English. Chinese stays reachable via EditorPrefs (internal/dev), but it's neither
        // auto-detected from the system language nor switchable from the scan UI.
        public static Lang Current
        {
            get => (Lang)EditorPrefs.GetInt(Key, (int)Lang.English);
            set => EditorPrefs.SetInt(Key, (int)value);
        }

        public static string Tr(string en, string zh) => Current == Lang.Chinese ? zh : en;
    }
}
