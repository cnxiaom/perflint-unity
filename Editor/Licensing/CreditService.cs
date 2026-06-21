using System;
using PerfLint.L10n;
using UnityEditor;

namespace PerfLint.Licensing
{
    /// <summary>
    /// Client-side mirror of the "credits quota" for the hosted LLM proxy. **The authoritative value lives on the server**
    /// (/llm decrements per call, pooled per license month / free daily pool);
    /// this class only caches the remaining balance returned via response headers, for UI display
    /// and "soft-block + upgrade prompt when exhausted".
    ///
    /// Note: calls that use a BYO key (ByoKey mode) bypass the proxy entirely and do not consume credits,
    /// so they are unrelated to this service —
    /// see the branch in <see cref="Entitlements.RequireAiCredit"/>.
    /// </summary>
    public static class CreditService
    {
        private const string KRemaining = "PerfLint.Credits.Remaining"; // remaining calls for the current period (cached)
        private const string KReset = "PerfLint.Credits.Reset";         // reset timestamp (ISO, provided by the server)
        private const string KKnown = "PerfLint.Credits.Known";         // whether a balance has ever been received from the server

        /// <summary>Fired whenever the remaining balance changes, so the UI can refresh its display.</summary>
        public static event Action Changed;

        public static bool Known => EditorPrefs.GetBool(KKnown, false);
        public static int Remaining => EditorPrefs.GetInt(KRemaining, -1);
        public static string ResetAt => EditorPrefs.GetString(KReset, "");

        /// <summary>
        /// Whether the hosted quota is exhausted (known and ≤ 0). Returns false when unknown — optimistic pass-through,
        /// with the server's 429 as the final backstop, to avoid incorrectly blocking new users who have never made a call.
        /// </summary>
        public static bool HostedExhausted
        {
            get
            {
                // Local dev unlock (null in release) also makes credits unlimited. No PERFLINT_DEV branch ships here.
                if (LicenseService.DevUnlockHook != null && LicenseService.DevUnlockHook()) return false;
                return Known && Remaining <= 0;
            }
        }

        /// <summary>Syncs the remaining balance from /llm response headers (X-PerfLint-Credits-Remaining / -Reset).</summary>
        public static void UpdateFromHeaders(string remainingHeader, string resetHeader)
        {
            if (!int.TryParse(remainingHeader, out var rem)) return;
            EditorPrefs.SetInt(KRemaining, rem < 0 ? 0 : rem);
            EditorPrefs.SetBool(KKnown, true);
            if (!string.IsNullOrEmpty(resetHeader)) EditorPrefs.SetString(KReset, resetHeader);
            Raise();
        }

        /// <summary>Forces the balance to 0 when the server explicitly returns 429 (quota exhausted).</summary>
        public static void MarkExhausted(string resetHeader)
        {
            EditorPrefs.SetInt(KRemaining, 0);
            EditorPrefs.SetBool(KKnown, true);
            if (!string.IsNullOrEmpty(resetHeader)) EditorPrefs.SetString(KReset, resetHeader);
            Raise();
        }

        /// <summary>Single-line balance label for use in the UI.</summary>
        public static string RemainingText()
        {
            if (!Known) return L.Tr("AI credits: ready (zero-config)", "AI 额度：就绪（零配置）");
            string reset = string.IsNullOrEmpty(ResetAt) ? "" : " · " + L.Tr("resets ", "重置于 ") + LicenseService.FormatExpiryLocal(ResetAt);
            return L.Tr("AI credits left this period: ", "本期 AI 剩余额度：") + Remaining + reset;
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { /* UI callback exceptions must not affect billing logic */ }
        }

        /// <summary>Clears the locally cached balance (returns to the "unknown" state). Intended for tests and license-deactivation resets.</summary>
        internal static void ResetCache()
        {
            EditorPrefs.DeleteKey(KRemaining);
            EditorPrefs.DeleteKey(KReset);
            EditorPrefs.DeleteKey(KKnown);
            Raise();
        }
    }
}
