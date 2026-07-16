using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;
using UnityEngine.Rendering;

namespace PerfLint.Capture
{
    /// <summary>
    /// In-player shader-variant recorder (development builds only). Release players never even contain this code —
    /// the whole assembly is compiled out via the asmdef's <c>UNITY_EDITOR || DEVELOPMENT_BUILD</c> constraint.
    ///
    /// How it works: when Graphics Settings → "Log Shader Compilation" is on, a development player logs one line per
    /// shader variant it compiles ("Uploaded shader variant to the GPU driver: …"). Those lines are printed by native
    /// engine code and — empirically, 2022.3 Windows/D3D11 probe, 2026-07-07 — are NOT dispatched to managed log
    /// callbacks, so the primary capture source is the player's own log file: <c>Application.consoleLogPath</c> is
    /// tailed incrementally from offset 0 (which also picks up the splash/startup variants compiled before this
    /// recorder booted). <c>Application.logMessageReceivedThreaded</c> stays subscribed as belt-and-suspenders for
    /// platforms/versions that do dispatch the lines; the session dedupes if both sources see one. Platforms without a
    /// console log file (Android/iOS — consoleLogPath is empty there) can't self-capture; their path is importing a
    /// logcat/console dump into the PerfLint Shaders panel, which shares this parser.
    ///
    /// Every couple of seconds the recorder
    ///   (a) streams new records to the editor over the profiler's PlayerConnection when one is attached, and
    ///   (b) rewrites a cumulative JSON capture file under persistentDataPath — the offline path for desktop players
    ///       that can't attach; the PerfLint Shaders panel imports it (or a raw Player.log) later.
    /// The file is seeded from the previous run, so playing through scenes across several sessions accumulates
    /// coverage in one capture.
    ///
    /// Two boot paths, deliberately redundant:
    ///  1. Scene injection (primary): the editor's PlayerVariantBuildInjector adds this component to scene 0 of every
    ///     development build made while "Log Shader Compilation" is on. Being referenced by a scene guarantees the
    ///     assembly ships and the component runs — no reliance on RuntimeInitializeOnLoad registration or on reading
    ///     GraphicsSettings inside the player.
    ///  2. <see cref="Boot"/> (fallback): RuntimeInitializeOnLoadMethod self-start for players whose first scene
    ///     didn't go through the injector (additive-only setups, bundles). Gated on Debug.isDebugBuild plus the baked
    ///     "Log Shader Compilation" value.
    /// <see cref="Awake"/> deduplicates when both fire.
    ///
    /// Diagnosability: the recorder announces itself in the player log on start, sends an empty "online" payload the
    /// moment an editor attaches (so the panel can tell "recorder alive, nothing compiled yet" apart from "recorder
    /// never started"), logs once when streaming begins, and warns once if sending fails.
    ///
    /// Privacy: PlayerConnection is the local editor-attach channel and the capture file stays on the device —
    /// nothing ever leaves the user's machine/LAN, same contract as the rest of PerfLint.
    /// </summary>
    [AddComponentMenu("")] // created by the build injector / self-boot only; keep out of the Add Component menu
    internal sealed class PlayerVariantRecorder : MonoBehaviour
    {
        private const float FlushInterval = 2f;

        private static PlayerVariantRecorder _instance;

        /// <summary>Set by the build injector so logs can tell which boot path produced this instance.</summary>
        [SerializeField] internal bool bootedByInjection;

        private readonly object _gate = new object();
        private readonly VariantCaptureSession _session = new VariantCaptureSession();
        private bool _dirtySinceWrite;
        private string _filePath; // null when persistentDataPath isn't writable — connection path still works
        private float _nextFlush;
        private bool _wasConnected;
        private bool _helloPending;
        private bool _announcedStreaming;
        private bool _warnedSendFailure;
        private string _consolePath;    // Application.consoleLogPath; empty on Android/iOS → tailing off
        private long _consoleOffset;    // how far into the console log we've parsed
        private string _tailRemainder = ""; // partial last line carried between tail reads

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            // The editor has its own Play Mode recorder (ShaderUtil tracking); this one is for built players.
            if (Application.isEditor || !Debug.isDebugBuild || _instance != null) return;
            bool logOn;
            try { logOn = GraphicsSettings.logWhenShaderIsCompiled; }
            catch { return; }
            if (!logOn) return;

            var go = new GameObject("PerfLintVariantRecorder");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerVariantRecorder>();
        }

        private void Awake()
        {
            // Defensive: injected objects exist only in built scenes, but never run in the editor either way.
            if (Application.isEditor || (_instance != null && _instance != this))
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            try
            {
                string dir = Path.Combine(Application.persistentDataPath, VariantCaptureProtocol.CaptureFolder);
                Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir,
                    VariantCaptureProtocol.CaptureFilePrefix +
                    Sanitize(Application.platform.ToString()) + "-" +
                    Sanitize(SystemInfo.graphicsDeviceType.ToString()) + ".json");

                // Seed from the previous session so repeated runs accumulate coverage in one file. Seeded records go
                // through Add → they are pending too, so an attaching editor receives the full cumulative set.
                if (File.Exists(_filePath))
                {
                    var prev = VariantCapturePayload.FromJson(File.ReadAllText(_filePath));
                    if (prev != null)
                        lock (_gate)
                            foreach (var r in prev.records)
                                _session.Add(r);
                }
            }
            catch { _filePath = null; }

            try { _consolePath = Application.consoleLogPath; }
            catch { _consolePath = null; }

            Application.logMessageReceivedThreaded += OnLog;
            Debug.Log("PerfLint: recording compiled shader variants (" +
                      (bootedByInjection ? "scene-injected" : "self-booted") +
                      ", development build). Source: " +
                      (string.IsNullOrEmpty(_consolePath) ? "log callback only (no console log file on this platform)" : _consolePath) +
                      ". Capture file: " + (_filePath ?? "<unavailable>"));
        }

        private void OnDestroy()
        {
            if (_instance != this) return; // a deduplicated duplicate — never subscribed
            Application.logMessageReceivedThreaded -= OnLog;
            Flush();
            _instance = null;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            // Miss path is a prefix check — cheap enough to run on every log line the app prints.
            if (!ShaderCompileLogParser.TryParse(condition, out var shader, out var pass, out var kws)) return;
            lock (_gate)
                if (_session.Add(VariantRecord.Create(shader, pass, kws)))
                    _dirtySinceWrite = true;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextFlush) return;
            _nextFlush = Time.unscaledTime + FlushInterval;
            Flush();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) Flush(); // mobile: app going to background is often the last chance to persist
        }

        private void OnApplicationQuit() => Flush();

        private void Flush()
        {
            TailConsoleLog();

            bool connected;
            try { connected = PlayerConnection.instance != null && PlayerConnection.instance.isConnected; }
            catch { connected = false; }

            // Announce on every (re)connection so the editor panel can show "recorder online" even before the first
            // variant compiles — the difference between "alive but idle" and "never started" is the whole diagnosis.
            if (connected && !_wasConnected) _helloPending = true;
            _wasConnected = connected;
            if (_helloPending && connected && TrySend(new List<VariantRecord>()))
                _helloPending = false;

            List<VariantRecord> toSend = null;
            List<VariantRecord> toWrite = null;
            lock (_gate)
            {
                // Only drain when an editor is attached; otherwise pending keeps accumulating so a late attach
                // still receives everything.
                if (connected && _session.PendingCount > 0) toSend = _session.DrainPending();
                if (_dirtySinceWrite && _filePath != null)
                {
                    toWrite = _session.Snapshot();
                    _dirtySinceWrite = false;
                }
            }

            if (toSend != null)
            {
                if (TrySend(toSend))
                {
                    if (!_announcedStreaming)
                    {
                        _announcedStreaming = true;
                        Debug.Log($"PerfLint: streaming captured shader variants to the editor ({toSend.Count} in the first batch).");
                    }
                }
                else
                {
                    lock (_gate) _session.Requeue(toSend); // transport hiccup — ride the next flush
                }
            }

            if (toWrite != null)
            {
                try { File.WriteAllText(_filePath, MakePayload(toWrite).ToJson()); }
                catch
                {
                    lock (_gate) _dirtySinceWrite = true; // best-effort; retry next flush
                }
            }
        }

        /// <summary>
        /// Incrementally parse the player's own console log for shader-upload lines — the primary capture source,
        /// since the engine prints them natively without dispatching to managed callbacks. Reads only the bytes added
        /// since the last flush (capped per tick), carries partial trailing lines between reads, and resets if the
        /// file shrinks (external truncation). Shader lines are ASCII, so a multi-byte char cut at a chunk boundary
        /// can't corrupt a match. Any IO hiccup (locked file, AV scan) just defers to the next flush.
        /// </summary>
        private void TailConsoleLog()
        {
            if (string.IsNullOrEmpty(_consolePath)) return;
            try
            {
                using (var fs = new FileStream(_consolePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < _consoleOffset) { _consoleOffset = 0; _tailRemainder = ""; }
                    if (fs.Length == _consoleOffset) return;
                    fs.Seek(_consoleOffset, SeekOrigin.Begin);
                    var buf = new byte[(int)System.Math.Min(fs.Length - _consoleOffset, 1 << 20)];
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n <= 0) return;
                    _consoleOffset += n;

                    string text = _tailRemainder + Encoding.UTF8.GetString(buf, 0, n);
                    int start = 0;
                    for (int i = 0; i < text.Length; i++)
                    {
                        if (text[i] != '\n') continue;
                        int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                        if (ShaderCompileLogParser.TryParse(text.Substring(start, end - start), out var s, out var p, out var k))
                            lock (_gate)
                                if (_session.Add(VariantRecord.Create(s, p, k)))
                                    _dirtySinceWrite = true;
                        start = i + 1;
                    }
                    _tailRemainder = text.Substring(start);
                    if (_tailRemainder.Length > 64 * 1024) _tailRemainder = ""; // runaway unterminated-line guard
                }
            }
            catch { /* try again next flush */ }
        }

        private bool TrySend(List<VariantRecord> records)
        {
            try
            {
                PlayerConnection.instance.Send(VariantCaptureProtocol.MessageGuid,
                    Encoding.UTF8.GetBytes(MakePayload(records).ToJson()));
                return true;
            }
            catch
            {
                if (!_warnedSendFailure)
                {
                    _warnedSendFailure = true;
                    Debug.LogWarning("PerfLint: couldn't stream captured variants to the editor; will keep retrying. " +
                                     "Records still persist to: " + (_filePath ?? "<unavailable>"));
                }
                return false;
            }
        }

        private static VariantCapturePayload MakePayload(List<VariantRecord> records) => new VariantCapturePayload
        {
            online = true, // any message from the recorder proves it's alive; empty payloads are pure heartbeats
            platform = Application.platform.ToString(),
            api = SystemInfo.graphicsDeviceType.ToString(),
            unity = Application.unityVersion,
            product = Application.productName,
            records = records,
        };

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
