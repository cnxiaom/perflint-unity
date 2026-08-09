using System;
using System.Collections.Generic;
using System.IO;
using PerfLint.L10n;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// How much each measured figure moves on this machine when nothing has been changed.
    ///
    /// This type exists because of a failed experiment, and the experiment is worth stating. A baseline was
    /// established, nothing was edited, and the same scene was measured again 24 minutes later. Of thirteen figures,
    /// five reported a change: frame time +1.1%, GPU frame time +1.3%, allocation per second −1.2%, total used memory
    /// −1.0%, managed heap −6.7%. Every figure that is genuinely a property of the content — draw calls, batches,
    /// SetPass, triangles, vertices, allocation per frame — correctly reported no measurable change.
    ///
    /// The reason is a mismatch of time scales. A baseline's repetitions are about a minute apart, so their spread
    /// measures short-term repeatability; it says nothing about how far these numbers wander over the tens of minutes
    /// or hours that actually pass while somebody does the work being measured. Using the first as a band for the
    /// second is not a tuning problem, it is the wrong quantity.
    ///
    /// So the right quantity gets measured instead of guessed. Any comparison in which PerfLint recorded no fixes is,
    /// as far as we can tell, a sample of pure drift: two measurements of an unchanged project, taken however far
    /// apart the user happened to leave them. Those samples accumulate here, and the band a verdict must clear is the
    /// larger of the baseline's repetition spread and the drift we have actually seen.
    ///
    /// Deliberately the maximum of observed samples rather than a standard deviation: with one to a handful of
    /// samples a σ estimate means nothing, whereas "we have watched this number move that far with nothing changed"
    /// is a fact. It errs toward a wider band, which errs toward staying silent about a real win — the failure this
    /// feature is allowed to have.
    ///
    /// Stored per scene rather than per baseline, and outside the baseline directory, because drift is a property of
    /// the machine and the scene: replacing a baseline should not throw away what was learned about either.
    /// </summary>
    public static class BenchmarkDrift
    {
        /// <summary>One observed null comparison: the project was (as far as we recorded) unchanged, and these figures moved anyway.</summary>
        [Serializable]
        public sealed class Sample
        {
            public string sceneGuid;

            /// <summary>
            /// Which baseline this reading is relative to (its pinned-at ticks).
            ///
            /// A delta is meaningless without its reference point, and this file used to ignore that: samples were kept
            /// per scene on the reasoning that drift belongs to the machine, so replacing a baseline should not throw
            /// away what was learned. Measured proof that this was wrong — the same later state read +13.0% against an
            /// older baseline and about ±3% against the current one, and mixing the two set the bar at 13% for a scene
            /// whose real figure was a third of that. An old baseline's deltas measure the distance between ITS
            /// conditions and now, which is a different quantity from drift around the baseline in force.
            ///
            /// 0 means "recorded before this field existed". Such a sample is ignored rather than assumed, because
            /// there is no way to tell what it was relative to.
            /// </summary>
            public long baselineTicks;

            /// <summary>Id of the "after" session, so the same comparison is never counted twice.</summary>
            public string afterSessionId;
            public long takenAtUtcTicks;
            /// <summary>How far apart the two measurements were. Kept so the UI can say what span the calibration covers.</summary>
            public double gapMinutes;
            // Parallel arrays rather than a dictionary: JsonUtility serializes neither dictionaries nor tuples.
            public string[] keys;
            public double[] deltaPercents;

            public DateTime AtUtc => new DateTime(takenAtUtcTicks, DateTimeKind.Utc);
        }

        /// <summary>The drift band for one scene, derived from its samples.</summary>
        public sealed class Band
        {
            readonly Dictionary<string, double> _worst = new Dictionary<string, double>(StringComparer.Ordinal);

            public int SampleCount { get; }
            /// <summary>Longest gap any sample covered. A calibration built only from back-to-back runs is worth saying so about.</summary>
            public double WidestGapMinutes { get; }

            public Band(IReadOnlyList<Sample> samples)
            {
                if (samples == null) return;
                SampleCount = samples.Count;

                foreach (var s in samples)
                {
                    if (s?.keys == null || s.deltaPercents == null) continue;
                    if (s.gapMinutes > WidestGapMinutes) WidestGapMinutes = s.gapMinutes;

                    int n = Math.Min(s.keys.Length, s.deltaPercents.Length);
                    for (int i = 0; i < n; i++)
                    {
                        string k = s.keys[i];
                        if (string.IsNullOrEmpty(k)) continue;
                        double abs = Math.Abs(s.deltaPercents[i]);
                        if (!_worst.TryGetValue(k, out double prev) || abs > prev) _worst[k] = abs;
                    }
                }
            }

            public bool HasData => SampleCount > 0;

            /// <summary>Largest movement this figure has shown with nothing changed, as a percentage. 0 when unknown.</summary>
            public double Percent(string key) =>
                key != null && _worst.TryGetValue(key, out double v) ? v : 0.0;

            /// <summary>Empty band — nothing observed yet, so nothing to add to the repetition spread.</summary>
            public static Band None => new Band(null);
        }

        /// <summary>Kept to a handful: the band is a maximum, so old samples never stop counting, and the file has no reason to grow.</summary>
        const int MaxSamples = 12;

        static string FilePath
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(root, "Library", "PerfLint", "drift.json");
            }
        }

        /// <summary>
        /// The band for a scene, or an empty one when nothing has been observed for it.
        /// </summary>
        /// <param name="excludeAfterSessionId">
        /// The comparison currently being judged. It MUST be excluded, because a null comparison is recorded as a
        /// drift sample and would otherwise be measured against a band containing itself — which makes
        /// |delta| ≤ band true by construction, so every such comparison dismisses itself and the detector goes
        /// permanently silent. Observed: a 10.0% frame-time movement reported as inside a ±10.0% band.
        /// </param>
        public static Band Load(string sceneGuid, long baselineTicks, string excludeAfterSessionId = null) =>
            new Band(Samples(sceneGuid, baselineTicks, excludeAfterSessionId));

        /// <summary>
        /// Records a null comparison as a drift sample, unless that same comparison is already recorded.
        ///
        /// Idempotent by "after" session id, because the caller re-reads its state on every window focus and must not
        /// be able to count one measurement as several observations of drift.
        /// </summary>
        public static bool RecordIfNew(string sceneGuid, long baselineTicks, string afterSessionId, double gapMinutes,
            IReadOnlyList<KeyValuePair<string, double>> deltas)
        {
            if (string.IsNullOrEmpty(sceneGuid) || string.IsNullOrEmpty(afterSessionId) || baselineTicks == 0) return false;
            if (deltas == null || deltas.Count == 0) return false;

            try
            {
                var all = LoadAll();
                foreach (var s in all)
                    if (s != null && string.Equals(s.afterSessionId, afterSessionId, StringComparison.Ordinal))
                        return false; // already counted

                var keys = new string[deltas.Count];
                var values = new double[deltas.Count];
                for (int i = 0; i < deltas.Count; i++) { keys[i] = deltas[i].Key; values[i] = deltas[i].Value; }

                all.Add(new Sample
                {
                    sceneGuid = sceneGuid,
                    baselineTicks = baselineTicks,
                    afterSessionId = afterSessionId,
                    takenAtUtcTicks = DateTime.UtcNow.Ticks,
                    gapMinutes = gapMinutes,
                    keys = keys,
                    deltaPercents = values
                });

                if (all.Count > MaxSamples) all.RemoveRange(0, all.Count - MaxSamples);
                Save(all);
                return true;
            }
            catch
            {
                // Calibration is an aid to honesty, not a dependency: a failure here leaves the band narrower, which
                // the caller already treats as "not calibrated yet".
                return false;
            }
        }

        /// <summary>
        /// The individual samples for a scene, oldest first.
        ///
        /// Exposed because the band is a maximum, so one sample decides it, and a single number on screen gives the
        /// user no way to notice that one of three readings is 10% while the others are 1%. Seeing the samples is what
        /// makes a contaminated one — a condition that changed rather than the machine drifting — discoverable at all.
        /// </summary>
        /// <param name="baselineTicks">Only readings taken against this baseline count; see <see cref="Sample.baselineTicks"/>.</param>
        /// <param name="excludeAfterSessionId">
        /// The comparison being judged. It MUST be excluded, because a null comparison is recorded as a drift reading
        /// and would otherwise be measured against a band containing itself — making |delta| ≤ band true by
        /// construction, so every such comparison dismisses itself. Observed: a 10.0% movement inside a ±10.0% band.
        /// </param>
        public static IReadOnlyList<Sample> Samples(string sceneGuid, long baselineTicks, string excludeAfterSessionId = null)
        {
            if (string.IsNullOrEmpty(sceneGuid) || baselineTicks == 0) return Array.Empty<Sample>();

            var mine = new List<Sample>();
            foreach (var s in LoadAll())
            {
                if (s == null || !string.Equals(s.sceneGuid, sceneGuid, StringComparison.Ordinal)) continue;
                if (s.baselineTicks != baselineTicks) continue;
                if (!string.IsNullOrEmpty(excludeAfterSessionId)
                    && string.Equals(s.afterSessionId, excludeAfterSessionId, StringComparison.Ordinal)) continue;
                // Retroactive: a sample recorded before this check existed can still be poisoned, and it goes on
                // widening the band for as long as it is kept. Filtering on read rather than rewriting the file makes
                // this idempotent and costs nothing — a sample that should never have been collected simply stops
                // counting, and no user data is destroyed to achieve it.
                if (ContentMoved(s.keys, s.deltaPercents)) continue;
                mine.Add(s);
            }
            return mine;
        }

        /// <summary>
        /// Whether a reading describes a scene that was not in the same state twice, and therefore is not a
        /// measurement of drift at all.
        ///
        /// Drift means "how far these numbers wander when nobody touches anything". The collection gate used to be
        /// "PerfLint recorded no changes", which misses everything the journal cannot see — above all a user playing
        /// their own game, since Play Mode never touches disk. Geometry is the tell: the same scene from the same
        /// viewpoint renders the same triangles, so a content figure moving means the two runs were not comparable.
        ///
        /// Found in a museum project's store, where 2 of 4 samples had been collected across different parts of the
        /// level — one at triangles -49.8%, drawCalls -49.8%, frameTime -69.3%. Those two were permanently raising
        /// the bar every later verdict had to clear. The other two showed triangles +0.5% and +0.1% and were real
        /// drift readings, one of them with frame time moving 39.4% on its own — exactly what this is for.
        ///
        /// Lives here rather than at the call site because it is a rule about what a drift sample IS, and it is now
        /// applied in two places: before recording one, and when reading the ones already stored.
        ///
        /// The threshold is a judgement: LOD switches and culling boundaries move these by a percent or two between
        /// runs, and a real change of scene state has been an order of magnitude larger every time it has been seen.
        /// </summary>
        internal static bool ContentMoved(IReadOnlyList<string> keys, IReadOnlyList<double> deltaPercents)
        {
            if (keys == null || deltaPercents == null) return false;
            int n = Math.Min(keys.Count, deltaPercents.Count);
            for (int i = 0; i < n; i++)
            {
                double p = deltaPercents[i];
                if (double.IsNaN(p)) continue;
                if (BenchmarkMetricKeys.Scope(keys[i]) != MetricScope.Content) continue;
                if (Math.Abs(p) >= ContentChangedPercent) return true;
            }
            return false;
        }

        /// <summary>Same rule, over the shape a fresh comparison produces. See the overload above.</summary>
        internal static bool ContentMoved(IReadOnlyList<KeyValuePair<string, double>> deltas)
        {
            if (deltas == null) return false;
            foreach (var d in deltas)
            {
                if (double.IsNaN(d.Value)) continue;
                if (BenchmarkMetricKeys.Scope(d.Key) != MetricScope.Content) continue;
                if (Math.Abs(d.Value) >= ContentChangedPercent) return true;
            }
            return false;
        }

        /// <summary>How far a content figure may move between two runs before the pair stops counting as "nobody touched anything". See <see cref="ContentMoved(IReadOnlyList{string}, IReadOnlyList{double})"/>.</summary>
        internal const double ContentChangedPercent = 3.0;

        /// <summary>Drops one sample. Needed because contamination arrives one reading at a time and forgetting everything is a heavy price for it.</summary>
        public static void Remove(string afterSessionId)
        {
            if (string.IsNullOrEmpty(afterSessionId)) return;
            try
            {
                var kept = new List<Sample>();
                foreach (var s in LoadAll())
                    if (s != null && !string.Equals(s.afterSessionId, afterSessionId, StringComparison.Ordinal)) kept.Add(s);
                Save(kept);
            }
            catch { /* ignore */ }
        }

        /// <summary>The figure that moved most in a sample, for display. Returns null when the sample carries nothing.</summary>
        public static KeyValuePair<string, double>? Worst(Sample s)
        {
            if (s?.keys == null || s.deltaPercents == null) return null;

            string key = null;
            double worst = 0;
            int n = Math.Min(s.keys.Length, s.deltaPercents.Length);
            for (int i = 0; i < n; i++)
            {
                double abs = Math.Abs(s.deltaPercents[i]);
                if (key == null || abs > worst) { key = s.keys[i]; worst = abs; }
            }
            return key == null ? (KeyValuePair<string, double>?)null
                              : new KeyValuePair<string, double>(key, worst);
        }

        /// <summary>Value a sample recorded for one figure, or NaN when it did not carry it.</summary>
        public static double ValueOf(Sample s, string key)
        {
            if (s?.keys == null || s.deltaPercents == null || key == null) return double.NaN;
            int n = Math.Min(s.keys.Length, s.deltaPercents.Length);
            for (int i = 0; i < n; i++)
                if (string.Equals(s.keys[i], key, StringComparison.Ordinal)) return s.deltaPercents[i];
            return double.NaN;
        }

        /// <summary>Forgets what was learned for one scene — for when the machine or the scene has changed enough that it no longer applies.</summary>
        public static void Clear(string sceneGuid)
        {
            try
            {
                var kept = new List<Sample>();
                foreach (var s in LoadAll())
                    if (s != null && !string.Equals(s.sceneGuid, sceneGuid, StringComparison.Ordinal)) kept.Add(s);
                Save(kept);
            }
            catch { /* ignore */ }
        }

        // ── Storage ───────────────────────────────────────────

        static List<Sample> LoadAll()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return new List<Sample>();
                var dto = JsonUtility.FromJson<Dto>(File.ReadAllText(path));
                return dto?.samples != null ? new List<Sample>(dto.samples) : new List<Sample>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfLint] " + L.Tr(
                    $"Could not read the drift calibration (treating as none): {e.Message}",
                    $"无法读取漂移标定数据（按未标定处理）：{e.Message}"));
                return new List<Sample>();
            }
        }

        static void Save(List<Sample> samples)
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(new Dto { samples = samples.ToArray() }));
        }

        [Serializable]
        sealed class Dto
        {
            public Sample[] samples;
        }
    }
}
