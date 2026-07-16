using System;
using System.Collections.Generic;
using UnityEngine;

namespace PerfLint.Capture
{
    /// <summary>
    /// One captured variant: shader path-name, pass NAME (players log names, not PassType — the editor maps the name
    /// to a PassType when building a ShaderVariantCollection), and the space-joined SORTED keyword set (sorted so the
    /// same variant always produces the same dedup key regardless of the order keywords appeared in the log).
    /// </summary>
    [Serializable]
    internal sealed class VariantRecord
    {
        public string shader;
        public string pass;
        public string keywords; // space-joined, ordinal-sorted; "" = keyword-less variant

        public static VariantRecord Create(string shader, string pass, string[] keywords)
        {
            var ks = keywords == null || keywords.Length == 0 ? Array.Empty<string>() : (string[])keywords.Clone();
            Array.Sort(ks, StringComparer.Ordinal);
            return new VariantRecord { shader = shader, pass = pass ?? "", keywords = string.Join(" ", ks) };
        }

        public string[] KeywordArray() =>
            string.IsNullOrEmpty(keywords) ? Array.Empty<string>() : keywords.Split(' ');

        /// <summary>Variant identity. Newline-separated — none of the three fields can contain '\n'.</summary>
        public string Key() => shader + "\n" + pass + "\n" + keywords;
    }

    /// <summary>
    /// What a recording player sends to the editor over PlayerConnection — and the schema of its capture file under
    /// persistentDataPath. Capture metadata plus a batch of records; JsonUtility-serializable on both ends so there is
    /// no JSON dependency in the player.
    /// </summary>
    [Serializable]
    internal sealed class VariantCapturePayload
    {
        public int version = 1;
        /// <summary>True on every message the in-player recorder sends (an empty-records payload is a pure heartbeat)
        /// — lets the editor panel tell "recorder alive, nothing compiled yet" apart from "recorder never started".
        /// False on imported files, where liveness is meaningless. JsonUtility defaults it to false for old payloads.</summary>
        public bool online;
        public string platform; // Application.platform at capture time
        public string api;      // SystemInfo.graphicsDeviceType — variant sets differ per graphics API
        public string unity;    // Application.unityVersion
        public string product;  // Application.productName — flags importing another project's capture
        public List<VariantRecord> records = new List<VariantRecord>();

        public string ToJson() => JsonUtility.ToJson(this);

        /// <summary>Null when the text isn't a capture payload (wrong JSON shape, or not JSON at all).</summary>
        public static VariantCapturePayload FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var p = JsonUtility.FromJson<VariantCapturePayload>(json);
                return p != null && p.records != null ? p : null;
            }
            catch { return null; }
        }

        /// <summary>Human-readable capture source, shown in the panel's source list.</summary>
        public string SourceLabel()
        {
            if (!string.IsNullOrEmpty(api) || !string.IsNullOrEmpty(unity))
                return $"{platform} / {api} / Unity {unity}";
            return string.IsNullOrEmpty(product) ? platform ?? "" : $"{platform}: {product}";
        }
    }

    /// <summary>Constants shared by the in-player recorder and the editor-side listener.</summary>
    internal static class VariantCaptureProtocol
    {
        /// <summary>PlayerConnection channel for capture batches (player → editor). Must match on both ends.</summary>
        public static readonly Guid MessageGuid = new Guid("6f1c34a8-52be-4a1d-9d0e-7c2b8f5e91a4");

        /// <summary>Folder under persistentDataPath the recorder keeps its cumulative capture file in.</summary>
        public const string CaptureFolder = "PerfLint";

        /// <summary>Capture file name prefix; full name is prefix + platform + "-" + graphics API + ".json".</summary>
        public const string CaptureFilePrefix = "shader-variants-";
    }
}
