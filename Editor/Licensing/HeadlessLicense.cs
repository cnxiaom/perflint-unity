using System;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;

namespace PerfLint.Licensing
{
    /// <summary>
    /// Headless Pro entitlement for CI / -executeMethod runs. The interactive client
    /// (<see cref="CreemLicenseClient"/>) is async — polled on EditorApplication.update — which cannot
    /// complete inside a synchronous -executeMethod (blocking the main thread stops the pump). This is the
    /// synchronous counterpart: a blocking HttpWebRequest to the same proxy endpoints, safe in batch mode.
    ///
    /// Seat model (respects the paid key's activation limit): a persistent CI runner counts as ONE of your
    /// seats, exactly like a dev PC. Resolution order:
    ///   1. Persisted license on this machine (EditorPrefs) — refresh the grace window with a seat-free
    ///      Validate, then honor <see cref="LicenseService.IsPro"/>. No key needed in CI config.
    ///   2. -perflintLicense &lt;key&gt; — a real Activate (consumes one seat). This is a one-time setup on a
    ///      given machine; afterwards it persists and path 1 takes over. (Re-activating every run would leak
    ///      seats, so callers should pass the key only to first-activate a machine.)
    /// </summary>
    internal static class HeadlessLicense
    {
        const int TimeoutMs = 20000;

        internal readonly struct Result
        {
            public readonly bool Entitled;
            public readonly string Message;
            public Result(bool entitled, string message) { Entitled = entitled; Message = message; }
        }

        internal static Result TryEntitle(string keyArg)
        {
            keyArg = (keyArg ?? "").Trim();

            // 1) Already activated on this machine — refresh grace best-effort (seat-free), then use IsPro.
            if (!string.IsNullOrEmpty(LicenseSettings.Key))
            {
                TryValidateSync(); // best-effort; if the network is down, the offline grace still covers IsPro
                if (LicenseService.IsPro)
                    return new Result(true, "Pro — persisted license on this machine");
                // Persisted but not currently Pro (expired / grace lapsed / revoked): fall through to activation if a key was given.
            }

            // 2) Explicit key: activate (consumes one seat). One-time per machine.
            if (!string.IsNullOrEmpty(keyArg))
            {
                var (ok, msg) = TryActivateSync(keyArg);
                if (ok && LicenseService.IsPro)
                    return new Result(true, "Pro — activated this machine (uses one of your license seats)");
                return new Result(false, "License activation failed: " + msg);
            }

            return new Result(false, string.IsNullOrEmpty(LicenseSettings.Key)
                ? "No license (Free). Activate once in the editor, or pass -perflintLicense <key> (uses one seat)."
                : "License is not active (expired, or offline grace has lapsed — reconnect and re-validate).");
        }

        // POST /validate {key, instance_id} using the stored key+instance. On an active response, refresh
        // status/expiry/last-validated (extends the grace window). Never creates an instance → seat-free.
        static void TryValidateSync()
        {
            string key = LicenseSettings.Key, inst = LicenseSettings.InstanceId;
            if (string.IsNullOrEmpty(key)) return;
            var r = PostSync("/validate", "{\"key\":" + JsonStr(key) + ",\"instance_id\":" + JsonStr(inst) + "}");
            if (!r.Reached || r.Code < 200 || r.Code >= 300) return; // transient/definitive handled by IsPro + grace
            var lic = ParseLicense(r.Body);
            if (lic == null || string.IsNullOrEmpty(lic.status)) return;
            LicenseSettings.Status = lic.status;
            LicenseSettings.ProductId = lic.product_id ?? "";
            LicenseSettings.ExpiresAt = lic.expires_at ?? "";
            LicenseSettings.LastValidatedUtc = DateTime.UtcNow;
        }

        // POST /activate {key, instance_name}. On success persist the full license state (path 1 then takes over).
        static (bool ok, string message) TryActivateSync(string key)
        {
            var r = PostSync("/activate", "{\"key\":" + JsonStr(key) + ",\"instance_name\":" + JsonStr(InstanceName()) + "}");
            if (!r.Reached)
                return (false, "cannot reach the license service (check network / api endpoint)");
            if (r.Code < 200 || r.Code >= 300)
                return (false, r.Code == 404 ? "that license key wasn't found" : "server returned " + r.Code + " " + (ParseError(r.Body) ?? ""));

            var lic = ParseLicense(r.Body);
            if (lic == null || string.IsNullOrEmpty(lic.status))
                return (false, "could not parse the license response");
            if (lic.status != "active")
                return (false, "license status is '" + lic.status + "' (expired or deactivated)");

            LicenseSettings.Key = key;
            LicenseSettings.InstanceId = lic.instance != null ? lic.instance.id : "";
            LicenseSettings.Status = lic.status;
            LicenseSettings.ProductId = lic.product_id ?? "";
            LicenseSettings.ExpiresAt = lic.expires_at ?? "";
            LicenseSettings.LastValidatedUtc = DateTime.UtcNow;
            return (true, "activated");
        }

        // ── Synchronous HTTP (blocking; safe in batch mode where the update-loop pump is unavailable) ──
        readonly struct Post { public readonly bool Reached; public readonly long Code; public readonly string Body;
            public Post(bool reached, long code, string body) { Reached = reached; Code = code; Body = body; } }

        static Post PostSync(string path, string bodyJson)
        {
            try
            {
                // Some Unity Mono builds default to an older protocol; force TLS 1.2 for the HTTPS proxy.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { /* enum/runtime variance — ignore */ }

                string url = LicenseSettings.Endpoint.TrimEnd('/') + path;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                var bytes = Encoding.UTF8.GetBytes(bodyJson);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    return new Post(true, (long)resp.StatusCode, sr.ReadToEnd());
            }
            catch (WebException we)
            {
                // Non-2xx responses arrive here; the body (error envelope) is on we.Response.
                if (we.Response is HttpWebResponse r)
                {
                    try
                    {
                        using (var sr = new StreamReader(r.GetResponseStream()))
                            return new Post(true, (long)r.StatusCode, sr.ReadToEnd());
                    }
                    catch { return new Post(true, (long)r.StatusCode, null); }
                }
                return new Post(false, 0, null); // DNS/TLS/timeout — network unreachable
            }
            catch (Exception)
            {
                return new Post(false, 0, null);
            }
        }

        // Same JSON shape the proxy normalizes to (mirrors CreemLicenseClient's private DTOs).
        [Serializable] class Lic { public string status; public string product_id; public string expires_at; public Inst instance; }
        [Serializable] class Inst { public string id; }
        [Serializable] class ErrEnvelope { public ErrBody error; }
        [Serializable] class ErrBody { public string message; }

        static Lic ParseLicense(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<Lic>(json); } catch { return null; }
        }

        static string ParseError(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<ErrEnvelope>(json)?.error?.message; } catch { return null; }
        }

        static string InstanceName()
        {
            string dev = SystemInfo.deviceName;
            return string.IsNullOrEmpty(dev) ? "unity-ci" : dev;
        }

        static string JsonStr(string s)
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
