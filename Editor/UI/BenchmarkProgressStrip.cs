using System;
using System.Collections.Generic;
using PerfLint.L10n;
using PerfLint.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// A progress strip painted across the top of the Game view for as long as a measurement is running.
    ///
    /// It exists because the measurement hides its own progress. Entering Play Mode brings the Game view forward,
    /// and on any ordinary layout the panel that started the run is docked in the same tab group — so the moment the
    /// user presses "measure", the screen telling them what is happening disappears behind the screen they are told
    /// not to touch. Everything the run knows was already being written to the Console and to a panel nobody could
    /// see. This puts it where the user is actually looking.
    ///
    /// EDITOR-SIDE, deliberately. The obvious alternative — a runtime GameObject drawing OnGUI — would be inside the
    /// thing being measured: its draw calls, its allocations and its frame time would all land in the numbers the
    /// strip exists to report. This costs the measurement nothing, because it is not in the game at all.
    ///
    /// Driven from <see cref="EditorApplication.update"/> and <c>[InitializeOnLoad]</c> rather than from any window,
    /// for the reason this project has been bitten by three times: a Play Mode round-trip is two domain reloads, each
    /// of which empties the Game view's UI tree, and an EditorWindow does not rebuild its contents until its tab is
    /// shown. A strip owned by a window would vanish on the first reload and come back only if somebody clicked the
    /// tab it lives in. So the tick owns it, re-attaches it after every reload, and removes it when the run ends —
    /// none of which depends on anyone looking at anything.
    /// </summary>
    [InitializeOnLoad]
    public static class BenchmarkProgressStrip
    {
        const string ElementName = "perflint-benchmark-progress";
        /// <summary>Whether this run has already said where its strip went. SessionState, so it survives the run's own domain reloads.</summary>
        const string KAnnounced = "PerfLint.Bench.StripAnnounced";

        // Fixed, NOT skin-following — the one place in the product where that is the correct call.
        //
        // Everything else PerfLint draws sits on an editor surface, so it takes its greys from PerfLintStyle and
        // flips with the skin. This strip paints its own backdrop, and that backdrop is dark on both skins because
        // it lies over rendered gameplay. Borrowing PerfLintStyle's greys meant borrowing the light-skin half too:
        // Dim there is #4A4F59, chosen to be read against white, and it was being painted onto a near-black strip.
        // Every user not running the dark theme got dark text on a dark bar. Same for the status tints, whose
        // light-skin values are deepened for the same reason and came out at ~3.9:1 here.
        static readonly Color StripInk = new Color(0.960f, 0.965f, 0.975f);
        static readonly Color StripDim = new Color(0.800f, 0.812f, 0.836f);
        static readonly Color StripAmber = new Color(0.980f, 0.820f, 0.420f);
        static readonly Color StripGood = new Color(0.460f, 0.840f, 0.570f);
        static readonly Color StripAccent = new Color(0.450f, 0.680f, 1.000f);

        // The instruction chip's fill, per phase. Low alpha on purpose: enough of an edge to be found by shape, not
        // so much that a bar sitting over somebody's game turns into a second UI competing with it.
        static readonly Color ChipAmber = new Color(0.980f, 0.820f, 0.420f, 0.18f);
        static readonly Color ChipNeutral = new Color(1f, 1f, 1f, 0.10f);

        static readonly Color CancelIdle = new Color(1f, 1f, 1f, 0.16f);
        static readonly Color CancelHover = new Color(1f, 1f, 1f, 0.30f);

        static Type _gameViewType;
        static string _lastSignature = "";

        static BenchmarkProgressStrip()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            // A run that ended — or an editor that was reloaded with no run in flight — must leave nothing behind.
            if (!BenchmarkRunner.IsRunning)
            {
                if (_lastSignature.Length > 0) { RemoveAll(); _lastSignature = ""; }
                SessionState.EraseBool(KAnnounced);
                return;
            }

            var progress = BenchmarkRunner.CurrentProgress;
            if (!progress.IsMeasuring) return;

            string signature = Signature(progress);
            bool changed = signature != _lastSignature;

            int views = 0, attached = 0;
            foreach (var view in GameViews())
            {
                views++;
                var strip = Find(view.rootVisualElement);
                if (strip == null)
                {
                    strip = Build();
                    view.rootVisualElement.Add(strip);
                    attached++;
                    changed = true; // freshly built, so it holds no text yet
                }
                if (changed) Paint(strip, progress);
            }

            // Said once per run, and only when something actually happened, because a strip that never appears is
            // otherwise indistinguishable from a strip that was never written. Tim ran a full measurement on
            // Unity 6.3 and saw no strip; every check afterwards — the type lookup, the window search, Build(),
            // Paint(), and Tick() itself driven with a faked sampling state — worked. Nothing in the product could
            // say whether it had been attached and hidden, or never attached at all. Now it can.
            if (attached > 0 && !SessionState.GetBool(KAnnounced, false))
            {
                SessionState.SetBool(KAnnounced, true);
                Debug.Log($"[PerfLint Benchmark] progress strip attached to {attached} of {views} Game view(s)");
            }

            _lastSignature = signature;
        }

        /// <summary>
        /// What the strip currently reads, so the text is rewritten only when it would differ.
        ///
        /// The tick runs at editor frame rate and the readout changes once a second, so without this every frame of
        /// the measurement would dirty a handful of labels — inside the Game view, during the run whose frame time is
        /// being recorded. Cheap, but not free, and not something to spend on redrawing an unchanged string.
        /// </summary>
        static string Signature(BenchmarkRunner.Progress p) =>
            string.Join("|", ((int)p.Phase).ToString(), p.RunNumber.ToString(), p.Repetitions.ToString(),
                Seconds(p.PhaseRemainingSeconds), Seconds(p.TotalRemainingSeconds),
                Mathf.FloorToInt((float)p.PhaseElapsedSeconds).ToString(), p.TargetSceneName);

        static string Seconds(double s) => double.IsNaN(s) ? "-" : Mathf.FloorToInt((float)s).ToString();

        // ── building ──────────────────────────────────────────

        static VisualElement Build()
        {
            var strip = new VisualElement { name = ElementName };
            strip.style.position = Position.Absolute;
            strip.style.left = 0;
            strip.style.right = 0;
            // Below the Game view's own toolbar rather than over it. Covering it would hide the resolution and
            // Maximize controls, and the toolbar's height differs between editor versions — so it is asked for
            // rather than assumed.
            strip.style.top = ToolbarHeight();
            strip.style.height = 28;
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.alignItems = Align.Center;
            strip.style.paddingLeft = 8;
            strip.style.paddingRight = 4;
            // Near-opaque, because this is the one surface in the product whose backdrop is chosen by somebody else.
            // At the 0.88 it shipped with, a bright frame — a daylit outdoor scene, a white menu — came through far
            // enough to take the grey secondary text with it, and which frame that is changes every second the run
            // is measuring. Legibility here cannot be allowed to depend on what the game happens to be drawing.
            strip.style.backgroundColor = new Color(0.055f, 0.062f, 0.078f, 0.97f);
            // The strip is over live gameplay, so it must not eat clicks meant for the game. Only its own button is
            // interactive; everything else lets input through.
            strip.pickingMode = PickingMode.Ignore;

            // A drawn swatch, never a character. The editor fonts on 2021/2022 have no glyph for the round symbols
            // that would read best here, and a missing glyph renders as a tofu box — see EditorGlyphSafetyTests.
            var dot = new VisualElement { name = "dot" };
            dot.style.width = 8;
            dot.style.height = 8;
            dot.style.marginRight = 8;
            // Pinned like the labels are. A round swatch is the whole phase read at a glance, and flex was happy to
            // squash it to 2 px on a narrow Game view — a sliver that still has a colour and no longer has a shape.
            dot.style.flexShrink = 0;
            PerfLintStyle.Round(dot, 4);
            dot.pickingMode = PickingMode.Ignore;
            strip.Add(dot);

            // 12 is the floor for anything in this product meant to be read, and this strip is read at a glance,
            // from across a Game view, by somebody who has been told not to touch the editor. It shipped at 11/11/10
            // in the two greys below Ink — the smallest and faintest text anywhere in PerfLint, on its least
            // controlled background. Every line here is now at or above the floor.
            strip.Add(Text("headline", 13, FontStyle.Bold, StripInk, 12));
            strip.Add(Text("runs", 12, FontStyle.Normal, StripDim, 12));
            strip.Add(Text("clock", 12, FontStyle.Normal, StripDim, 12));

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1;
            strip.Add(spacer);

            // The one line on the strip that asks for something, given a shape instead of a colour.
            //
            // Contrast alone was not the problem the second time round: amber on near-black measures about 11:1 and
            // still read as the least important thing up here, because it was set in the same size and weight as the
            // clock and pushed to the far edge, which is where a screen puts what it does not need you to read. What
            // was wrong is the ranking — "play until you get there" is the only instruction on screen, and it was
            // ranked below the timer. A chip gives it an edge of its own, so it is found by shape before it is read.
            var instruction = Text("instruction", 12, FontStyle.Bold, StripAmber, 8);
            instruction.style.paddingLeft = 8;
            instruction.style.paddingRight = 8;
            instruction.style.paddingTop = 2;
            instruction.style.paddingBottom = 2;
            PerfLintStyle.Round(instruction, 4);
            // The only thing on the strip allowed to give up room, and the reason the rest is pinned below. A Game
            // view docked narrow cannot fit the waiting instruction — it is a full sentence — and the flex default
            // would have taken the space out of whatever sat last in the row, which is the Cancel button. Losing the
            // end of a sentence that is also stated in the panel and in the confirmation dialog is survivable;
            // losing the only way to stop a run, while the screen tells you not to touch the editor, is not.
            instruction.style.flexShrink = 1;
            instruction.style.minWidth = 0;
            instruction.style.overflow = Overflow.Hidden;
            instruction.style.textOverflow = TextOverflow.Ellipsis;
            strip.Add(instruction);

            // Styled explicitly rather than left to the editor's default button, which on this backdrop is a dark
            // grey plate on a near-black bar — the one control up here, and the hardest thing on it to find. Inline
            // values beat USS, so :hover cannot come from the stylesheet (see PerfLintStyle) and is wired by hand.
            var cancel = new Button(() =>
            {
                BenchmarkRunner.Cancel();
                BenchmarkIntent.Clear();
            })
            { text = L.Tr("Cancel", "取消"), name = "cancel" };
            cancel.style.height = 20;
            cancel.style.marginTop = 0;
            cancel.style.marginBottom = 0;
            cancel.style.marginLeft = 0;
            cancel.style.marginRight = 0;
            cancel.style.paddingLeft = 10;
            cancel.style.paddingRight = 10;
            cancel.style.fontSize = 12;
            cancel.style.color = StripInk;
            cancel.style.backgroundColor = CancelIdle;
            PerfLintStyle.Round(cancel, 4);
            PerfLintStyle.Border(cancel, new Color(1f, 1f, 1f, 0.34f));
            cancel.style.flexShrink = 0;
            cancel.RegisterCallback<PointerEnterEvent>(_ => cancel.style.backgroundColor = CancelHover);
            cancel.RegisterCallback<PointerLeaveEvent>(_ => cancel.style.backgroundColor = CancelIdle);
            strip.Add(cancel);

            // Sits on the strip's lower edge, so the shape of the remaining time is readable without reading it.
            var track = new VisualElement { name = "track", pickingMode = PickingMode.Ignore };
            track.style.position = Position.Absolute;
            track.style.left = 0;
            track.style.right = 0;
            track.style.bottom = 0;
            track.style.height = 2;
            track.style.backgroundColor = new Color(1f, 1f, 1f, 0.10f);
            var fill = new VisualElement { name = "fill", pickingMode = PickingMode.Ignore };
            fill.style.height = 2;
            fill.style.width = Length.Percent(0);
            track.Add(fill);
            strip.Add(track);

            return strip;
        }

        static Label Text(string name, int size, FontStyle style, Color color, float marginRight)
        {
            var l = new Label { name = name };
            l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = style;
            l.style.color = color;
            l.style.marginRight = marginRight;
            // Held at full width by default; the instruction opts back into shrinking. These are short and already
            // abbreviated — a clipped "本阶段剩 0:1" is worse than no clock at all.
            l.style.flexShrink = 0;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }

        // ── painting ──────────────────────────────────────────

        static void Paint(VisualElement strip, BenchmarkRunner.Progress p)
        {
            bool waiting = p.Phase == BenchmarkRunner.Phase.AwaitScene;
            bool sampling = p.Phase == BenchmarkRunner.Phase.Sample;

            // Amber while the run is waiting on something outside its control, accent while it is actually taking
            // numbers. The distinction is the one thing a glance has to carry: one of those states wants the user to
            // keep playing, the other wants them to keep still.
            Color tint = waiting ? StripAmber : sampling ? StripGood : StripAccent;

            SetLabel(strip, "dot", null, tint);
            SetLabel(strip, "headline", p.Headline, StripInk);
            SetLabel(strip, "runs", RunsLabel(p), StripDim);
            SetLabel(strip, "clock", Clock(p), StripDim);

            // Amber while it is waiting on the user, neutral while it is asking them to stay off the editor. Both
            // keep the chip: the shape is what makes the instruction findable, and it should not appear and vanish
            // as the phases turn over — a control that moves is harder to find than one that only changes colour.
            var instruction = strip.Q<Label>("instruction");
            if (instruction != null)
            {
                instruction.text = p.Instruction ?? "";
                instruction.style.color = waiting ? StripAmber : StripDim;
                instruction.style.backgroundColor =
                    string.IsNullOrEmpty(p.Instruction) ? Color.clear : waiting ? ChipAmber : ChipNeutral;
            }

            var track = strip.Q<VisualElement>("track");
            var fill = track?.Q<VisualElement>("fill");
            if (fill != null)
            {
                fill.style.backgroundColor = tint;
                fill.style.width = Length.Percent(PhaseFraction(p) * 100f);
            }
        }

        /// <summary>
        /// Which repetition this is, counted across the WHOLE thing the user pressed once.
        ///
        /// Pressing "record a baseline" now buys three repetitions and then two more for the calibration behind
        /// them. Counting each half on its own showed "run 3/3" and then, seconds later, "calibration 1/2" — so the
        /// bar announced it had finished and then started again, and the only way to know five was the number was
        /// to have read the dialog. It counts 1..5 straight through, and names which half it is in.
        ///
        /// The baseline's own repetition count is taken from the constant rather than from the session that just
        /// ended: by the time the calibration is running, its own spec is the only one the runner still has.
        /// </summary>
        static string RunsLabel(BenchmarkRunner.Progress p)
        {
            if (p.Repetitions <= 1) return "";

            if (BenchmarkIntent.IsRunningChainedCalibration)
            {
                int done = BenchmarkRunner.BaselineRepetitions;
                int total = done + p.Repetitions;
                return L.Tr($"run {done + p.RunNumber}/{total} · calibration",
                            $"第 {done + p.RunNumber}/{total} 轮 · 标定");
            }

            // A baseline with a calibration still queued behind it: the total the user was promised is both halves,
            // and saying 1/3 here would set them up for the same surprise from the other end.
            if (BenchmarkIntent.HasChainedCalibration)
            {
                int total = p.Repetitions + BenchmarkRunner.CompareRepetitions;
                return L.Tr($"run {p.RunNumber}/{total} · baseline",
                            $"第 {p.RunNumber}/{total} 轮 · 基线");
            }

            return L.Tr($"run {p.RunNumber}/{p.Repetitions}", $"第 {p.RunNumber}/{p.Repetitions} 轮");
        }

        static void SetLabel(VisualElement strip, string name, string text, Color color)
        {
            var e = strip.Q<VisualElement>(name);
            if (e == null) return;
            if (e is Label l && text != null) l.text = text;
            if (name == "dot") e.style.backgroundColor = color;
            else e.style.color = color;
        }

        /// <summary>
        /// The clock line. Says what is known and stays quiet about what is not: while waiting for a scene there is
        /// no end time to report, because the game — or the person playing it — decides when that phase ends. It
        /// counts up instead, which is a fact rather than a guess.
        /// </summary>
        static string Clock(BenchmarkRunner.Progress p)
        {
            if (p.Phase == BenchmarkRunner.Phase.AwaitScene)
                return L.Tr($"waiting {Mmss(p.PhaseElapsedSeconds)}", $"已等待 {Mmss(p.PhaseElapsedSeconds)}");

            string left = double.IsNaN(p.PhaseRemainingSeconds)
                ? "" : L.Tr($"{Mmss(p.PhaseRemainingSeconds)} left", $"本阶段剩 {Mmss(p.PhaseRemainingSeconds)}");
            string total = double.IsNaN(p.TotalRemainingSeconds)
                ? "" : L.Tr($"~{Mmss(p.TotalRemainingSeconds)} total", $"总计约剩 {Mmss(p.TotalRemainingSeconds)}");

            if (left.Length > 0 && total.Length > 0) return left + " · " + total;
            return left.Length > 0 ? left : total;
        }

        static string Mmss(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            int s = Mathf.RoundToInt((float)seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        /// <summary>How far through the current phase, for the bar. Zero for phases with no fixed length.</summary>
        static float PhaseFraction(BenchmarkRunner.Progress p)
        {
            if (double.IsNaN(p.PhaseRemainingSeconds)) return 0f;
            double total = p.PhaseElapsedSeconds + p.PhaseRemainingSeconds;
            return total <= 0 ? 0f : Mathf.Clamp01((float)(p.PhaseElapsedSeconds / total));
        }

        // ── the Game views ────────────────────────────────────

        static float ToolbarHeight()
        {
            try
            {
                float h = EditorStyles.toolbar != null ? EditorStyles.toolbar.fixedHeight : 0f;
                return h > 1f ? h : 21f;
            }
            catch { return 21f; }
        }

        /// <summary>
        /// The internal <c>UnityEditor.GameView</c> type, or null when this editor does not have one under that name.
        ///
        /// Asked of every loaded assembly rather than of one named guess. It shipped as
        /// <c>typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")</c>, which reads as "the editor
        /// assembly" and is not: EditorWindow lives in UnityEditor.CoreModule, GameView does not, and the editor's
        /// module split differs between versions. So the lookup returned null and the strip silently never
        /// appeared — no error, no missing feature anywhere else, just a bar that was never drawn.
        ///
        /// Found the only way it could be: reported from a running editor on a version the strip had never been
        /// opened on. Nothing about it is visible to a compile or to an EditMode test that does not ask this
        /// question, which is why asking it is now a test — see the guard named after this method.
        /// </summary>
        internal static Type ResolveGameViewType()
        {
            if (_gameViewType != null) return _gameViewType;

            // The documented home first, so the common case costs one lookup.
            _gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")
                            ?? Type.GetType("UnityEditor.GameView, UnityEditor");
            if (_gameViewType != null) return _gameViewType;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Editor types only live in editor assemblies, and walking every player assembly to find that out
                // is the sort of thing that shows up in domain-reload times.
                string name = asm.GetName().Name;
                if (name == null || name.IndexOf("UnityEditor", StringComparison.Ordinal) < 0) continue;
                try
                {
                    var t = asm.GetType("UnityEditor.GameView");
                    if (t != null) { _gameViewType = t; return t; }
                }
                catch { /* a assembly that refuses to answer is not the one we want */ }
            }
            return null;
        }

        static IEnumerable<EditorWindow> GameViews()
        {
            if (Application.isBatchMode) yield break; // no editor windows to paint on
            if (ResolveGameViewType() == null) yield break;

            UnityEngine.Object[] all;
            try { all = Resources.FindObjectsOfTypeAll(_gameViewType); }
            catch { yield break; }
            if (all == null) yield break;

            foreach (var o in all)
            {
                // Every open Game view gets one. They all render the scene, so whichever the user is looking at has
                // to be able to answer "what is happening" — and which one is frontmost is not reliably readable.
                if (o is EditorWindow w && w != null && w.rootVisualElement != null) yield return w;
            }
        }

        static VisualElement Find(VisualElement root)
        {
            foreach (var c in root.Children())
                if (c.name == ElementName) return c;
            return null;
        }

        static void RemoveAll()
        {
            foreach (var view in GameViews())
            {
                var strip = Find(view.rootVisualElement);
                if (strip != null) strip.RemoveFromHierarchy();
            }
        }
    }
}
