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
        private const string KProvider = "PerfLint.License.Provider";        // billing channel that issued the entitlement

        /// <summary>
        /// <see cref="Provider"/> value for an Asset Store one-time purchase (activated with an invoice
        /// number). Named rather than inlined because the distinction drives real behaviour: a buyout is
        /// perpetual, so it must not lapse just because the machine was offline past the grace window.
        /// </summary>
        public const string ProviderUnity = "unity";

        // ── Configurable constants (fill in before release based on your deployment) ─────────────────────
        /// <summary>License validation + hosted LLM proxy (Cloudflare Worker, custom domain api.perflint.dev). Can be overridden by the user in advanced settings.</summary>
        public const string DefaultEndpoint = "https://api.perflint.dev";

        /// <summary>Purchase/upgrade landing page (landing page pricing section, then redirects to Creem checkout).</summary>
        public const string BuyUrl = "https://perflint.dev/#pricing";

        /// <summary>
        /// Asset Store listing for the one-time-purchase Pro package. <b>Empty until that package is
        /// actually published</b> — the UI gates the "Buy on the Asset Store" button on this being
        /// non-empty, so we never ship a button that leads nowhere (see CLAUDE.md: cross-references must
        /// be gated on the thing existing, not written as a wish).
        /// </summary>
        public const string AssetStoreProUrl = "";

        /// <summary>
        /// Whether the one-time Asset Store purchase exists as a product — i.e. whether an invoice number
        /// is something a user could actually be holding. Gates the wording of the activation field.
        ///
        /// <b>Deliberately separate from <see cref="AssetStoreProUrl"/></b>, because the two facts become
        /// true at different moments and conflating them ships a broken package: the build that Pro buyers
        /// download is submitted <i>before</i> its own listing URL exists, so a single switch keyed on the
        /// URL would leave every buyer staring at a field labelled "License key" with an invoice number in
        /// hand and nowhere to put it. Someone who has already paid needs to be told where to type it —
        /// not offered a button selling them what they just bought.
        ///
        /// Flip to true in the build submitted as the Pro package; fill in the URL later, once the listing
        /// is live. <c>static readonly</c> rather than <c>const</c> so flipping it never turns call sites
        /// into "unreachable code" warnings.
        /// </summary>
        public static readonly bool AssetStoreBuyoutAvailable = false;

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

        /// <summary>
        /// Billing channel that issued the current entitlement: "creem" / "dodo" / <see cref="ProviderUnity"/>.
        /// Empty when unknown (activated against an older proxy, or never activated).
        /// </summary>
        public static string Provider
        {
            get => EditorPrefs.GetString(KProvider, "");
            set => EditorPrefs.SetString(KProvider, value ?? "");
        }

        /// <summary>True when this machine's entitlement is a perpetual Asset Store buyout rather than a subscription.</summary>
        public static bool IsPerpetualBuyout => Provider == ProviderUnity;

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
            EditorPrefs.DeleteKey(KProvider);
        }
    }
}
