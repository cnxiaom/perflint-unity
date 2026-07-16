using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PerfLint.Capture;
using UnityEditor;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Editor-side receiver for device shader-variant captures. Registered once per domain load on the profiler's
    /// PlayerConnection channel, so a development build launched via Build &amp; Run (or attached over USB/LAN) streams
    /// the variants it compiles straight into the editor while the game runs on the device — no cable gymnastics.
    ///
    /// Devices that can't attach have the offline path: <see cref="ImportFile"/> accepts the recorder's JSON capture
    /// file from persistentDataPath, or ANY raw log text (Player.log, logcat dump) — every line that parses as a
    /// shader-compilation message is taken, so the workflow degrades gracefully even if the in-build recorder itself
    /// couldn't run.
    ///
    /// Accumulated records survive domain reloads and editor restarts in Library/PerfLint (per-project, not versioned)
    /// until the user clears them.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayerVariantIngest
    {
        private const string StoreDir = "Library/PerfLint";
        private const string StorePath = StoreDir + "/player-shader-capture.json";

        [Serializable]
        private sealed class Store
        {
            public List<VariantRecord> records = new List<VariantRecord>();
            public List<string> sources = new List<string>();
        }

        internal sealed class ImportResult
        {
            public int Added;
            public int Parsed;
            public bool WasJson;
            public string Error;
        }

        private static Store _store;
        private static HashSet<string> _keys;
        private static bool _recorderSeenOnline;

        /// <summary>Editor time of the last live batch that added records — drives the panel's "streaming" hint.</summary>
        public static double LastLiveReceive { get; private set; } = double.NegativeInfinity;

        /// <summary>
        /// True once the in-player recorder has reported in (any message, including its empty heartbeat) during the
        /// current attach session. Distinguishes "recorder alive, nothing compiled yet" from "player attached but the
        /// build has no active recorder" — the state that needs a rebuild. Resets whenever no player is attached.
        /// </summary>
        public static bool RecorderOnline
        {
            get
            {
                if (ConnectedPlayers == 0) _recorderSeenOnline = false;
                return _recorderSeenOnline;
            }
        }

        static PlayerVariantIngest()
        {
            try
            {
                EditorConnection.instance.Initialize();
                EditorConnection.instance.Register(VariantCaptureProtocol.MessageGuid, OnPlayerMessage);
            }
            catch { /* headless/batchmode without a connection service — device capture just stays inactive */ }
        }

        public static int Count { get { EnsureLoaded(); return _store.records.Count; } }

        public static IReadOnlyList<string> Sources { get { EnsureLoaded(); return _store.sources; } }

        public static List<VariantRecord> Records() { EnsureLoaded(); return new List<VariantRecord>(_store.records); }

        public static int ConnectedPlayers
        {
            get
            {
                try { return EditorConnection.instance.ConnectedPlayers.Count; }
                catch { return 0; }
            }
        }

        /// <summary>Merge a payload (live message or imported file) into the store. Returns how many records were new.</summary>
        public static int Merge(VariantCapturePayload payload)
        {
            if (payload == null || payload.records == null || payload.records.Count == 0) return 0;
            EnsureLoaded();
            int added = 0;
            foreach (var r in payload.records)
            {
                if (r == null || string.IsNullOrEmpty(r.shader)) continue;
                if (_keys.Add(r.Key())) { _store.records.Add(r); added++; }
            }
            string src = payload.SourceLabel();
            bool newSource = !string.IsNullOrEmpty(src) && !_store.sources.Contains(src);
            if (newSource) _store.sources.Add(src);
            if (added > 0 || newSource) SaveStore();
            return added;
        }

        /// <summary>
        /// Import a capture from disk: the recorder's JSON file, or any raw log text (Player.log / logcat) — lines are
        /// scanned for shader-compilation messages wherever they sit in the line (logcat prefixes tolerated).
        /// </summary>
        public static ImportResult ImportFile(string path)
        {
            var res = new ImportResult();
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { res.Error = e.Message; return res; }

            var payload = VariantCapturePayload.FromJson(text);
            if (payload != null && payload.records.Count > 0)
            {
                res.WasJson = true;
                res.Parsed = payload.records.Count;
                res.Added = Merge(payload);
                return res;
            }

            var bag = new VariantCapturePayload { platform = "log file", product = Path.GetFileName(path) };
            foreach (var raw in text.Split('\n'))
            {
                var msg = ExtractMessage(raw);
                if (msg != null && ShaderCompileLogParser.TryParse(msg, out var s, out var p, out var k))
                {
                    bag.records.Add(VariantRecord.Create(s, p, k));
                    res.Parsed++;
                }
            }
            res.Added = Merge(bag);
            return res;
        }

        public static void Clear()
        {
            _store = new Store();
            _keys = new HashSet<string>();
            try { if (File.Exists(StorePath)) File.Delete(StorePath); }
            catch { /* stale store file just gets overwritten on the next save */ }
        }

        private static void OnPlayerMessage(MessageEventArgs args)
        {
            if (args?.data == null) return;
            VariantCapturePayload payload;
            try { payload = VariantCapturePayload.FromJson(Encoding.UTF8.GetString(args.data)); }
            catch { return; }
            if (payload != null && payload.online) _recorderSeenOnline = true;
            if (Merge(payload) > 0) LastLiveReceive = EditorApplication.timeSinceStartup;
        }

        private static void EnsureLoaded()
        {
            if (_store != null) return;
            _store = new Store();
            _keys = new HashSet<string>();
            try
            {
                if (!File.Exists(StorePath)) return;
                var s = JsonUtility.FromJson<Store>(File.ReadAllText(StorePath));
                if (s == null) return;
                if (s.records != null)
                    foreach (var r in s.records)
                        if (r != null && !string.IsNullOrEmpty(r.shader) && _keys.Add(r.Key()))
                            _store.records.Add(r);
                if (s.sources != null) _store.sources.AddRange(s.sources);
            }
            catch { /* corrupt store → start empty; the device file / log can always be re-imported */ }
        }

        private static void SaveStore()
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(StorePath, JsonUtility.ToJson(_store));
            }
            catch { /* Library not writable is not actionable here; in-memory state still serves the session */ }
        }

        /// <summary>The shader-compilation message inside a possibly-prefixed log line, or null.</summary>
        private static string ExtractMessage(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int i = line.IndexOf("Compiled shader: ", StringComparison.Ordinal);
            if (i < 0) i = line.IndexOf("Uploaded shader variant to the GPU driver: ", StringComparison.Ordinal);
            return i < 0 ? null : line.TrimEnd('\r').Substring(i);
        }
    }
}
