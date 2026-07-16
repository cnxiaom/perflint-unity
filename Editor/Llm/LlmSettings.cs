using UnityEditor;

namespace PerfLint.Llm
{
    public enum LlmProvider
    {
        Anthropic = 0,   // Claude, native Messages API
        DeepSeek = 1      // OpenAI-compatible /chat/completions
    }

    /// <summary>
    /// LLM invocation mode.
    ///   Hosted: zero-config — no key required; requests go through our hosted proxy (→ deepseek-v4-flash)
    ///           using the license/instance for auth. We bear the cost, so this is subject to monthly/daily
    ///           credit quotas (anti-abuse). This is the default mode.
    ///   ByoKey: advanced — bring your own provider API key; the client connects directly to the provider
    ///           (never via our servers). The user pays their own token costs, so credits are not counted
    ///           and there is no usage cap. Also serves as an escape hatch for teams with compliance concerns.
    /// </summary>
    public enum LlmMode
    {
        Hosted = 0,
        ByoKey = 1
    }

    /// <summary>
    /// LLM configuration, persisted in EditorPrefs (machine-local, not version-controlled). Keys and models
    /// are stored separately per provider, so switching providers does not lose the other provider's settings.
    /// API keys are stored in plaintext in local EditorPrefs and are intended for local development use only.
    /// </summary>
    public static class LlmSettings
    {
        private const string KMode = "PerfLint.Llm.Mode";
        private const string KEnabled = "PerfLint.Llm.Enabled";
        private const string KProvider = "PerfLint.Llm.Provider";
        private const string KAutoVerifyFix = "PerfLint.Fix.AutoVerify";
        private static string KKey(LlmProvider p) => "PerfLint.Llm.Key." + p;
        private static string KModel(LlmProvider p) => "PerfLint.Llm.Model." + p;

        // ── Endpoints and models per provider ──
        public const string AnthropicBase = "https://api.anthropic.com";
        public const string DeepSeekBase = "https://api.deepseek.com";

        public const string AnthropicDefault = "claude-haiku-4-5";
        public const string AnthropicStrong = "claude-opus-4-8";
        // v4-flash = non-thinking (cheap and fast); v4-pro = stronger reasoning (R1 family).
        public const string DeepSeekDefault = "deepseek-v4-flash";
        public const string DeepSeekStrong = "deepseek-v4-pro";

        // The hosted proxy always uses flash (fix/explain are mechanical tasks that require no deep reasoning; cheap and fast). The server also enforces this model.
        public const string HostedModel = DeepSeekDefault;

        /// <summary>Hosted LLM proxy endpoint: reuses the same domain as the license proxy (same Worker), at path /llm.</summary>
        public static string ProxyLlmEndpoint =>
            Licensing.LicenseSettings.Endpoint.TrimEnd('/') + "/llm";

        /// <summary>Non-consuming balance read (same Worker, /llm/balance): syncs the real remaining credits without spending one.</summary>
        public static string ProxyBalanceEndpoint =>
            Licensing.LicenseSettings.Endpoint.TrimEnd('/') + "/llm/balance";

        private const string KAnonId = "PerfLint.Client.AnonId";

        /// <summary>Current invocation mode. Defaults to Hosted (zero-config).</summary>
        public static LlmMode Mode
        {
            get => (LlmMode)EditorPrefs.GetInt(KMode, (int)LlmMode.Hosted);
            set => EditorPrefs.SetInt(KMode, (int)value);
        }

        /// <summary>
        /// Anonymous client ID (generated on first use and persisted in local EditorPrefs). Free users have no
        /// license InstanceId, so this is used as their identity for the hosted proxy's daily quota. Note: clearing
        /// EditorPrefs or switching machines resets this ID — the resulting quota leak is accepted at MVP stage.
        /// </summary>
        public static string AnonClientId
        {
            get
            {
                var id = EditorPrefs.GetString(KAnonId, "");
                if (string.IsNullOrEmpty(id))
                {
                    id = System.Guid.NewGuid().ToString("N");
                    EditorPrefs.SetString(KAnonId, id);
                }
                return id;
            }
        }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(KEnabled, false);
            set => EditorPrefs.SetBool(KEnabled, value);
        }

        /// <summary>
        /// Whether to automatically trigger a background compilation check after an AI fix is applied (with automatic
        /// rollback on failure). Enabled by default. Disabling falls back to "write-guard only + wait for the next
        /// natural compile / manual check", which is useful for very large projects where every domain reload is slow.
        /// </summary>
        public static bool AutoVerifyFix
        {
            get => EditorPrefs.GetBool(KAutoVerifyFix, true);
            set => EditorPrefs.SetBool(KAutoVerifyFix, value);
        }

        public static LlmProvider Provider
        {
            get => (LlmProvider)EditorPrefs.GetInt(KProvider, (int)LlmProvider.Anthropic);
            set => EditorPrefs.SetInt(KProvider, (int)value);
        }

        public static string ApiKey
        {
            get => EditorPrefs.GetString(KKey(Provider), "");
            set => EditorPrefs.SetString(KKey(Provider), value ?? "");
        }

        public static string Model
        {
            get => EditorPrefs.GetString(KModel(Provider), DefaultModel(Provider));
            set => EditorPrefs.SetString(KModel(Provider), string.IsNullOrEmpty(value) ? DefaultModel(Provider) : value);
        }

        public static string BaseUrl => Provider == LlmProvider.DeepSeek ? DeepSeekBase : AnthropicBase;

        public static string DefaultModel(LlmProvider p) => p == LlmProvider.DeepSeek ? DeepSeekDefault : AnthropicDefault;
        public static string StrongModel(LlmProvider p) => p == LlmProvider.DeepSeek ? DeepSeekStrong : AnthropicStrong;

        public static string[] ModelChoices(LlmProvider p) => p == LlmProvider.DeepSeek
            ? new[] { DeepSeekDefault, DeepSeekStrong }
            : new[] { AnthropicDefault, AnthropicStrong };

        /// <summary>Returns the model appropriate for the required strength: migration-category rules use the strong model; everything else uses the user's selected default model.</summary>
        public static string ModelFor(bool strong) => strong ? StrongModel(Provider) : Model;

        /// <summary>
        /// Model used exclusively for script-fix tasks: forces the non-thinking (fast) model. Fixing is a task of
        /// "strictly outputting in ORIGINAL/FIXED format" and requires no deep reasoning; the thinking model
        /// (DeepSeek v4-pro) would burn the token budget on reasoning_content, leaving the actual content empty.
        /// DeepSeek → flash (downgraded even if the user's default is pro); Anthropic has no automatic thinking,
        /// so the user's selected model is used as-is.
        /// </summary>
        public static string FixModel => Provider == LlmProvider.DeepSeek ? DeepSeekDefault : Model;

        /// <summary>
        /// Whether AI is available (controls whether AI buttons are shown and whether calls are allowed).
        ///   Hosted: always true — available with zero config; quotas and authorization are enforced server-side at /llm.
        ///   ByoKey: follows the old logic — requires the feature to be enabled and a key to be provided.
        /// </summary>
        public static bool IsConfigured =>
            Mode == LlmMode.Hosted || (Enabled && !string.IsNullOrEmpty(ApiKey));

        /// <summary>Provider display name for the UI: Hosted uses our own brand name (does not expose the underlying model vendor); ByoKey shows the specific provider the user has configured.</summary>
        public static string ProviderDisplayName =>
            Mode == LlmMode.Hosted
                ? "PerfLint AI service"
                : (Provider == LlmProvider.DeepSeek ? "DeepSeek" : "Claude");
    }
}
