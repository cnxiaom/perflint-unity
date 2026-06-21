using System;
using UnityEditor;

namespace PerfLint.Licensing
{
    /// <summary>
    /// Local license state (stored in EditorPrefs, per-machine, not version-controlled).
    ///
    /// Design: Creem's license validation/activation API requires the store **secret key** (x-api-key),
    /// which cannot be embedded in the client. Therefore the editor does not connect to Creem directly;
    /// instead it goes through a thin stateless proxy (see backend/creem-license-proxy).
    /// The proxy's only job is to inject the secret key and forward the Creem response;
    /// all caching / offline grace / gating logic lives in this client.
    /// </summary>
    public static class LicenseSettings
    {
        private const string KKey = "PerfLint.License.Key";
        private const string KInstance = "PerfLint.License.InstanceId";
        private const string KStatus = "PerfLint.License.Status";
        private const string KProduct = "PerfLint.License.ProductId";
        private const string KExpires = "PerfLint.License.ExpiresAt";       // ISO-8601; empty = perpetual (perpetual license)
        private const string KValidated = "PerfLint.License.LastValidated";  // UTC ticks
        private const string KEndpoint = "PerfLint.License.Endpoint";        // proxy endpoint override (advanced)

        // ── Configurable constants (fill in before release based on your deployment) ─────────────────────
        /// <summary>License validation + hosted LLM proxy (Cloudflare Worker, custom domain api.perflint.dev). Can be overridden by the user in advanced settings.</summary>
        public const string DefaultEndpoint = "https://api.perflint.dev";

        /// <summary>Purchase/upgrade landing page (landing page pricing section, then redirects to Creem checkout).</summary>
        public const string BuyUrl = "https://perflint.dev/#pricing";

        /// <summary>If this many days have passed without a successful re-validation, a background re-validation is attempted.</summary>
        public const double RevalidateAfterDays = 3;

        /// <summary>Offline grace period since the last successful validation; once exceeded, falls back to Free and forces a new online activation.</summary>
        public const double GraceDays = 14;

        public static string Key
        {
            get => EditorPrefs.GetString(KKey, "");
            set => EditorPrefs.SetString(KKey, value ?? "");
        }

        public static string InstanceId
        {
            get => EditorPrefs.GetString(KInstance, "");
            set => EditorPrefs.SetString(KInstance, value ?? "");
        }

        /// <summary>Creem license status: "active" / "inactive" / "expired" / "disabled", etc.</summary>
        public static string Status
        {
            get => EditorPrefs.GetString(KStatus, "");
            set => EditorPrefs.SetString(KStatus, value ?? "");
        }

        public static string ProductId
        {
            get => EditorPrefs.GetString(KProduct, "");
            set => EditorPrefs.SetString(KProduct, value ?? "");
        }

        public static string ExpiresAt
        {
            get => EditorPrefs.GetString(KExpires, "");
            set => EditorPrefs.SetString(KExpires, value ?? "");
        }

        public static DateTime LastValidatedUtc
        {
            get
            {
                long t = long.TryParse(EditorPrefs.GetString(KValidated, "0"), out var v) ? v : 0;
                return t > 0 ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;
            }
            set => EditorPrefs.SetString(KValidated, value.ToUniversalTime().Ticks.ToString());
        }

        public static string Endpoint
        {
            get
            {
                var s = EditorPrefs.GetString(KEndpoint, "");
                return string.IsNullOrEmpty(s) ? DefaultEndpoint : s;
            }
            set => EditorPrefs.SetString(KEndpoint, value ?? "");
        }

        /// <summary>Clears all local license cache (deactivation / sign-out).</summary>
        public static void Clear()
        {
            EditorPrefs.DeleteKey(KKey);
            EditorPrefs.DeleteKey(KInstance);
            EditorPrefs.DeleteKey(KStatus);
            EditorPrefs.DeleteKey(KProduct);
            EditorPrefs.DeleteKey(KExpires);
            EditorPrefs.DeleteKey(KValidated);
        }
    }
}
