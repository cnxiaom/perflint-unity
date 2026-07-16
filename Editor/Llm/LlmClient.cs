using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine.Networking;
using PerfLint.L10n;
using PerfLint.Licensing;

namespace PerfLint.Llm
{
    public readonly struct LlmMessage
    {
        public readonly string Role;    // "user" | "assistant"
        public readonly string Content;
        public LlmMessage(string role, string content) { Role = role; Content = content; }
    }

    public readonly struct LlmResult
    {
        public readonly bool Success;
        public readonly string Text;
        public readonly string Error;
        private LlmResult(bool ok, string text, string error) { Success = ok; Text = text; Error = error; }
        public static LlmResult Ok(string text) => new LlmResult(true, text, null);
        public static LlmResult Fail(string error) => new LlmResult(false, null, error);
    }

    /// <summary>
    /// Ultra-thin multi-provider LLM client (raw HTTP, no external dependencies). Non-streaming; completion
    /// is polled via EditorApplication.update and the callback fires on the main thread. Supports Anthropic
    /// (/v1/messages) and DeepSeek (OpenAI-compatible /chat/completions).
    /// </summary>
    public static class LlmClient
    {
        public static void Send(
            string model,
            string system,
            IReadOnlyList<LlmMessage> messages,
            int maxTokens,
            Action<LlmResult> onDone) => Send(model, system, messages, maxTokens, onDone, false);

        /// <param name="disableThinking">DeepSeek-specific: when true, adds {"thinking":{"type":"disabled"}} to the
        /// request body, causing the model to skip the chain-of-thought and produce an answer directly (faster and
        /// cheaper). Has no effect on Anthropic (which does not think by default).</param>
        public static void Send(
            string model,
            string system,
            IReadOnlyList<LlmMessage> messages,
            int maxTokens,
            Action<LlmResult> onDone,
            bool disableThinking)
        {
            // Zero-config default: routes through our hosted proxy (→ deepseek-v4-flash); no local key required; quotas are enforced server-side.
            if (LlmSettings.Mode == LlmMode.Hosted)
            {
                SendHosted(system, messages, maxTokens, onDone, disableThinking);
                return;
            }

            if (!LlmSettings.IsConfigured)
            {
                onDone(LlmResult.Fail(L.Tr("LLM not configured: in PerfLint's LLM settings, choose a Provider and enter an API Key.", "LLM 未配置：请在 PerfLint 的 LLM 设置里选择 Provider 并填入 API Key。")));
                return;
            }

            var provider = LlmSettings.Provider;
            string baseUrl = LlmSettings.BaseUrl.TrimEnd('/');
            string key = LlmSettings.ApiKey;

            string url;
            string body;

            if (provider == LlmProvider.DeepSeek)
            {
                url = baseUrl + "/chat/completions";
                body = BuildOpenAiBody(model, system, messages, maxTokens, disableThinking);
            }
            else
            {
                url = baseUrl + "/v1/messages";
                body = BuildAnthropicBody(model, system, messages, maxTokens);
            }

            // Build via Uri, not the string url setter (same MakeInitialUrl port bug as SendHosted): the default
            // provider hosts have no port, but a self-hosted BYO base with one would otherwise crash.
            var req = new UnityWebRequest(new Uri(url), "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("content-type", "application/json");

            if (provider == LlmProvider.DeepSeek)
            {
                req.SetRequestHeader("Authorization", "Bearer " + key);
            }
            else
            {
                req.SetRequestHeader("x-api-key", key);
                req.SetRequestHeader("anthropic-version", "2023-06-01");
            }

            var op = req.SendWebRequest();

            void Tick()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Tick;

                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        string msg = ParseError(req.downloadHandler.text) ?? req.error ?? L.Tr("Request failed", "请求失败");
                        onDone(LlmResult.Fail($"[{(int)req.responseCode}] {msg}"));
                        return;
                    }

                    string raw = req.downloadHandler.text;
                    string parseError;
                    string text = provider == LlmProvider.DeepSeek
                        ? ParseOpenAiText(raw, out parseError)
                        : ParseAnthropicTextEx(raw, out parseError);

                    onDone(text != null
                        ? LlmResult.Ok(text)
                        : LlmResult.Fail(parseError));
                }
                finally
                {
                    req.Dispose();
                }
            }

            EditorApplication.update += Tick;
        }

        // ── Hosted proxy path (zero-config default) ──────────────────────
        /// <summary>
        /// Calls through our Worker's /llm route (the server holds the DeepSeek key, enforces deepseek-v4-flash,
        /// and deducts credits per license/instance). The client sends no provider key; it only sends the
        /// license key + instance_id for server-side authentication and billing.
        /// The response body is OpenAI-style (the proxy forwards the DeepSeek response verbatim), so
        /// <see cref="ParseOpenAiText"/> is reused; the remaining credit balance is returned via the
        /// X-PerfLint-Credits-Remaining/-Reset response headers and synced to <see cref="CreditService"/>.
        /// </summary>
        private static void SendHosted(
            string system,
            IReadOnlyList<LlmMessage> messages,
            int maxTokens,
            Action<LlmResult> onDone,
            bool disableThinking)
        {
            string url = LlmSettings.ProxyLlmEndpoint;
            string body = BuildHostedBody(maxTokens, system, messages, disableThinking);

            // Build via Uri, not the string url setter: the string path runs UnityWebRequest's MakeInitialUrl,
            // which throws "Invalid URI: Invalid port specified" on URLs with an explicit port. The prod proxy
            // (api.perflint.dev, no port) never hit this, but a self-hosted / local-dev endpoint with a port does.
            var req = new UnityWebRequest(new Uri(url), "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("content-type", "application/json");

            var op = req.SendWebRequest();

            void Tick()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Tick;

                try
                {
                    // Regardless of success or failure, sync the balance returned by the proxy to local state first (used by the UI and soft-block logic).
                    CreditService.UpdateFromHeaders(
                        req.GetResponseHeader("X-PerfLint-Credits-Remaining"),
                        req.GetResponseHeader("X-PerfLint-Credits-Reset"));

                    if (req.responseCode == 429)
                    {
                        CreditService.MarkExhausted(req.GetResponseHeader("X-PerfLint-Credits-Reset"));
                        onDone(LlmResult.Fail(L.Tr(
                            "Out of AI credits for this period. Upgrade to Pro for more, or add your own API key under Advanced for unlimited (self-funded) use.",
                            "本期 AI 额度已用完。升级 Pro 获取更多，或在「高级」里填入自己的 API key 无限使用（自费 token）。")));
                        return;
                    }

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        string msg = ParseError(req.downloadHandler.text) ?? req.error ?? L.Tr("Request failed", "请求失败");
                        onDone(LlmResult.Fail($"[{(int)req.responseCode}] {msg}"));
                        return;
                    }

                    string text = ParseOpenAiText(req.downloadHandler.text, out var parseError);
                    onDone(text != null ? LlmResult.Ok(text) : LlmResult.Fail(parseError));
                }
                finally
                {
                    req.Dispose();
                }
            }

            EditorApplication.update += Tick;
        }

        /// <summary>
        /// Best-effort, non-consuming sync of the real remaining credit balance from the proxy's /llm/balance route
        /// (reads server-side KV without spending a credit), so the UI shows the true number the moment the LLM panel
        /// opens or the license tier flips — instead of the optimistic "5000/month · ready" standby label that only
        /// gets corrected after the next actual /llm call. No-op in BYO mode (that path bypasses credits entirely).
        /// Silent on any failure (network/offline): the standby label + server 429 backstop remain.
        /// </summary>
        public static void SyncHostedBalance()
        {
            if (LlmSettings.Mode != LlmMode.Hosted) return;

            string url = LlmSettings.ProxyBalanceEndpoint;
            var req = new UnityWebRequest(new Uri(url), "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(BuildIdentityBody())),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("content-type", "application/json");

            var op = req.SendWebRequest();

            void Tick()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Tick;
                try
                {
                    // Only trust a clean 200 with the balance headers; on any error leave the cached/standby state as-is.
                    if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
                    {
                        CreditService.UpdateFromHeaders(
                            req.GetResponseHeader("X-PerfLint-Credits-Remaining"),
                            req.GetResponseHeader("X-PerfLint-Credits-Reset"));
                    }
                }
                finally
                {
                    req.Dispose();
                }
            }

            EditorApplication.update += Tick;
        }

        /// <summary>Auth/billing identity body ({key, instance_id}) shared by the hosted call and the balance query — no code snippet, no model.</summary>
        private static string BuildIdentityBody()
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            string instance = LicenseSettings.InstanceId;
            if (string.IsNullOrEmpty(instance)) instance = LlmSettings.AnonClientId;
            sb.Append("\"key\":").Append(JsonStr(LicenseSettings.Key)).Append(',');
            sb.Append("\"instance_id\":").Append(JsonStr(instance));
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildHostedBody(int maxTokens, string system, IReadOnlyList<LlmMessage> messages, bool disableThinking)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            // Authentication and billing identity: license key (Pro → monthly pool) or empty (Free → daily pool keyed by instance_id).
            // Free users have no license InstanceId; fall back to the anonymous client ID as the daily-quota identity.
            string instance = LicenseSettings.InstanceId;
            if (string.IsNullOrEmpty(instance)) instance = LlmSettings.AnonClientId;
            sb.Append("\"key\":").Append(JsonStr(LicenseSettings.Key)).Append(',');
            sb.Append("\"instance_id\":").Append(JsonStr(instance)).Append(',');
            sb.Append("\"model\":").Append(JsonStr(LlmSettings.HostedModel)).Append(',');
            sb.Append("\"max_tokens\":").Append(maxTokens).Append(',');
            if (disableThinking)
                sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            sb.Append("\"messages\":");
            AppendMessagesArray(sb, messages, system); // OpenAI-style: system prompt is prepended as the first message
            sb.Append('}');
            return sb.ToString();
        }

        // ── Request body builders ────────────────────────────────────────
        private static string BuildAnthropicBody(string model, string system, IReadOnlyList<LlmMessage> messages, int maxTokens)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"model\":").Append(JsonStr(model)).Append(',');
            sb.Append("\"max_tokens\":").Append(maxTokens).Append(',');
            if (!string.IsNullOrEmpty(system))
                sb.Append("\"system\":").Append(JsonStr(system)).Append(',');
            sb.Append("\"messages\":");
            AppendMessagesArray(sb, messages, null);
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildOpenAiBody(string model, string system, IReadOnlyList<LlmMessage> messages, int maxTokens, bool disableThinking)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"model\":").Append(JsonStr(model)).Append(',');
            sb.Append("\"max_tokens\":").Append(maxTokens).Append(',');
            // DeepSeek's thinking mode is enabled by default; turning it off makes the model skip the chain-of-thought and answer directly (faster and cheaper).
            if (disableThinking)
                sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            sb.Append("\"messages\":");
            // OpenAI-style: system prompt is prepended as the first message.
            AppendMessagesArray(sb, messages, system);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendMessagesArray(StringBuilder sb, IReadOnlyList<LlmMessage> messages, string leadingSystem)
        {
            sb.Append('[');
            bool first = true;
            if (!string.IsNullOrEmpty(leadingSystem))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(JsonStr(leadingSystem)).Append('}');
                first = false;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"role\":").Append(JsonStr(messages[i].Role))
                  .Append(",\"content\":").Append(JsonStr(messages[i].Content)).Append('}');
            }
            sb.Append(']');
        }

        // ── Response parsing ─────────────────────────────────────────────
        [Serializable] private class AnthropicResponse { public ContentBlock[] content; }
        [Serializable] private class ContentBlock { public string type; public string text; }

        [Serializable] private class OpenAiResponse { public Choice[] choices; }
        [Serializable] private class Choice { public OaMessage message; public string finish_reason; }
        [Serializable] private class OaMessage { public string role; public string content; public string reasoning_content; }

        [Serializable] private class ErrorEnvelope { public ErrorBody error; }
        [Serializable] private class ErrorBody { public string type; public string message; }

        private static string ParseAnthropicTextEx(string json, out string error)
        {
            error = null;
            try
            {
                var r = UnityEngine.JsonUtility.FromJson<AnthropicResponse>(json);
                if (r?.content == null) { error = L.Tr("Could not parse the response. Raw: ", "无法解析响应。原始：") + Truncate(json, 600); return null; }
                var sb = new StringBuilder();
                foreach (var b in r.content)
                    if (b != null && b.type == "text" && !string.IsNullOrEmpty(b.text))
                        sb.Append(b.text);
                if (sb.Length > 0) return sb.ToString();
                error = L.Tr("Response content is empty. Raw: ", "响应 content 为空。原始：") + Truncate(json, 600);
                return null;
            }
            catch { error = L.Tr("Could not parse the response. Raw: ", "无法解析响应。原始：") + Truncate(json, 600); return null; }
        }

        private static string ParseOpenAiText(string json, out string error)
        {
            error = null;
            try
            {
                var r = UnityEngine.JsonUtility.FromJson<OpenAiResponse>(json);
                var ch = r?.choices != null && r.choices.Length > 0 ? r.choices[0] : null;
                var c = ch?.message?.content;
                if (!string.IsNullOrEmpty(c)) return c;

                // content is empty: distinguish between "truncated by max_tokens" and "model wrote its answer
                // into reasoning_content", so we can surface an actionable error rather than a black-box "could not parse".
                bool truncated = ch?.finish_reason == "length";
                bool hasReasoning = !string.IsNullOrEmpty(ch?.message?.reasoning_content);
                if (truncated)
                    error = L.Tr("The model output hit max_tokens during the thinking phase and was truncated before producing a final answer (content is empty). ", "模型输出在思考阶段就达到 max_tokens 被截断，未生成正式答案（content 为空）。") +
                            L.Tr("Please retry; if it keeps happening, switch to a non-thinking model or shrink the code window.", "请重试；若反复如此，换非思考模型或减小代码窗口。");
                else if (hasReasoning)
                    error = L.Tr("The model only produced reasoning content (reasoning_content) and no final answer (content is empty). Please retry or switch models.", "模型只输出了思考内容（reasoning_content）、没有正式答案（content 为空）。请重试或更换模型。");
                else
                    error = L.Tr("Could not parse the response (content may be empty). Raw: ", "无法解析响应（content 可能为空）。原始：") + Truncate(json, 600);
                return null;
            }
            catch { error = L.Tr("Could not parse the response. Raw: ", "无法解析响应。原始：") + Truncate(json, 600); return null; }
        }

        // Both providers use { "error": { "message": ... } } as their error envelope, so this parser is shared.
        private static string ParseError(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var e = UnityEngine.JsonUtility.FromJson<ErrorEnvelope>(json);
                return e?.error?.message;
            }
            catch { return null; }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return L.Tr("(empty)", "(空)");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
