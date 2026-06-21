using System;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Licensing
{
    /// <summary>
    /// License orchestration: activate / validate / deactivate, and derive <see cref="IsPro"/> from the local cache (including offline grace period).
    /// Key distinction: when the network is unreachable, the cache is preserved (still Pro within the grace period); only a definitive server rejection (4xx / status not active) falls back to Free.
    /// </summary>
    [InitializeOnLoad]
    public static class LicenseService
    {
        /// <summary>Fired after a license state change (activation / deactivation / re-validation) so the UI can refresh gating.</summary>
        public static event Action Changed;

        static LicenseService()
        {
            // On editor startup, perform one "re-validate if stale" pass in the background without interrupting the user.
            EditorApplication.delayCall += () => MaybeRevalidate();
        }

        /// <summary>
        /// Local-only dev unlock hook. Registered at editor load by PerfLintLicenseDev (a file gated by PERFLINT_DEV
        /// AND excluded from the published package via .gitattributes export-ignore), so it is null in the shipped
        /// package and the published source contains no PERFLINT_DEV branch at all — a user adding the PERFLINT_DEV
        /// define would activate nothing. Setting this internal field from outside requires reflection (crack-level),
        /// which is the accepted bar; the goal is only to remove the trivial define-based bypass.
        /// </summary>
        internal static Func<bool> DevUnlockHook;

        /// <summary>Lets the dev unlock file (same assembly) fire the Changed event to refresh gating UI after toggling.</summary>
        internal static void RaiseChanged() => Raise();

        /// <summary>Whether the current session has Pro entitlement (the sole criterion for feature gating).</summary>
        public static bool IsPro
        {
            get
            {
                if (DevUnlockHook != null && DevUnlockHook()) return true; // null in release; only the local dev file sets it
                if (string.IsNullOrEmpty(LicenseSettings.Key)) return false;
                if (LicenseSettings.Status != "active") return false;
                if (IsExpired()) return false;
                // Offline grace: if more than GraceDays have elapsed since the last successful validation, force fallback to Free to prevent permanent offline bypass.
                if ((DateTime.UtcNow - LicenseSettings.LastValidatedUtc).TotalDays > LicenseSettings.GraceDays)
                    return false;
                return true;
            }
        }

        /// <summary>Human-readable status line for display in the UI.</summary>
        public static string StatusLine()
        {
            if (string.IsNullOrEmpty(LicenseSettings.Key)) return "Free";
            if (IsPro)
            {
                string exp = string.IsNullOrEmpty(LicenseSettings.ExpiresAt)
                    ? L.Tr("(perpetual / lifetime)", "（买断 / 永久）")
                    : L.Tr("(valid until ", "（有效期至 ") + FormatExpiryLocal(LicenseSettings.ExpiresAt) + L.Tr(")", "）");
                return "Pro " + exp;
            }
            if (IsExpired()) return L.Tr("Expired (please renew)", "已过期（请续费）");
            if (LicenseSettings.Status == "active") return L.Tr("Pro (offline grace expired, please reconnect to re-check)", "Pro（离线宽限已过，请联网复验）");
            return L.Tr("Invalid / deactivated", "无效 / 已停用");
        }

        /// <summary>
        /// Formats an ISO-8601 (UTC) expiry timestamp into a friendly, machine-local-timezone string
        /// (e.g. "2026-07-20 13:35") so users see their own time, not a raw "...T05:35:11.495Z".
        /// Falls back to the raw string if it can't be parsed — we never hide the information.
        /// </summary>
        internal static string FormatExpiryLocal(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return iso;
            if (DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dto))
                return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso;
        }

        private static bool IsExpired()
        {
            var raw = LicenseSettings.ExpiresAt;
            if (string.IsNullOrEmpty(raw)) return false; // perpetual / lifetime purchase — no expiry date
            return DateTime.TryParse(raw, null,
                       System.Globalization.DateTimeStyles.AdjustToUniversal, out var exp)
                   && exp < DateTime.UtcNow;
        }

        // ── Activate ────────────────────────────────────────
        public static void Activate(string key, Action<bool, string> onDone)
        {
            key = (key ?? "").Trim();
            if (string.IsNullOrEmpty(key)) { onDone(false, L.Tr("Please enter a license key.", "请输入许可证密钥。")); return; }

            CreemLicenseClient.Activate(key, InstanceName(), resp =>
            {
                if (resp.Ok && resp.Status == "active")
                {
                    LicenseSettings.Key = key;
                    LicenseSettings.InstanceId = resp.InstanceId;
                    LicenseSettings.Status = resp.Status;
                    LicenseSettings.ProductId = resp.ProductId;
                    LicenseSettings.ExpiresAt = resp.ExpiresAt ?? "";
                    LicenseSettings.LastValidatedUtc = DateTime.UtcNow;
                    Raise();
                    onDone(true, L.Tr("Activated successfully.", "激活成功。"));
                }
                else if (resp.Ok)
                {
                    onDone(false, L.Tr("This license status is ", "该许可证状态为 ") + resp.Status + L.Tr(", cannot activate (it may be expired or deactivated).", "，无法激活（可能已过期或被停用）。"));
                }
                else if (resp.ServerReached)
                {
                    onDone(false, L.Tr("Activation failed: ", "激活失败：") + resp.Error);
                }
                else
                {
                    onDone(false, L.Tr("Cannot reach the license service. Check your network and try again.", "无法连接许可证服务，请检查网络后重试。"));
                }
            });
        }

        // ── Validate (manual or background) ────────────────────────────
        public static void Validate(Action<bool, string> onDone)
        {
            string key = LicenseSettings.Key, inst = LicenseSettings.InstanceId;
            if (string.IsNullOrEmpty(key)) { onDone?.Invoke(false, L.Tr("Not activated yet.", "尚未激活。")); return; }

            CreemLicenseClient.Validate(key, inst, resp =>
            {
                if (resp.Ok)
                {
                    // Server is authoritative: update the status (active → extend; non-active → naturally fall back to Free).
                    LicenseSettings.Status = resp.Status;
                    LicenseSettings.ProductId = resp.ProductId;
                    LicenseSettings.ExpiresAt = resp.ExpiresAt ?? "";
                    LicenseSettings.LastValidatedUtc = DateTime.UtcNow;
                    Raise();
                    onDone?.Invoke(IsPro, IsPro ? L.Tr("Verified.", "校验通过。") : L.Tr("License is no longer valid: ", "许可证已失效：") + resp.Status);
                }
                else if (resp.ServerReached && IsDefinitiveInvalid(resp.HttpCode))
                {
                    // Key does not exist / has been revoked / instance is invalid: definitively fall back to Free.
                    LicenseSettings.Status = "inactive";
                    Raise();
                    onDone?.Invoke(false, L.Tr("License is invalid or revoked; reverted to Free.", "许可证无效或已撤销，已回落 Free。"));
                }
                else
                {
                    // Network failure or transient error (5xx/429): preserve the cache; still Pro within the grace period.
                    onDone?.Invoke(IsPro, L.Tr("Temporarily unable to verify (network/service hiccup); ", "暂时无法校验（网络/服务波动），") + (IsPro ? L.Tr("still usable within the grace period.", "宽限期内继续可用。") : L.Tr("please try again later.", "请稍后重试。")));
                }
            });
        }

        /// <summary>Silently re-validates in the background when the cache is stale (no popups, no interruption).</summary>
        public static void MaybeRevalidate()
        {
            if (string.IsNullOrEmpty(LicenseSettings.Key)) return;
            if ((DateTime.UtcNow - LicenseSettings.LastValidatedUtc).TotalDays < LicenseSettings.RevalidateAfterDays)
                return;
            Validate(null);
        }

        // ── Deactivate ────────────────────────────────────────
        public static void Deactivate(Action<bool, string> onDone)
        {
            string key = LicenseSettings.Key, inst = LicenseSettings.InstanceId;
            if (string.IsNullOrEmpty(key)) { LicenseSettings.Clear(); Raise(); onDone(true, L.Tr("Cleared.", "已清除。")); return; }

            CreemLicenseClient.Deactivate(key, inst, resp =>
            {
                // Regardless of the server result, always clear local state (the user's intent is to deactivate on this machine); a network failure merely shows a notice.
                LicenseSettings.Clear();
                Raise();
                if (resp.Ok || resp.ServerReached) onDone(true, L.Tr("Deactivated on this machine.", "已在本机停用。"));
                else onDone(true, L.Tr("Cleared the local license (could not notify the server; the remote instance will be reclaimed later).", "已清除本机许可证（未能通知服务器，远端实例可稍后自行回收）。"));
            });
        }

        // 400/401/403/404/410 are treated as definitively invalid; 0/408/429/5xx are treated as transient — preserve the grace period.
        private static bool IsDefinitiveInvalid(long code)
            => code == 400 || code == 401 || code == 403 || code == 404 || code == 410;

        private static string InstanceName()
        {
            string dev = SystemInfo.deviceName;
            return string.IsNullOrEmpty(dev) ? "unity-editor" : dev;
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { /* UI callback exceptions must not affect licensing logic */ }
        }
    }
}
