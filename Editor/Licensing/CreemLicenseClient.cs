using System;
using System.Text;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine.Networking;

namespace PerfLint.Licensing
{
    /// <summary>Normalized license response (hides the concrete JSON shape of Creem/the proxy).</summary>
    public readonly struct LicenseResponse
    {
        public readonly bool Ok;            // HTTP 2xx and the license was successfully parsed
        public readonly bool ServerReached; // an HTTP response was received (distinguishes network failure vs. server-side rejection)
        public readonly long HttpCode;
        public readonly string Error;
        public readonly string Status;      // Creem status field
        public readonly string ProductId;
        public readonly string ExpiresAt;   // ISO-8601 or ""
        public readonly string InstanceId;

        private LicenseResponse(bool ok, bool reached, long code, string error,
            string status, string product, string expires, string instance)
        {
            Ok = ok; ServerReached = reached; HttpCode = code; Error = error;
            Status = status; ProductId = product; ExpiresAt = expires; InstanceId = instance;
        }

        public static LicenseResponse Success(long code, string status, string product, string expires, string instance)
            => new LicenseResponse(true, true, code, null, status, product, expires, instance);

        public static LicenseResponse ServerError(long code, string error)
            => new LicenseResponse(false, true, code, error, null, null, null, null);

        public static LicenseResponse NetworkError(string error)
            => new LicenseResponse(false, false, 0, error, null, null, null, null);
    }

    /// <summary>
    /// Minimal license client: POSTs to the proxy's /activate, /validate, and /deactivate endpoints (the proxy injects the Creem secret key and forwards the request).
    /// Structurally mirrors LlmClient — raw HTTP, no external dependencies, polled via EditorApplication.update, callbacks on the main thread.
    /// </summary>
    public static class CreemLicenseClient
    {
        public static void Activate(string key, string instanceName, Action<LicenseResponse> onDone)
            => Post("/activate", $"{{\"key\":{JsonStr(key)},\"instance_name\":{JsonStr(instanceName)}}}", onDone);

        public static void Validate(string key, string instanceId, Action<LicenseResponse> onDone)
            => Post("/validate", $"{{\"key\":{JsonStr(key)},\"instance_id\":{JsonStr(instanceId)}}}", onDone);

        public static void Deactivate(string key, string instanceId, Action<LicenseResponse> onDone)
            => Post("/deactivate", $"{{\"key\":{JsonStr(key)},\"instance_id\":{JsonStr(instanceId)}}}", onDone);

        private static void Post(string path, string body, Action<LicenseResponse> onDone)
        {
            string url = LicenseSettings.Endpoint.TrimEnd('/') + path;
            // Build via Uri, not the string ctor: UnityWebRequest's string path runs MakeInitialUrl,
            // which mis-parses URLs with an explicit port (e.g. http://127.0.0.1:8787) and throws
            // "Invalid URI: Invalid port specified". Prod endpoint has no port so it never hit this,
            // but self-hosting / local-dev endpoints do. The Uri overload bypasses that parsing.
            var req = new UnityWebRequest(new Uri(url), "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 20
            };
            req.SetRequestHeader("content-type", "application/json");

            var op = req.SendWebRequest();

            void Tick()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Tick;
                try
                {
                    long code = req.responseCode;
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        var parsed = Parse(req.downloadHandler.text, code);
                        onDone(parsed);
                    }
                    else if (code > 0)
                    {
                        string msg = ParseError(req.downloadHandler.text) ?? req.error ?? L.Tr("request failed", "请求失败");
                        onDone(LicenseResponse.ServerError(code, $"[{code}] {msg}"));
                    }
                    else
                    {
                        onDone(LicenseResponse.NetworkError(req.error ?? L.Tr("network unreachable", "网络不可达")));
                    }
                }
                finally
                {
                    req.Dispose();
                }
            }

            EditorApplication.update += Tick;
        }

        // ── Parsing (the proxy forwards Creem's license object verbatim) ─────────────
        [Serializable] private class CreemLicense
        {
            public string status;
            public string product_id;
            public string expires_at;
            public CreemInstance instance;
        }
        [Serializable] private class CreemInstance { public string id; }

        [Serializable] private class ErrEnvelope { public ErrBody error; }
        [Serializable] private class ErrBody { public string message; }

        private static LicenseResponse Parse(string json, long code)
        {
            try
            {
                var lic = UnityEngine.JsonUtility.FromJson<CreemLicense>(json);
                if (lic == null || string.IsNullOrEmpty(lic.status))
                    return LicenseResponse.ServerError(code, L.Tr("Could not parse the license response: ", "无法解析许可证响应：") + Truncate(json, 300));
                return LicenseResponse.Success(
                    code, lic.status, lic.product_id, lic.expires_at, lic.instance?.id);
            }
            catch (Exception ex)
            {
                return LicenseResponse.ServerError(code, L.Tr("Error parsing the license response: ", "解析许可证响应出错：") + ex.Message);
            }
        }

        private static string ParseError(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return UnityEngine.JsonUtility.FromJson<ErrEnvelope>(json)?.error?.message; }
            catch { return null; }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? L.Tr("(empty)", "(空)") : (s.Length <= max ? s : s.Substring(0, max) + "…");

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2).Append('"');
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
            return sb.Append('"').ToString();
        }
    }
}
