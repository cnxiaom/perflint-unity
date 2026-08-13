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

        /// <summary>
        /// Asset Store listing for the one-time-purchase Pro package. <b>Empty, and expected to stay
        /// empty</b> — as of 2026-08-12 the Asset Store carries the free package only, as a funnel, and
        /// there is no Pro package listed there to link to. Kept as the higher-priority half of
        /// <see cref="BuyUrl"/> so that reversing that route later is a one-line change.
        /// </summary>
        public const string AssetStoreProUrl = "";

        /// <summary>
        /// Where Pro is actually sold: a subscription on perflint.dev. This is the whole point of the free
        /// package being on the Asset Store — it is a funnel, and a funnel with no outlet is just a free
        /// tool. <b>Leaving this empty is not the safe choice</b>, it is the one that silently breaks the
        /// business: the license panel goes blank and two Pipeline commands emit "Upgrade: " with nothing
        /// after it.
        ///
        /// The site only serves this page while <c>STORE_MODE !== 'off'</c> (site/src/consts.ts) — under
        /// 'off' the route is not emitted at all, so this would 404. That is acceptable rather than
        /// gated-on-live-state, because the two facts move together by construction: 'off' means Pro
        /// cannot be bought anywhere, which is a bigger problem than a stale link in the panel. If the
        /// site ever goes back to 'off', empty this too and ship it.
        /// </summary>
        public const string SiteProUrl = "https://perflint.dev/pricing/";

        /// <summary>
        /// Where to send someone who wants Pro — <b>or empty when there is nowhere to send them</b>.
        ///
        /// Two channels, in priority order, because they can both be true at once and the store one wins
        /// when it exists (a buyer already inside the Asset Store should not be sent out to a subscription
        /// page). Today only the second is live: the store carries the free package as a funnel, Pro is a
        /// subscription on perflint.dev.
        ///
        /// Still nullable by construction, and callers must still check <see cref="CanBuy"/> rather than
        /// emit a dead link — both halves can be empty, and were between 2026-08-11 and 08-12.
        /// </summary>
        public static string BuyUrl => !string.IsNullOrEmpty(AssetStoreProUrl) ? AssetStoreProUrl : SiteProUrl;

        /// <summary>Whether a purchase destination exists at all. Gate every buy button, link and upgrade URL on this.</summary>
        public static bool CanBuy => !string.IsNullOrEmpty(BuyUrl);

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
        /// <b>False, and expected to stay false</b> (route settled 2026-08-12): the Asset Store carries the
        /// FREE package only, as a funnel, and Pro is a subscription sold on perflint.dev. No Pro package is
        /// listed there, so nobody can be holding an Asset Store invoice for one, and offering to activate
        /// with an invoice number would be asking for a credential that cannot exist. This supersedes the
        /// 2026-08-11 plan of submitting a $59.99 buyout package alongside the free one.
        ///
        /// The invoice-verification path behind it is built and tested end to end (worker → Unity's
        /// publisher/v1/invoice/verify.json, seats in KV) and is deliberately left in place rather than
        /// deleted — it costs nothing dormant, and reversing the route later should not mean rebuilding it.
        /// <c>tools/build-asset-store-package.sh --edition pro</c> flips this line and asserts the rewrite
        /// landed; that switch has no current use, and <c>--edition free</c> is the only edition shipped.
        ///
        /// <c>static readonly</c> rather than <c>const</c> so flipping it never turns call sites into
        /// "unreachable code" warnings.
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
