using UnityEditor;

namespace PerfLint.Runtime
{
    /// <summary>
    /// What a measurement was asked FOR, and the one place that acts on it when the measurement lands.
    ///
    /// This exists because it was missing, and the shape of the failure is worth keeping written down. Both panels
    /// wrote the same three SessionState keys, but only ONE of them had a completion handler that read the intent
    /// back and pinned the finished session as the baseline. So "Make this the new baseline" in the Autopilot ran a
    /// full three-repetition measurement, wrote it to disk, and then dropped it: the user re-measured a project they
    /// had just changed, was compared against a baseline from before the change, and was told nothing had happened.
    /// Confirmed from disk — the correct 3-run session was sitting in Library/PerfLint/benchmarks, unpinned, while
    /// meta.json still named a session from twenty minutes earlier.
    ///
    /// The lesson is the same one Deep Profile taught a day earlier: a policy written in two places is a policy that
    /// will be updated in one. So the keys and the "what happens when it finishes" decision live here, once, and both
    /// windows delegate.
    ///
    /// SessionState rather than fields because a Play Mode round-trip costs two domain reloads, which destroy the
    /// window and every delegate it registered — and rather than EditorPrefs because an intent must not outlive the
    /// editor session that formed it.
    /// </summary>
    public static class BenchmarkIntent
    {
        /// <summary>Just measure — refresh the figures, decide nothing.</summary>
        public const string Plain = "plain";
        /// <summary>Pin the result as the "before" everything later is judged against.</summary>
        public const string Baseline = "baseline";
        /// <summary>Measure in order to compare against the existing baseline.</summary>
        public const string Compare = "compare";

        /// <summary>
        /// The measurement to start by itself once a baseline has been pinned, as a spec, or empty for none.
        ///
        /// A pinned baseline is unusable on its own: nothing yet says how far these numbers move when nobody
        /// touches them, so the verification screen can only ask for one more measurement with nothing changed —
        /// and asking is all it did, which put a manual click in the middle of a sequence that has no decision in
        /// it. Both halves measure the same scene under the same conditions and neither needs the user, so the
        /// second half starts itself.
        ///
        /// Held as a SPEC rather than a flag because the plan can be edited while the first half runs, and the
        /// calibration has to describe the same scenes the baseline did or it is measuring a different thing.
        /// </summary>
        const string KChained = "PerfLint.Window.ChainedSpec";
        /// <summary>Whether the run in flight is that second half. Read by the Game view strip, which must not call it "run 1 of 2" a second time.</summary>
        const string KChainRunning = "PerfLint.Window.ChainRunning";

        const string KAwaiting = "PerfLint.Window.AwaitingBenchmark";
        const string KIntent = "PerfLint.Window.BenchmarkIntent";
        const string KPendingSpec = "PerfLint.Window.PendingBenchSpec";
        /// <summary>The armed intent has been seen with its run actually in flight — not merely with no run in flight.</summary>
        const string KSawRunning = "PerfLint.Window.BenchmarkSawRunning";
        /// <summary>Session the window-independent completion has already handled, so it does not act on it every frame.</summary>
        const string KFinalized = "PerfLint.Window.BenchmarkFinalizedSession";

        /// <summary>A measurement one of the panels started is still in flight (or parked behind a compile).</summary>
        public static bool Awaiting => SessionState.GetBool(KAwaiting, false);

        public static string Current => SessionState.GetString(KIntent, Plain);

        /// <summary>A measurement asked for while the editor was busy, parked until the project stops moving.</summary>
        public static string PendingSpecJson => SessionState.GetString(KPendingSpec, "");
        public static bool HasPendingSpec => !string.IsNullOrEmpty(PendingSpecJson);

        public static void Arm(string intent)
        {
            SessionState.SetString(KIntent, string.IsNullOrEmpty(intent) ? Plain : intent);
            SessionState.SetBool(KAwaiting, true);
            SessionState.EraseBool(KSawRunning);   // this intent is waiting on a run that has not started
        }

        public static void Park(BenchmarkSpec spec)
        {
            if (spec != null) SessionState.SetString(KPendingSpec, spec.ToJson());
        }

        public static BenchmarkSpec TakeParkedSpec()
        {
            var spec = BenchmarkSpec.FromJson(PendingSpecJson);
            SessionState.EraseString(KPendingSpec);
            return spec;
        }

        public static void Clear()
        {
            SessionState.EraseBool(KAwaiting);
            SessionState.EraseString(KIntent);
            SessionState.EraseString(KPendingSpec);
            SessionState.EraseBool(KSawRunning);
            ClearChained();
        }

        /// <summary>Asks that a calibration run start by itself once the baseline now being measured has been pinned.</summary>
        public static void ChainCalibrationAfterBaseline(BenchmarkSpec spec)
        {
            if (spec != null) SessionState.SetString(KChained, spec.ToJson());
        }

        public static bool HasChainedCalibration => !string.IsNullOrEmpty(SessionState.GetString(KChained, ""));

        /// <summary>True while the chained calibration is the run in flight.</summary>
        public static bool IsRunningChainedCalibration => SessionState.GetBool(KChainRunning, false);

        public static void ClearChained()
        {
            SessionState.EraseString(KChained);
            SessionState.EraseBool(KChainRunning);
        }

        /// <summary>
        /// Starts the calibration that was queued behind a baseline. Returns null when it started or when there was
        /// nothing queued, or a refusal to show.
        ///
        /// Deliberately gated on the run having FINISHED rather than merely stopped. A cancelled baseline still gets
        /// pinned — whatever repetitions completed are on disk and usable — but a user who pressed Cancel is not
        /// asking for a second measurement to start on its own, which is the one way this could feel like the tool
        /// running away with the editor.
        /// </summary>
        static string StartChainedCalibration()
        {
            string json = SessionState.GetString(KChained, "");
            SessionState.EraseString(KChained);
            if (string.IsNullOrEmpty(json)) return null;

            if (BenchmarkRunner.CurrentPhase != BenchmarkRunner.Phase.Done) return null;

            var spec = BenchmarkSpec.FromJson(json);
            if (spec == null) return null;

            Arm(Compare);
            SessionState.SetBool(KChainRunning, true);
            string refusal = BenchmarkRunner.Begin(spec);
            if (refusal != null)
            {
                // Left armed would leave the panel believing a measurement is in flight forever.
                Clear();
                return refusal;
            }
            return null;
        }

        /// <summary>Called while a measurement is in flight, so "finished" can mean finished rather than not started.</summary>
        static void NoteRunning() => SessionState.SetBool(KSawRunning, true);

        /// <summary>
        /// Takes the intent of a measurement that has just finished, clearing it so the next poll — from either
        /// window — does not act on the same completion twice.
        /// </summary>
        public static bool TryConsumeFinished(out string intent)
        {
            intent = Plain;
            if (!Awaiting || BenchmarkRunner.IsRunning || HasPendingSpec) return false;
            // "Not running" alone also describes the moment between Arm and Begin, and the run whose phase is still
            // Done from LAST time. Requiring that this intent has been seen with its own run in flight is what makes
            // "finished" mean finished.
            if (!SessionState.GetBool(KSawRunning, false)) return false;

            intent = Current;
            SessionState.EraseBool(KAwaiting);
            SessionState.EraseString(KIntent);
            SessionState.EraseBool(KSawRunning);
            return true;
        }

        /// <summary>Whether a finished measurement with this intent becomes the baseline. One line, and the point is that there is only one of it.</summary>
        public static bool ShouldPin(string intent) =>
            string.Equals(intent, Baseline, System.StringComparison.Ordinal);

        /// <summary>
        /// Promotes the run set that just finished to the pinned baseline. Returns null on success, or a
        /// human-readable refusal for the caller to show.
        ///
        /// Runs even when the session was cancelled or timed out partway: whatever repetitions completed are already
        /// on disk and are a usable baseline — the refusals inside <see cref="BenchmarkBaseline.Pin"/> are about
        /// measurements that are WRONG (a VSync cap), not about ones that are merely short.
        /// </summary>
        public static string PinFinishedAsBaseline()
        {
            string id = BenchmarkRunner.CurrentSessionId;
            if (string.IsNullOrEmpty(id)) return null;

            var session = BenchmarkRunner.LoadSession(id);
            return BenchmarkBaseline.Pin(session, session?.Spec?.scenePath);
        }

        /// <summary>
        /// Does what a finished measurement asked for WITHOUT anyone having to be looking at a panel — pins it as
        /// the baseline, or banks it as an observation of drift. Returns the session it handled, null when there was
        /// nothing to do, and puts any refusal in <paramref name="refusal"/>.
        ///
        /// The class remarks record this failing once already, and the fix then was to move the POLICY here. The
        /// TRIGGER stayed in the windows, so it failed again by another route: the intent is only ever consumed from
        /// a window's poll, and a window's poll is a UIElements schedule that does not tick while its tab is hidden.
        ///
        /// Measured on this machine rather than reasoned about: with the Autopilot AND the main panel both open, the
        /// Game view focused, and the editor left idle, an armed intent was still armed. That is not an edge case —
        /// entering Play Mode switches to the Game tab, so a scene docked alongside it hides the Autopilot the moment
        /// the measurement it asked for begins, and the dialog that starts one says "don't use the editor while it
        /// runs". A three-repetition baseline then lands as an ordinary comparison against the OLD baseline, silently.
        /// Observed live: an intent sat unconsumed for three minutes after its run finished, and the measurement was
        /// filed against a baseline from fifty minutes earlier.
        ///
        /// Deliberately does not consume the intent. The windows still do that, for the notification they show and
        /// the refusal dialog; re-pinning the same session is a no-op, so both paths can run. What this must not do
        /// is pin the same session on every editor update, which is what the session id guards.
        /// </summary>
        public static string FinalizeFinishedRun(out string refusal)
        {
            refusal = null;
            if (!Awaiting || BenchmarkRunner.IsRunning || HasPendingSpec) return null;
            if (!SessionState.GetBool(KSawRunning, false)) return null;

            string id = BenchmarkRunner.CurrentSessionId;
            if (string.IsNullOrEmpty(id)) return null;
            if (string.Equals(id, SessionState.GetString(KFinalized, ""), System.StringComparison.Ordinal))
                return null;

            SessionState.SetString(KFinalized, id);

            if (ShouldPin(Current))
            {
                refusal = PinFinishedAsBaseline();
                // Only behind a baseline that actually became one. A refused pin (VSync on, a sampling window that
                // crossed a scene load) leaves nothing for a calibration to be a calibration OF, and starting one
                // anyway would spend another two minutes measuring against a "before" that does not exist.
                if (refusal == null) refusal = StartChainedCalibration();
                else ClearChained();
                return id;
            }

            // Whatever this was, it was not the baseline the queued calibration belonged behind.
            ClearChained();

            // Not a baseline, so this run is an "after" — and reading the state is what banks a comparison in which
            // nothing was recorded as a drift observation. That reading was only ever triggered by somebody looking:
            // two measurements back to back with no load in between replaced the first "after" with the second, and
            // the first one's observation of what this machine does on its own was simply lost. Hit while scripting
            // a calibration, where nothing polls; the same is true of the CLI and Pipeline entry points.
            BenchmarkVerifyState.Load();
            return id;
        }

        /// <summary>
        /// Drives <see cref="PinFinishedBaselineOnce"/> from the editor loop, which runs whatever the user is looking
        /// at and re-registers itself after every domain reload — the same reason <see cref="BenchmarkRunner"/> is
        /// built this way. A measurement spans two reloads, so nothing that lives in a window can be relied on.
        /// </summary>
        [InitializeOnLoad]
        static class Completion
        {
            static Completion() => EditorApplication.update += Tick;

            static void Tick()
            {
                if (Awaiting && BenchmarkRunner.IsRunning) { NoteRunning(); return; }

                string finished = FinalizeFinishedRun(out string refusal);
                if (finished != null && refusal != null)
                    UnityEngine.Debug.LogWarning("[PerfLint] " + refusal);
            }
        }
    }
}
