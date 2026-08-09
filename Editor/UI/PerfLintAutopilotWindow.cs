using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// The front door: one question, one answer, one button.
    ///
    /// It exists because the main panel is an instrument and the people this is for are not instrument readers. That
    /// panel puts a scan bar, a frame-rate headline, a budget ring, a target selector, a four-field recommendation and
    /// a findings list above the thing they came for — and no amount of restyling the before/after block fixes being
    /// the eighth item on a dense screen.
    ///
    /// So the rule here is subtractive: every screen states where you are, what to do next, and nothing else. Detail is
    /// not deleted, it is one link away in the main panel — all fourteen figures, the drift readings that set the bar, and
    /// the findings list. That division is the whole design: this window is allowed to be simple because the other one
    /// still exists.
    ///
    /// Colours follow the editor skin rather than the light mockups: a white panel docked in a dark editor reads as a
    /// web page someone embedded. What carries over from the mockups is the type scale, the spacing and the one-thing-
    /// at-a-time structure, which is what made them calm.
    /// </summary>
    public sealed class PerfLintAutopilotWindow : EditorWindow
    {
        [MenuItem("Tools/PerfLint/Autopilot", priority = 1)]
        public static PerfLintAutopilotWindow Open()
        {
            var win = GetWindow<PerfLintAutopilotWindow>();
            win.titleContent = new GUIContent("PerfLint Autopilot");
            // Taller than the old 320: the window now states where the loop stands above the three steps, and at
            // 320 px that header, the step strip and the card were all fighting for the same handful of rows.
            win.minSize = new Vector2(500, 470);
            win.Show();
            return win;
        }

        // ── palette ───────────────────────────────────────────
        //
        // Worked out here and now shared: the values, the geometry helpers and the stylesheet all live in
        // <see cref="PerfLintStyle"/>, which the other four windows wear too. The aliases below are kept because
        // this file names these colours about two hundred times and a rename would be churn with no reader.
        //
        // The reasoning, for anyone about to change a number: flat and quiet, following the editor window Tim picked
        // out as the target look — a dark canvas, one card lifted a shade above it, hairline rules instead of a box
        // around every block, and colour spent only on status. Start at the editor's own grey and build UPWARDS;
        // every level is lighter than the one it sits on, except Track, the recessed groove the step segments sit in.
        static bool Pro => PerfLintStyle.Pro;
        static Color Ink => PerfLintStyle.Ink;
        static Color Dim => PerfLintStyle.Dim;
        static Color Dimmer => PerfLintStyle.Dimmer;
        static Color SurfaceRaised => PerfLintStyle.SurfaceRaised;
        static Color SurfaceSoft => PerfLintStyle.SurfaceSoft;
        static Color Track => PerfLintStyle.Track;
        static Color Line => PerfLintStyle.Line;
        static Color Hair => PerfLintStyle.Hair;
        static Color Good => PerfLintStyle.Good;
        static Color Bad => PerfLintStyle.Bad;
        static Color Amber => PerfLintStyle.Amber;
        static Color Accent => PerfLintStyle.Accent;

        static Color Fade(Color c, float alpha) => PerfLintStyle.Fade(c, alpha);

        static void Round(VisualElement element, float radius) => PerfLintStyle.Round(element, radius);

        static void Border(VisualElement element, Color color, float width = 1) =>
            PerfLintStyle.Border(element, color, width);

        /// <summary>A block of secondary detail. Filled, not outlined — an outline around every block is what made the old screen read as a stack of boxes.</summary>
        static VisualElement SoftPanel(Color background)
        {
            var panel = new VisualElement
            {
                style =
                {
                    backgroundColor = background,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 8,
                    paddingBottom = 8
                }
            };
            Round(panel, 4);
            return panel;
        }

        /// <summary>A hairline rule. Zones are separated by a line and a gap rather than by boxing each one.</summary>
        static VisualElement Divider(float top = 10, float bottom = 10) => PerfLintStyle.Divider(top, bottom);

        /// <summary>A zone heading: small, bold, quiet. It labels the block under it without competing with the screen's own title.</summary>
        static VisualElement SectionHead(string text)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap,
                          marginBottom = 8 }
            };
            row.Add(new Label(text)
            {
                style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 12, color = Dimmer,
                          unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal }
            });
            return row;
        }

        /// <summary>Label left, control right — the settings row of the reference window. The control keeps its own width.</summary>
        static VisualElement MetaRow(string label, VisualElement control)
        {
            var row = MetaShell(label);
            control.style.flexShrink = 0;
            row.Add(control);
            return row;
        }

        /// <summary>
        /// The same row with a sentence on the right.
        ///
        /// NoWrap, unlike the control version: with the row allowed to wrap, a value longer than the space left
        /// jumps to its own full-width line and the label column it was supposed to line up with is left dangling
        /// above it. The value shrinks and wraps INSIDE its column instead, which is the point of the column.
        /// </summary>
        static VisualElement MetaText(string label, string value, Color? tint = null)
        {
            var row = MetaShell(label);
            row.style.flexWrap = Wrap.NoWrap;
            row.style.alignItems = Align.FlexStart;
            // flexShrink = 1 explicitly. UIElements defaults it to 0 — the opposite of CSS — so flexGrow and
            // minWidth = 0 together still leave a label at its content width, and a sentence 26 px too long simply
            // paints past the card and turns on the horizontal scrollbar. Measured, not guessed: w=390 in a 364 slot.
            row.Add(new Label(value)
            {
                style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 13, color = tint ?? Dim,
                          whiteSpace = WhiteSpace.Normal }
            });
            return row;
        }

        static VisualElement MetaShell(string label)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap,
                          marginBottom = 8 }
            };
            row.Add(new Label(label)
            {
                style = { width = 96, flexShrink = 0, fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal }
            });
            return row;
        }

        /// <summary>A drawn status bead. Never a glyph: the 2021/2022 editor fonts have no emoji and no fallback.</summary>
        static VisualElement Bead(Color tint)
        {
            var dot = new VisualElement
            { style = { width = 9, height = 9, flexShrink = 0, backgroundColor = tint } };
            Round(dot, 5);
            return dot;
        }

        /// <summary>
        /// A caveat: an amber block, with the text in it written as text.
        ///
        /// Fill plus a rule of the same hue, all the way round — the construction measured off the reference, where
        /// a 1 px border is what stops a low-alpha wash reading as grey. The 3 px left stripe it replaces marked the
        /// block without colouring it.
        ///
        /// It used to take a tint for the LABEL, and all three callers passed Amber — so the sentence was amber, on
        /// an amber rule, on an amber fill: one hue three times, at roughly half the contrast of ordinary body text.
        /// The parameter is gone rather than defaulted, so the next caller cannot reintroduce it. The block says
        /// this is a caveat; the writing in it is just writing.
        /// </summary>
        static VisualElement Notice(string text)
        {
            var panel = Themed(SoftPanel(Color.clear), "pl-note--warning");
            panel.style.marginBottom = 8;
            panel.Add(new Label(text)
            {
                style = { fontSize = 12, color = Ink, whiteSpace = WhiteSpace.Normal }
            });
            return panel;
        }

        ScrollView _body;
        VisualElement _header;
        VisualElement _card;
        Button[] _tabButtons;
        BenchmarkVerifyState _renderState;
        CurrentDiagnosis _renderDiagnosis;
        string _signature;
        string _runnerSignature;
        int _idlePolls;

        void CreateGUI()
        {
            PerfLintStyle.Apply(rootVisualElement);
            rootVisualElement.style.paddingLeft = 16;
            rootVisualElement.style.paddingRight = 16;
            rootVisualElement.style.paddingTop = 12;
            rootVisualElement.style.paddingBottom = 12;

            // Outside the ScrollView on purpose: it is the window's identity and the state of the loop, and both of
            // those have to stay true while you scroll a long round. It also keeps the step strip the first thing
            // INSIDE the scroll, which is what the layout guard pins.
            _header = new VisualElement { style = { flexShrink = 0 } };
            rootVisualElement.Add(_header);

            _body = new ScrollView { style = { flexGrow = 1 } };
            rootVisualElement.Add(_body);

            Render();

            // The measurement spans two domain reloads, which destroy this window and any delegate it registered, so
            // progress is polled rather than pushed — the same reason the main panel does.
            rootVisualElement.schedule.Execute(Poll).Every(500);
        }

        void OnFocus() => Render();

        void Poll()
        {
            // A measurement this window asked for has landed — act on WHY it was asked for before drawing anything,
            // or the screen renders against a baseline that was supposed to have just been replaced.
            if (BenchmarkIntent.TryConsumeFinished(out string intent))
            {
                if (BenchmarkIntent.ShouldPin(intent))
                {
                    string refusal = BenchmarkIntent.PinFinishedAsBaseline();
                    if (refusal != null)
                        EditorUtility.DisplayDialog(L.Tr("Baseline not set", "未能建立基线"), refusal, "OK");
                }
                Render();
                return;
            }

            // The runner's state is cheap to inspect and is the only thing that needs sub-second response. Loading
            // BenchmarkVerifyState and CurrentDiagnosis is not: on a real 651-finding project the two disk reads plus
            // ranking cost ~8.7 ms. The old comparison accidentally compared this short runner signature with the
            // much longer rendered signature, so they could NEVER match — the window rebuilt itself every 500 ms
            // forever, including while someone was clicking between tabs.
            string runner = RunnerSignature();
            if (runner != _runnerSignature)
            {
                Render();
                return;
            }

            if (BenchmarkRunner.IsRunning)
            {
                // StatusLine includes live repetition/progress text within one phase. Repaint that small card from the
                // cached models; never pay the disk/ranking cost just to advance a progress sentence.
                if (_renderState != null && _renderDiagnosis != null)
                    RenderLoaded(_renderState, _renderDiagnosis, rebuildShell: false);
                return;
            }
            if (HasPending) return;

            // Idle external changes (a scan or baseline produced in another panel) still arrive without requiring
            // focus, just not at animation frequency. The ordinary paths — a button in this window, focus returning,
            // or a runner phase change — refresh immediately.
            if (++_idlePolls < 4) return;
            _idlePolls = 0;

            var st = BenchmarkVerifyState.Load();
            var diag = CurrentDiagnosis.Load();
            string sig = RenderSignature(st, diag);
            if (sig == _signature) return;
            RenderLoaded(st, diag, rebuildShell: true);
        }

        // ── building blocks ───────────────────────────────────

        /// <summary>
        /// A screen's heading.
        ///
        /// 15.5 rather than 20: a finding title is a whole sentence carrying its own figures — "Runtime GC
        /// allocation: median 56.0 KB/frame (peak 173.4 KB) — this sample couldn't pin it to a method" — and at 20
        /// bold it wrapped to two lines and dominated the screen over the numbers and the button. Verdict headlines
        /// ("You cut 1.4 ms off every frame") are short and read fine either way, so the long case sets the size.
        /// </summary>
        static Label Title(string text) => new Label(text)
        {
            style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, color = Ink,
                      whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
        };

        static Label Body(string text, float size = 13, float opacity = 1f) => new Label(text)
        {
            style = { fontSize = size, color = Dim, whiteSpace = WhiteSpace.Normal, opacity = opacity,
                      marginBottom = 8 }
        };

        static Label Foot(string text) => new Label(text)
        {
            style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal }
        };

        /// <summary>The one thing to do. Sized and coloured to be the obvious target rather than one button among six.</summary>
        static Button Primary(string text, Action onClick) => Standalone(PerfLintStyle.Primary(text, onClick));

        static Button Secondary(string text, Action onClick) => Standalone(PerfLintStyle.Secondary(text, onClick));

        /// <summary>This window's buttons sit at the left of their own row, with the gap to their right.</summary>
        static Button Standalone(Button b)
        {
            b.style.marginLeft = 0; b.style.marginRight = 12;
            b.style.marginTop = 0; b.style.marginBottom = 0;
            b.style.alignSelf = Align.FlexStart;
            return b;
        }

        /// <summary>Hands a control's colours over to the stylesheet. See <see cref="PerfLintStyle.Themed{T}"/> for why it must.</summary>
        static T Themed<T>(T element, string className) where T : VisualElement =>
            PerfLintStyle.Themed(element, className);

        /// <summary>Which coloured block a verdict gets. Calibrated is a success and must not wear the warning colour.</summary>
        static string NoteClassFor(BenchmarkComparison.Outcome outcome) => outcome switch
        {
            BenchmarkComparison.Outcome.Proved => "pl-note--good",
            // Neutral, not green. Calibrated IS the calibration succeeding — that reasoning is sound and is why it was
            // kept out of the warning colour — but colour is the one signal on this screen a newcomer never has to
            // translate, and green means "success" across every piece of software they have used. Spending it on the
            // run where they did nothing and nothing happened teaches "green = I did not do anything", which is
            // precisely backwards for the one colour that has to mean something when a real fix lands.
            BenchmarkComparison.Outcome.Calibrated => "pl-note--accent",
            BenchmarkComparison.Outcome.Worse => "pl-note--bad",
            BenchmarkComparison.Outcome.Unproven => "pl-note--warning",
            _ => "pl-note--accent"
        };

        /// <summary>The class that washes a card in its own severity. Info is the quietest on purpose — most findings are Info.</summary>
        static string SeverityClass(Severity severity) => PerfLintStyle.SeverityClass(severity);

        /// <summary>
        /// Gives the shared finding actions the compact button look.
        ///
        /// Now a no-op in practice — <see cref="FindingCardUI.Actions"/> applies it itself, because the same row of
        /// buttons is drawn by every panel and there is no longer a reason for one of them to look different. Kept
        /// as the call site that says so, and because it is idempotent.
        /// </summary>
        static VisualElement ThemedActions(VisualElement actions) => PerfLintStyle.CompactActions(actions);

        static VisualElement Row() =>
            new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    marginTop = 8
                }
            };

        // ── render ────────────────────────────────────────────

        // ── tabs ──────────────────────────────────────────────
        //
        // Three screens rather than one, because the six states this window used to be were a state MACHINE: at any
        // moment it showed exactly one card and the rest of the loop was invisible. A reader could not see that there
        // were three steps, let alone where they were in them. The tabs are always present and always navigable, and
        // the last screen's action returns to the first — that loop is the product, not decoration.

        const string KTab = "PerfLint.Autopilot.Tab";
        const int TabConclusion = 0, TabRound = 1, TabVerify = 2;

        static int Tab
        {
            get => Mathf.Clamp(SessionState.GetInt(KTab, TabConclusion), TabConclusion, TabVerify);
            set => SessionState.SetInt(KTab, value);
        }

        void GoTab(int tab)
        {
            tab = Mathf.Clamp(tab, TabConclusion, TabVerify);
            if (Tab == tab) return;
            Tab = tab;

            // A tab changes only which already-loaded model is being presented. Reloading both persisted models here
            // made every click parse JSON, rebuild the comparison journal and sort hundreds of findings again. Keep
            // those models until a real external change, action, focus or poll tells us they are stale.
            if (_renderState == null || _renderDiagnosis == null)
            {
                Render();
                return;
            }

            _body.scrollOffset = Vector2.zero;
            RenderLoaded(_renderState, _renderDiagnosis, rebuildShell: false);
        }

        void Render(BenchmarkVerifyState st = null)
        {
            if (_body == null) return;
            st ??= BenchmarkVerifyState.Load();
            var diag = CurrentDiagnosis.Load();
            RenderLoaded(st, diag, rebuildShell: true);
        }

        static string RunnerSignature() =>
            BenchmarkRunner.CurrentPhase + "|" + HasPending;

        static string RenderSignature(BenchmarkVerifyState st, CurrentDiagnosis diag) =>
            st.Signature + "|" + RunnerSignature() + "|" + diag.Signature + "|" + Tab;

        void RenderLoaded(BenchmarkVerifyState st, CurrentDiagnosis diag, bool rebuildShell)
        {
            if (_body == null || st == null || diag == null) return;
            _renderState = st;
            _renderDiagnosis = diag;
            _runnerSignature = RunnerSignature();
            _signature = RenderSignature(st, diag);
            _idlePolls = 0;

            if (rebuildShell || _card == null)
            {
                BuildHeader(st, diag);

                _body.Clear();
                _body.Add(BuildTabBar());

                _card = new VisualElement
                {
                    style = { marginBottom = 8 }
                };
                _body.Add(_card);
                _body.Add(BuildFooter());
            }
            else
            {
                UpdateTabBar();
                _card.Clear();
            }

            // A measurement in flight owns the screen whichever tab you are on: nothing else on it is actionable
            // until it lands, and hiding that behind a tab is how somebody starts a second one.
            if (BenchmarkRunner.IsRunning) { RenderMeasuring(_card); }
            else if (HasPending) { RenderPending(_card); }
            else
            {
                // Above whichever tab you are on, because it is about the measurement that just landed rather than
                // about a screen — and the tab you are on when one lands is not something you chose: entering Play
                // Mode brings the Game view forward.
                var truth = BuildSceneTruthNotice(st);
                if (truth != null) _card.Add(truth);

                switch (Tab)
                {
                    case TabConclusion: RenderConclusion(_card, diag, st); break;
                    case TabRound: RenderRound(_card, diag, st); break;
                    default: RenderVerifyTab(_card, st); break;
                }
            }
        }

        // ── header ────────────────────────────────────────────

        /// <summary>
        /// The window's name, what it is for, and where the loop currently stands — above the steps, always visible.
        ///
        /// The three beads are not decoration and not a second copy of the tabs: the tabs say which screen you are
        /// looking at, the rail says which of the three things this loop needs actually EXISTS yet. Before this, a
        /// reader had to open each tab in turn to find out that there was no baseline, or that the baseline on record
        /// belonged to a scene they closed last week — and that second one reads as a bug when you discover it three
        /// clicks in.
        /// </summary>
        void BuildHeader(BenchmarkVerifyState st, CurrentDiagnosis d)
        {
            if (_header == null) return;
            _header.Clear();

            // One line, not five. The name, and the state of the loop, on the same row.
            //
            // The block this replaces was a 20 px title, a subtitle, a rule, three label/value rows on a drawn rail
            // and a second rule — about 110 px of chrome, a third of a short window, before anything you can act on.
            // Every one of those rows is still here; they are just states rather than a table, and a state that is
            // fine is worth one short phrase and a coloured dot.
            var top = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center }
            };
            top.Add(new Label("Autopilot")
            {
                style = { fontSize = 20, unityFontStyleAndWeight = FontStyle.Bold, color = Ink, marginRight = 16 }
            });
            foreach (var chip in StatusChips(st, d)) top.Add(chip);
            _header.Add(top);
            _header.Add(Divider(10, 12));
        }

        /// <summary>
        /// The three things this loop needs, as one dot and one phrase each.
        ///
        /// Wording is short where the state is fine and long where it is not: "273 findings" needs no explanation,
        /// while a baseline belonging to a scene you closed last week has to say so, because that is the state a
        /// reader would otherwise act on for ten minutes before finding out.
        /// </summary>
        IEnumerable<VisualElement> StatusChips(BenchmarkVerifyState st, CurrentDiagnosis d)
        {
            bool scanned = d != null && d.HasScan;
            yield return StatusChip(scanned ? Good : Dimmer,
                scanned
                    ? L.Tr($"{d.Scan.Findings.Count} findings", $"{d.Scan.Findings.Count} 条 findings")
                    : L.Tr("not scanned", "未扫描"));

            bool hasBaseline = st != null && st.HasBaseline;
            bool baselineHere = hasBaseline && st.BaselineDescribesSceneToMeasure();
            // "not this scene" is only true without a plan. With one, the open scene is beside the point and the
            // mismatch is against what measuring is AIMED at — so the chip has to name that instead, or it sends the
            // reader off to open a scene that would change nothing.
            string aiming = BenchmarkVerifyState.SceneToMeasureName();
            bool planned = !string.IsNullOrEmpty(BenchmarkVerifyState.PlannedSceneGuid());
            yield return StatusChip(baselineHere ? Good : hasBaseline ? Amber : Dimmer,
                baselineHere
                    ? L.Tr($"{st.Baseline.SceneName} · {Fmt(st.Baseline.FrameMsMedian)}",
                           $"{st.Baseline.SceneName} · {Fmt(st.Baseline.FrameMsMedian)}")
                    : hasBaseline
                        ? planned
                            ? L.Tr($"baseline is {st.Baseline.SceneName}, but measuring {aiming}",
                                   $"基线在 {st.Baseline.SceneName}，但要测的是 {aiming}")
                            : L.Tr($"baseline is {st.Baseline.SceneName}, not this scene",
                                   $"基线在 {st.Baseline.SceneName}，不是当前场景")
                        : L.Tr("no baseline", "无基线"));

            bool compared = st != null && st.HasComparison;
            yield return StatusChip(compared ? Good : Dimmer,
                compared
                    ? L.Tr("compared", "已复测")
                    : hasBaseline ? L.Tr("not re-measured", "未复测") : L.Tr("awaiting baseline", "待基线"));
        }

        static VisualElement StatusChip(Color tint, string text)
        {
            var chip = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 1,
                          minWidth = 0, marginRight = 12, marginTop = 4 }
            };
            var dot = Bead(tint);
            dot.style.marginRight = 8;
            chip.Add(dot);
            chip.Add(new Label(text)
            {
                style = { flexShrink = 1, minWidth = 0, fontSize = 12,
                          color = tint == Dimmer ? Dimmer : Dim, whiteSpace = WhiteSpace.Normal }
            });
            return chip;
        }

        VisualElement BuildTabBar()
        {
            _tabButtons = new Button[3];
            // A recessed track holding three segments, rather than three outlined buttons side by side. The track is
            // what carries the outline, so the segments themselves can be borderless and the selected one is simply
            // the raised surface — the same construction as the reference window's Custom/Cloud and stdio/http pairs.
            var bar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginBottom = 16, flexShrink = 0,
                          backgroundColor = Track,
                          paddingLeft = 4, paddingRight = 4, paddingTop = 4, paddingBottom = 4 }
            };
            Round(bar, 6);
            Border(bar, Line);

            void Add(int tab, string step, string text)
            {
                bool on = Tab == tab;
                var b = new Button(() => GoTab(tab)) { text = step + "  " + text };
                b.style.flexGrow = 1;
                b.style.flexBasis = 0;
                b.style.minWidth = 0;
                b.style.paddingTop = 8; b.style.paddingBottom = 8;
                b.style.paddingLeft = 8; b.style.paddingRight = 8;
                b.style.marginLeft = 0; b.style.marginRight = 0;
                b.style.marginTop = 0; b.style.marginBottom = 0;
                Round(b, 4);
                StyleTab(b, on);
                _tabButtons[tab] = b;
                bar.Add(b);
            }

            Add(TabConclusion, "1", L.Tr("Where you are", "当前结论"));
            Add(TabRound, "2", L.Tr("This round", "本轮修复"));
            Add(TabVerify, "3", L.Tr("Did it work", "验证结果"));
            return bar;
        }

        /// <summary>
        /// The three steps, styled to be read as the primary navigation they are.
        ///
        /// Bold on both states: at Normal weight the inactive two receded into the chrome, so the one structure a
        /// first-time reader most needs — that this is a three-step loop, and which step they are on — was the
        /// quietest text on screen. What separates the states now is the surface, not an outline: the selected
        /// segment is lifted and its text is full-strength ink, the other two sit flat in the track. That is the
        /// segmented control from the reference window, and it stops the strip reading as three competing buttons.
        /// </summary>
        static void StyleTab(Button button, bool on)
        {
            button.style.fontSize = 13;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            // Colours live in the stylesheet, not here. An inline background outranks :hover, which is what left
            // the strip as the one control in the window that did not answer the mouse.
            button.EnableInClassList("pl-tab", true);
            button.EnableInClassList("pl-tab--on", on);
        }

        void UpdateTabBar()
        {
            if (_tabButtons == null) return;
            for (int i = 0; i < _tabButtons.Length; i++)
                if (_tabButtons[i] != null) StyleTab(_tabButtons[i], Tab == i);
        }

        /// <summary>The verification loop, which is what this window used to be in its entirety.</summary>
        void RenderVerifyTab(VisualElement card, BenchmarkVerifyState st)
        {
            string blocked = MeasurementBlockedReason();
            if (blocked != null) { RenderBlockedByEditorState(card, blocked); return; }

            // Above everything else on this tab, because it decides what every screen below it is even about. A
            // reader who has not noticed that measurements are aimed at a scene they closed last week will read the
            // rest of this tab as broken.
            RenderScenePlan(card);

            // A plan naming a scene that is gone is the only thing worth acting on: every screen below would be
            // describing a measurement that cannot start, against a scene nobody can name. The plan block has already
            // said so in its own colour, so this stops rather than stacking a second, confused explanation under it —
            // which is what it did when first built ("baseline is Main, but measuring Main").
            if (BenchmarkScenePlan.Current.AnyMissing) return;

            bool deepProfileAlreadyAddressed = false;
            if (!st.HasBaseline) RenderNeedsBaseline(card, st);
            else if (!st.BaselineDescribesSceneToMeasure()) RenderWrongScene(card, st);
            else if (!st.HasComparison) RenderNeedsAfter(card, st);
            else deepProfileAlreadyAddressed = RenderResult(card, st);

            // Said once, wherever the measure button ends up: Deep Profile changes what the next measurement can
            // answer, and finding that out afterwards is finding it out too late. Suppressed when the screen has
            // just refused a comparison BECAUSE of Deep Profile — advertising what it buys directly underneath is
            // the screen arguing with itself.
            string mode = deepProfileAlreadyAddressed ? null : MeasurementModeNote();
            if (mode != null) card.Add(Foot(mode));
        }

        // ── the scene plan ────────────────────────────────────
        //
        // Two questions that look like one and are not: where Play Mode BOOTS, and which scene the measurement is
        // ABOUT. On any project with a boot sequence they have different answers — the editor holds Init because
        // that is the only scene the game will start from, the game loads its way to the level, and the level is the
        // thing worth numbers. Until this existed, every run on such a project was filed against Init.

        /// <summary>Whether the plan's editor is expanded. A window field, so it survives the rebuild each render does.</summary>
        bool _editingScenePlan;

        void RenderScenePlan(VisualElement card)
        {
            var plan = BenchmarkScenePlan.Current;

            // Wraps, like every other row on these screens that puts a button beside a sentence. It did not, and the
            // guard only caught it once this row appeared on the conclusion screen: at 600 px the button that opens
            // the picker is clipped away, on a window whose minimum width is 460. It was reachable on the
            // verification tab by luck of what else was on it, not by construction.
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center,
                          marginBottom = 10 }
            };
            // Body weight, not footnote weight. This sentence decides what every screen under it is about — a reader
            // who skims past it reads the rest of the tab as broken — and it shipped as the faintest, smallest text
            // on the tab, which is the styling for something safe to ignore.
            //
            // flexGrow 0 for the same reason as the conclusion screen's row: growing the sentence throws the button
            // at the far edge of the panel, and a control fifteen hundred pixels from what it acts on reads as
            // decoration. The row wraps, so a narrow window drops the button to its own line.
            row.Add(new Label(ScenePlanSummary(plan))
            {
                style = { fontSize = 13, color = plan.AnyMissing ? Bad : Dim, flexShrink = 1, minWidth = 0,
                          whiteSpace = WhiteSpace.Normal, flexGrow = 0, marginRight = 10 }
            });

            // Full size and the same words as everywhere else this control appears. It was compact here and
            // full-size on the first-run screen, and called "Change" here and "Choose the scene" there — one job,
            // three screens, and no reason for any of them to look or read differently.
            var toggle = Secondary(_editingScenePlan ? L.Tr("Done", "完成")
                                 : plan.IsEmpty ? L.Tr("Choose the scene", "设置场景")
                                 : L.Tr("Change the scene", "更改场景"),
                                   () => { _editingScenePlan = !_editingScenePlan; Render(); });
            toggle.style.marginRight = 0;
            toggle.style.flexShrink = 0;
            row.Add(toggle);
            card.Add(row);

            if (_editingScenePlan) card.Add(ScenePlanEditor(plan));
        }

        /// <summary>
        /// What the plan will do, in one sentence, phrased as the sequence that will actually happen.
        ///
        /// Names the scene rather than describing the setting, because "target scene: configured" tells a reader
        /// nothing they can check. A scene whose asset has gone says so outright — falling back to measuring
        /// whatever is open while the user believes a plan is in force is how a run gets filed under the wrong
        /// scene without anybody being told.
        /// </summary>
        static string ScenePlanSummary(BenchmarkScenePlan.Plan plan)
        {
            if (plan.StartMissing)
                return L.Tr("The scene set to boot from has been deleted or moved — pick it again.",
                            "设定的启动场景已被删除或移动——请重新选择。");
            if (plan.TargetMissing)
                return L.Tr("The scene set to be measured has been deleted or moved — pick it again.",
                            "设定要测量的场景已被删除或移动——请重新选择。");

            string start = BenchmarkScenePlan.NameOf(plan.StartPath);
            string target = BenchmarkScenePlan.NameOf(plan.TargetPath);

            if (plan.HasStart && plan.HasTarget)
                return L.Tr($"Boots {start}, waits until the game has loaded {target}, then measures.",
                            $"从 {start} 启动，等游戏加载出 {target} 后再开始测量。");
            if (plan.HasTarget)
                return L.Tr($"Starts from whatever scene is open, waits until the game has loaded {target}, then measures.",
                            $"从当前打开的场景启动，等游戏加载出 {target} 后再开始测量。");
            if (plan.HasStart)
                return L.Tr($"Opens {start} and measures it.", $"打开 {start} 并测量它。");

            var open = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            string name = open.IsValid() && !string.IsNullOrEmpty(open.name) ? open.name : "—";
            return L.Tr($"Measures whichever scene is open — right now {name}.",
                        $"测量当前打开的场景——现在是 {name}。");
        }

        VisualElement ScenePlanEditor(BenchmarkScenePlan.Plan plan)
        {
            var box = new VisualElement
            {
                style = { marginBottom = 12, paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 10 }
            };
            Round(box, 6);
            // Recessed, not raised. This is the one block on the tab that has to carry several lines of explanation
            // under each control, and SurfaceRaised — the lightest grey in the palette — left that explanation at
            // roughly 3:1 against its own background: present, and unreadable. Track is the far end of the same
            // palette, so the greys already used everywhere else get their contrast back without inventing a colour.
            // It also reads as what it is: a groove holding settings, rather than a card floating above the page.
            box.style.backgroundColor = PerfLintStyle.Track;
            Border(box, Line);

            box.Add(SceneRow(
                L.Tr("Boot from", "从此启动"),
                L.Tr("Play Mode starts here. Leave empty to start from whatever scene you have open.",
                     "Play Mode 从这里开始。留空则从你当前打开的场景启动。"),
                plan.StartGuid,
                guid => BenchmarkScenePlan.Save(guid, BenchmarkScenePlan.Current.TargetGuid)));

            box.Add(SceneRow(
                L.Tr("Measure", "测量"),
                L.Tr("Nothing is sampled until the game has loaded this scene — play to it if it takes a menu. Leave empty to measure the scene you booted from.",
                     "在游戏加载出这个场景之前不会开始采样——如果需要过菜单，正常玩到那里即可。留空则测量启动时那个场景。"),
                plan.TargetGuid,
                guid => BenchmarkScenePlan.Save(BenchmarkScenePlan.Current.StartGuid, guid)));

            if (!plan.IsEmpty)
            {
                var clear = PerfLintStyle.AsCompact(new Button(() =>
                {
                    BenchmarkScenePlan.Clear();
                    Render();
                })
                { text = L.Tr("Clear both", "两个都清空") });
                clear.style.alignSelf = Align.FlexStart;
                clear.style.marginLeft = 0;
                clear.style.marginTop = 4;
                box.Add(clear);
            }

            // Said here rather than after the fact: a plan pointing somewhere new makes the baseline on record
            // describe a different scene, and the next screen will ask for a new one. Better to know before picking.
            box.Add(new Label(L.Tr("A baseline belongs to the scene it was measured in. Point this somewhere else and you will be asked to record a new one.",
                                   "基线属于它被测量的那个场景。把这里指向别处后，会要求你重新记录一次基线。"))
            {
                style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginTop = 8 }
            });

            return box;
        }

        /// <summary>One labelled scene picker. Saves on change rather than behind a Save button — there is nothing to batch.</summary>
        VisualElement SceneRow(string label, string help, string guid, Action<string> onPicked)
        {
            var wrap = new VisualElement { style = { marginBottom = 8 } };

            var field = new UnityEditor.UIElements.ObjectField(label)
            {
                objectType = typeof(SceneAsset),
                allowSceneObjects = false,
                value = LoadSceneAsset(guid)
            };
            field.labelElement.style.minWidth = 78;
            field.labelElement.style.color = Ink;
            field.labelElement.style.fontSize = 12;
            field.style.marginLeft = 0;
            field.style.marginRight = 0;
            field.RegisterValueChangedCallback(evt =>
            {
                string picked = "";
                if (evt.newValue != null)
                {
                    string path = AssetDatabase.GetAssetPath(evt.newValue);
                    if (!string.IsNullOrEmpty(path)) picked = AssetDatabase.AssetPathToGUID(path);
                }
                onPicked(picked);
                Render();
            });
            wrap.Add(field);

            // The line that explains what leaving a picker empty does. It is the only place that is explained, so it
            // is not a footnote — Dim at 12, the same weight the rest of the window gives text somebody has to read.
            wrap.Add(new Label(help)
            {
                style = { fontSize = 12, color = Dim, whiteSpace = WhiteSpace.Normal, marginTop = 3, marginLeft = 2 }
            });
            return wrap;
        }

        static SceneAsset LoadSceneAsset(string guid)
        {
            string path = BenchmarkScenePlan.PathOf(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
        }

        // ── s1: where you are ─────────────────────────────────

        /// <summary>
        /// The one thing most worth doing, and the evidence behind saying so.
        ///
        /// Every claim on this screen carries where it came from, because the whole product position is that a
        /// conclusion is worth what its evidence is worth: an Editor measurement locates, a target device decides.
        /// The ranked list beside it is allowed to answer "?" — a bottleneck we cannot rank is shown as unrankable
        /// rather than given a made-up position, which is the honest state for GPU under Deep Profile.
        /// </summary>
        void RenderConclusion(VisualElement card, CurrentDiagnosis d, BenchmarkVerifyState st)
        {
            if (!d.HasScan) { RenderFirstRun(card, st); return; }

            // The target, and where the numbers judged against it came from — as the label/value rows they are.
            card.Add(BuildTargetRow(d));
            var unblock = BuildHeadlineAction(d);
            if (unblock != null) card.Add(unblock);
            card.Add(Divider(6, 14));

            if (!d.HasSteps)
            {
                card.Add(Title(L.Tr("Nothing is ranked above the rest", "没有明显更值得先做的项")));
                card.Add(Body(L.Tr("The scan found nothing that the current evidence ranks as worth doing first. Measure your scene to give the ranking something to work from.",
                                   "当前证据排不出哪一项更值得先做。采样一次你的场景，排序才有依据。")));
                card.Add(Primary(L.Tr("Go to the measurement", "去做测量"), () => GoTab(TabVerify)));
                return;
            }

            var gap = BuildEvidenceGapNotice(d, st);
            if (gap != null) card.Add(gap);

            // Urgency has to match the verdict standing one line above it. Presenting the top item as "priority #1"
            // directly under "you're inside your budget" claims a pressure the measurement has just disproved — the
            // main panel already learned this; the screen was contradicting itself.
            bool meeting = d.Measurement.StatusAgainst(d.Goal) == FrameStatus.Meeting;
            var top = d.Steps[0];

            card.Add(new Label(meeting
                    ? L.Tr("Nothing here is blocking your target — worth doing when you get to it",
                           "没有阻挡你目标的项——有空再做即可")
                    : L.Tr("Most worth doing · priority #1", "当前最值得处理 · 优先级 #1"))
            {
                style = { fontSize = 12, color = meeting ? Dimmer : Accent,
                          unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4,
                          whiteSpace = WhiteSpace.Normal }
            });
            card.Add(Title(top.Finding.GroupTitleOrTitle));
            card.Add(Body(top.WhyNow, 13));

            var tags = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 8 } };
            foreach (var t in EvidenceTags(top, d)) tags.Add(MetaTag(t.Text, t.Tint));
            card.Add(tags);

            var facts = Themed(SoftPanel(Color.clear), "pl-panel");
            facts.style.marginBottom = 4;
            Field(facts, L.Tr("Expected", "预期改善"), top.Expected);
            Field(facts, L.Tr("Risk", "风险"), top.Risk);
            Field(facts, L.Tr("Undo", "可否撤销"), top.Undo);
            card.Add(facts);

            // No second "this scene has not been measured" block here: BuildEvidenceGapNotice already says it at the
            // top of this screen, picks the right repetition count (a first measurement of a scene has to become its
            // baseline) and checks whether measuring is even possible right now. A copy added below it said the same
            // thing in different words and offered the same button with a different duration — 70s against 2 min for
            // one action — because it hardcoded the baseline count. Looking for the existing one first would have
            // cost nothing.
            var acts = Row();
            acts.Add(Primary(L.Tr("Start fixing this", "开始解决这个问题"), () => GoTab(TabRound)));
            // The same actions the other panels offer, from the same place — this screen used to have a "Show me"
            // that could not open a line and no way to apply anything at all.
            acts.Add(ThemedActions(FindingCardUI.Actions(top.Finding, new FindingCardUI.Context
            {
                Scan = d.Scan,
                Diagnosis = d,
                OnApplied = (msg, updated) => { ShowNotification(new GUIContent(msg)); Render(); }
            })));
            card.Add(acts);

            // Findings carry the advice that was true when they were produced. Deep Profile turned on since then
            // makes RUN.HOT003's "Enable Deep Profile and re-sample" read as an instruction you have already
            // followed — so the live state gets the last word, and names the step actually left.
            //
            // The quoted example used to be RUN.GC001's, and this note survives its departure rather than following
            // it: allocation is attributed from callstacks now, but the hotspot and frame-time rules still need
            // per-method markers, so the banner is still about them.
            if (d.Runtime != null && !d.Runtime.WasDeepProfile && UnityEditorInternal.ProfilerDriver.deepProfiling)
                card.Add(Foot(L.Tr("This measurement was taken with Deep Profile off, and you have turned it on since. Sample again and the findings will name the methods rather than telling you to enable it.",
                                   "这次采样是在 Deep Profile 关闭时做的，而你之后把它打开了。重新采样一次，findings 就会直接点出方法名，而不是让你去开启它。")));

            if (d.Steps.Count > 1) card.Add(BuildRoadmap(d));
        }

        /// <summary>
        /// What a project sees before it has been scanned or measured — the actual first screen of the product.
        ///
        /// It used to be one sentence and a button that opened a different window, which is a redirect rather than
        /// a start: the screen said "scan the project first" and then offered no way to scan, while the two things
        /// that DO begin the loop — pick the scene, record a baseline — sat on the third tab, whose own empty state
        /// calls itself "step one". Tim, opening it on a clean project: "S1，啥都做不了".
        ///
        /// So both steps are here, in the order they pay off. Neither is a gate: a baseline can be recorded before
        /// a scan exists, and the ranking simply has nothing to rank until the scan lands.
        /// </summary>
        void RenderFirstRun(VisualElement card, BenchmarkVerifyState st)
        {
            card.Add(Title(L.Tr("Nothing here yet", "这里还什么都没有")));
            card.Add(Body(L.Tr("Two things, once each, and this screen starts answering \"what should I do first\".",
                               "两件事各做一次，这一屏就会开始回答「我该先做什么」。")));

            var scan = Themed(SoftPanel(Color.clear), "pl-note--accent");
            scan.style.marginBottom = 12;
            scan.Add(StepHead(L.Tr("Step 1 · Scan the project", "第一步 · 扫描工程")));
            scan.Add(new Label(L.Tr("Finds the problems that can be found without running anything, and everything on this screen is ranked from it. No Play Mode.",
                                    "找出不需要运行就能发现的问题；这一屏的排序全部建立在它之上。不进 Play Mode。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });
            var scanRow = Row();
            scanRow.Add(Primary(L.Tr("Scan the project", "扫描工程"), () => PerfLintWindow.OpenAndScan()));
            scan.Add(scanRow);
            card.Add(scan);

            var bench = Themed(SoftPanel(Color.clear), "pl-note--accent");
            bench.style.marginBottom = 12;
            bench.Add(StepHead(L.Tr("Step 2 · Record a baseline", "第二步 · 记录一次基线")));
            bench.Add(new Label(L.Tr("Measures how it runs right now. Without it, nothing you change afterwards can be shown to have helped.",
                                     "测量它现在跑得怎么样。没有它，之后你改的任何东西都无法证明有没有用。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });
            // Which scene this is about, as a full-width sentence rather than the label/value row the measured
            // screen uses. On a wide panel that row pushes its button to the far edge, a thousand pixels from the
            // sentence it belongs to, and shrinks it to a compact one — so the single most consequential decision
            // on this screen looked like an afterthought. Tim: "这个设置场景是核心，现在不够显眼".
            //
            // It is consequential because getting it wrong is not recoverable by pressing the other button: on a
            // project that boots through an entry scene, measuring "whatever is open" spends three minutes and
            // comes back describing a scene the user did not choose.
            var plan = BenchmarkScenePlan.Current;
            bench.Add(new Label(ScenePlanSummary(plan))
            {
                style = { fontSize = 13, color = plan.AnyMissing ? Bad : Dim,
                          whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
            });

            // Both actions, same size, in the order they have to happen: decide what is being measured, then
            // measure it. The picker sits to the LEFT of the primary for that reason alone — this is the one screen
            // where the second button is useless until the first has been considered.
            var benchRow = Row();
            benchRow.Add(Secondary(plan.IsEmpty ? L.Tr("Choose the scene", "设置场景")
                                                : L.Tr("Change the scene", "更改场景"),
                                   () => { _editingScenePlan = !_editingScenePlan; Render(); }));
            benchRow.Add(Primary(L.Tr($"Record a baseline · about {MinutesFor(BaselineRunsIncludingCalibration)}",
                                      $"记录基线 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                                 () => StartMeasurement(BaselineRuns, baseline: true)));
            bench.Add(benchRow);
            if (_editingScenePlan) bench.Add(ScenePlanEditor(plan));

            string blocked = MeasurementBlockedReason();
            if (blocked != null) { benchRow.SetEnabled(false); bench.Add(Foot(blocked)); }
            card.Add(bench);
        }

        /// <summary>
        /// A step's heading on the first-run screen.
        ///
        /// Ink, not Accent. The first version used the accent colour and WindowStyleSyncTests caught it: accent text
        /// inside an accent block is one colour saying the same thing twice, at a fraction of the contrast of
        /// ordinary body text — the same defect the Scan panel's blue-on-blue notices were fixed for. The block
        /// carries the colour; the writing in it is writing.
        /// </summary>
        static Label StepHead(string text) => new Label(text)
        {
            style = { fontSize = 13, color = Ink, unityFontStyleAndWeight = FontStyle.Bold,
                      whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
        };

        /// <summary>
        /// Says the last measurement was taken somewhere other than where it was filed, and what was done about it.
        /// Null in the ordinary case, which is nearly always.
        ///
        /// This is the screen for the shape of project the Autopilot was worst at: an entry scene that loads a
        /// loading scene that loads the level. Nothing in the flow asks a first-time user which scene they mean —
        /// they open the window on Init and press the one button on it — so the run boots from Init, spends its five
        /// second warmup while the game loads its way to the level, and samples the level. The numbers were the
        /// level's all along; only the name was Init's.
        ///
        /// Two things are reported, and they are not degrees of the same thing:
        ///
        ///   Relabelled  every repetition sampled start-to-end in the same other scene. The measurement is kept and
        ///               re-filed under it — the two minutes are not wasted — and the offer is to make that scene
        ///               the standing target, so the next run WAITS for it rather than arriving there by timing.
        ///   Unusable    sampling straddled a scene load, or the repetitions landed in different scenes. Nothing is
        ///               relabelled, because there is no one scene to relabel it to.
        ///
        /// The offer disappears once the plan names that scene, so this cannot become a banner that outlives what it
        /// is about.
        /// </summary>
        VisualElement BuildSceneTruthNotice(BenchmarkVerifyState st)
        {
            var truth = st?.SceneTruth ?? default;
            bool relabelled = truth.Verdict == BenchmarkSceneTruth.Verdict.Relabelled;
            bool unusable = truth.Verdict == BenchmarkSceneTruth.Verdict.Unusable;
            if (!relabelled && !unusable) return null;

            // Nothing left to say once measuring is already aimed there: the plan does deliberately what this run
            // did by accident, so repeating the story would be a notice about a solved problem.
            bool alreadyPlanned = relabelled && !string.IsNullOrEmpty(truth.SceneGuid) &&
                string.Equals(BenchmarkVerifyState.PlannedSceneGuid(), truth.SceneGuid, StringComparison.Ordinal);
            if (alreadyPlanned) return null;

            var box = Themed(SoftPanel(Color.clear), relabelled ? "pl-note--accent" : "pl-note--warning");
            box.style.marginBottom = 12;

            string filed = truth.FiledUnderName;
            box.Add(new Label(relabelled
                    ? string.IsNullOrEmpty(filed)
                        ? L.Tr($"That measurement was taken in {truth.SceneName} — the game had loaded it before sampling started, so it is filed under {truth.SceneName}.",
                               $"这次测量是在 {truth.SceneName} 里做的——采样开始前游戏已经加载到了那里，所以结果记在 {truth.SceneName} 名下。")
                        : L.Tr($"That measurement was taken in {truth.SceneName}, not in {filed} — the game had loaded it before sampling started. It is filed under {truth.SceneName}.",
                               $"这次测量实际是在 {truth.SceneName} 里做的，不是 {filed}——采样开始前游戏已经加载到了那里。结果已记在 {truth.SceneName} 名下。")
                    : L.Tr("That measurement did not stay in one scene, so it does not describe any of them.",
                           "这次测量没有停留在同一个场景里，因此它不描述其中任何一个。"))
            { style = { fontSize = 13, color = Ink, whiteSpace = WhiteSpace.Normal,
                        unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 } });

            var names = truth.SceneNamesTouched;
            box.Add(new Label(relabelled
                    ? L.Tr($"The numbers are good — the whole sampling window was in {truth.SceneName}. It got there on its own timing though, so set it as the scene to measure and the next run will wait for it instead.",
                           $"数字本身是可用的——整个采样窗口都在 {truth.SceneName} 里。但这次是靠时机赶巧到那儿的，把它设为要测量的场景，下次测量就会明确等它加载出来再开始。")
                    : names.Count > 1
                        ? L.Tr($"Sampling ran across {string.Join(" → ", names)}. Set which scene to measure and take it again — the run will then wait until the game has loaded that scene before the clock starts.",
                               $"采样跨越了 {string.Join(" → ", names)}。请指定要测量的场景后重测——之后测量会等游戏加载出那个场景才开始计时。")
                        : L.Tr("Set which scene to measure and take it again — the run will then wait until the game has loaded that scene before the clock starts.",
                               "请指定要测量的场景后重测——之后测量会等游戏加载出那个场景才开始计时。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });

            var row = Row();
            if (relabelled)
                // Writes the target only. The start scene is deliberately left alone: the game has to boot from
                // wherever it boots from, and this run proved that route works.
                row.Add(Secondary(L.Tr($"Always measure {truth.SceneName}", $"以后固定测量 {truth.SceneName}"), () =>
                {
                    BenchmarkScenePlan.Save(BenchmarkScenePlan.Current.StartGuid, truth.SceneGuid);
                    Render();
                }));
            else
                row.Add(Secondary(L.Tr("Set the scene to measure", "设置要测量的场景"), () =>
                {
                    _editingScenePlan = true;
                    GoTab(TabVerify);
                }));
            box.Add(row);
            return box;
        }

        /// <summary>
        /// What the evidence under this screen is missing, and the one click that fixes it. Null when nothing is.
        ///
        /// TWO gaps, not one, and they are independent:
        ///
        ///   no measurement -> the RANKING is a guess. Real findings, ordered by static scan alone.
        ///   no baseline    -> the ROUND cannot be verified. There is no "before" for anything done next to beat.
        ///
        /// One block for both because they are answered by the same button and differ only in how many repetitions
        /// it takes — the file already records what splitting them costs, one screen down: a second copy said the
        /// same thing in different words and offered the same action at a different duration.
        ///
        /// The second gap was invisible until it was looked for, and only the first was ever checked here. The
        /// runtime panel saves a session without pinning anything, so anyone who sampled from there had a ranking
        /// backed by real numbers, no baseline, and NOTHING on this screen saying so. They found out on the round
        /// screen — which does say it, in a button — after the fixes had been applied, at which point the one thing
        /// that could have given them a before was already in the past. A before cannot be taken afterwards; that
        /// is the whole reason this is worth interrupting someone for.
        ///
        /// Deliberately a button rather than sampling automatically: measuring enters Play Mode for over a minute
        /// and cannot be done while you work, so it stays a decision.
        ///
        /// The cases read differently on purpose. Never measured is a blank; a measurement of ANOTHER scene is the
        /// one that misleads, because the window was showing its numbers a moment ago and the reader has no reason
        /// to suspect they moved on. And "measured, no before" is not a caveat about the list below at all — the
        /// list is fine — so it does not wear the caveat colour or take the primary button away from the work.
        /// </summary>
        VisualElement BuildEvidenceGapNotice(CurrentDiagnosis d, BenchmarkVerifyState st)
        {
            bool unmeasured = !d.Measurement.HasData;
            // A first measurement of a scene also becomes its baseline, or the verification screen has no before.
            bool needBaseline = st == null || !st.HasBaseline || !st.BaselineDescribesSceneToMeasure();
            if (!unmeasured && !needBaseline) return null;

            // Same shape as every other caveat on these screens (Notice): a tinted fill with the colour restated as a
            // rule down its left edge, rather than a differently-built yellow box that happens to live here.
            var box = Themed(SoftPanel(Color.clear), unmeasured ? "pl-note--warning" : "pl-note--accent");
            box.style.marginBottom = 12;

            bool elsewhere = d.Runtime != null && !d.RuntimeApplies;
            // The scene that was RUNNING, not every scene loaded around it — a project that loads additively lists
            // four names for one measurement, and reading them all out as "where it was taken" is both wrong and
            // unreadable.
            string other = elsewhere
                ? (!string.IsNullOrEmpty(d.Runtime.ActiveScene) ? d.Runtime.ActiveScene
                   : d.Runtime.Scenes != null && d.Runtime.Scenes.Count > 0 ? string.Join(", ", d.Runtime.Scenes) : null)
                : null;
            // Named, not "a different scene": the reader has to be able to tell whether that is the scene they meant.
            // Empty when the baseline has no scene path to name, and then the unnamed wording is used instead —
            // "The baseline on record was taken in ." is worse than not mentioning it.
            string baselineElsewhere = !unmeasured && st != null && st.HasBaseline
                                       && !string.IsNullOrEmpty(st.Baseline.SceneName)
                ? st.Baseline.SceneName : null;

            box.Add(new Label(unmeasured
                    ? elsewhere && other != null
                        ? L.Tr($"The only measurement on record was taken in {other}, so nothing below has seen this scene run.",
                               $"现有的唯一一次测量是在 {other} 做的，所以下面的内容都没见过当前场景运行时的样子。")
                        : L.Tr("This scene has not been measured, so nothing below has seen it run.",
                               "当前场景还没有被测量过，所以下面的内容都没见过它运行时的样子。")
                    : baselineElsewhere != null
                        ? L.Tr($"The baseline on record was taken in {baselineElsewhere}, so nothing you fix here can be measured against it.",
                               $"已有的基线记录在 {baselineElsewhere}，所以在这里修的任何东西都无法与它对比。")
                        : L.Tr("This scene has been measured, but nothing is pinned as the \"before\".",
                               "这个场景测过了，但没有任何一次被钉成「之前」。"))
            // Ink on a tinted block: the block already says what kind of thing this is, and the lead sentence is the
            // one thing on the screen that has to be read. Emphasis is weight and brightness; the hue belongs to the
            // block.
            { style = { fontSize = 13, color = Ink, whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 } });

            box.Add(new Label(unmeasured
                    ? L.Tr("The list is still real — it is the static scan, ordered by a guess at what is limiting you. A measurement replaces the guess.",
                           "列表本身是真实的——那是静态扫描，按「什么在拖累你」的推测排序。测一次就把推测换成事实。")
                    : L.Tr("The list below is backed by that measurement. Proving a fix worked needs one from before it, and a before cannot be taken afterwards — measuring now records it.",
                           "下面的排序有那次实测支撑。但要证明一处修复有效，需要的是改动之前的测量，而「之前」事后补不出来——现在测一次就会留下它。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });

            // No scene plan row here: BuildScenePlanMetaRow already stands above this block, on every state of this
            // screen rather than only on the ones with a caveat. A second copy inside the caveat is the duplication
            // this file keeps re-learning about — one control, one place per screen.
            int reps = needBaseline ? BaselineRuns : CompareRuns;
            // What the button PROMISES, which is not what it passes to the runner: a baseline drags its calibration
            // along behind it. Quoting the baseline's own three repetitions said "about 2 min" here while the
            // first-run screen said "about 3 min" for the identical action — two buttons, one job, two durations,
            // and the shorter one was the lie. Seen on a clean museum, in the two screens a new user meets first.
            int announced = needBaseline ? BaselineRunsIncludingCalibration : reps;
            string aiming = BenchmarkVerifyState.SceneToMeasureName();
            var row = Row();
            // Names the scene rather than saying "this scene". "This" is a claim about WHICH scene, and it is the
            // claim that is wrong on exactly the projects that need the plan — the line above now answers it, so the
            // button says the subject instead of pointing at something the reader has to infer.
            //
            // Same words as the verify screen uses for the same action, rather than a third phrasing for it. The
            // weight differs though: with no measurement at all this IS the next thing to do, while a missing before
            // is a prerequisite for verifying — real, but not a reason to take the primary button away from the work
            // the screen exists to point at.
            row.Add(unmeasured
                ? Primary(string.IsNullOrEmpty(aiming)
                              ? L.Tr($"Measure this scene · about {MinutesFor(announced)}", $"测量当前场景 · 约 {MinutesFor(announced)}")
                              : L.Tr($"Measure {aiming} · about {MinutesFor(announced)}", $"测量 {aiming} · 约 {MinutesFor(announced)}"),
                          () => StartMeasurement(reps, baseline: needBaseline))
                : Secondary(L.Tr($"Record a baseline · about {MinutesFor(announced)}", $"记录基线 · 约 {MinutesFor(announced)}"),
                            () => StartMeasurement(reps, baseline: true)));
            box.Add(row);

            string blocked = MeasurementBlockedReason();
            if (blocked != null) { row.SetEnabled(false); box.Add(Foot(blocked)); }
            return box;
        }

        /// <summary>An evidence label and the tone it is allowed to carry.</summary>
        readonly struct Evidence
        {
            public readonly string Text; public readonly Color Tint;
            public Evidence(string text, Color tint) { Text = text; Tint = tint; }
        }

        /// <summary>
        /// Where this conclusion's confidence comes from, and where it stops.
        ///
        /// Deliberately never says "confirmed on device": nothing in this window can. The Editor tags say what was
        /// measured here; the device tag says what is still unknown, and it is shown precisely because it is the tag
        /// a reader would otherwise assume in our favour.
        /// </summary>
        IEnumerable<Evidence> EvidenceTags(NextStep step, CurrentDiagnosis d)
        {
            bool runtime = step.Finding.Domain == Domain.Runtime;

            yield return runtime && d.RuntimeApplies
                ? new Evidence(L.Tr("Confirmed in the Editor", "Editor 已确认"), Good)
                : new Evidence(L.Tr("From the static scan", "来自静态扫描"), Dim);

            if (step.OffCriticalPath)
                yield return new Evidence(L.Tr("Not what's limiting you right now", "当前不是瓶颈"), Amber);

            if (d.Measurement.TimingsInflated)
                yield return new Evidence(L.Tr("Milliseconds are the profiler's", "毫秒是 Profiler 开销"), Amber);

            // The one tag this window may never omit: nothing here has been seen on the target device.
            yield return new Evidence(L.Tr("Device impact: unknown", "真机影响：未知"), Dimmer);
        }

        /// <summary>
        /// A fact about an item, written as text rather than drawn as a chip.
        ///
        /// This is the ONLY way a non-clickable fact is drawn in this window, and the exception that used to
        /// exist — a chip with a fill, a 1 px border and a radius, kept for "short" labels — is deleted rather
        /// than narrowed. It was read as a button twice: first as "report only — decide and change it yourself"
        /// on the round screen, then as "Confirmed in the Editor" on the conclusion screen. Length was never
        /// the variable. A border plus a corner radius IS the shape of a control on these screens, so anything
        /// wearing it and doing nothing is lying, at two words or at twelve.
        ///
        /// Italic with no chrome is how the reference writes the same kind of line (its .item-metadata).
        /// </summary>
        static VisualElement MetaTag(string text, Color tint) => new Label(text)
        {
            style = { fontSize = 12, color = tint, unityFontStyleAndWeight = FontStyle.Italic,
                      marginRight = 12, marginBottom = 2, whiteSpace = WhiteSpace.Normal,
                      flexShrink = 1, minWidth = 0 }
        };

        static void Field(VisualElement parent, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, paddingTop = 4, paddingBottom = 4 }
            };
            row.Add(new Label(label)
            {
                style =
                {
                    width = 96, flexShrink = 0, fontSize = 12, color = Dimmer,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            });
            row.Add(new Label(value)
            {
                style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 13, color = Dim, whiteSpace = WhiteSpace.Normal }
            });
            parent.Add(row);
        }

        /// <summary>
        /// What comes after, in order — with "?" for anything the evidence cannot rank.
        ///
        /// The unrankable slot is the point of this list rather than a corner case. GPU under Deep Profile is exactly
        /// that today: the profiler inflates only the CPU side, so a CPU-vs-GPU position would be manufactured.
        /// </summary>
        /// <summary>
        /// The target every number on this screen is judged against, where it is stated.
        ///
        /// This had no UI at all for a while, and the gap is worth recording. The selector used to live in the main
        /// panel's conclusion card, which was retired wholesale when these three screens took over "what should I do"
        /// — correctly, except that one control in it was not a duplicate of anything here. So the budget, the
        /// meeting/failing verdict, the ranking's relevance weighting and the story line were all being decided by a
        /// number in EditorPrefs with no way left to see or change it. Tim hit it by reading "a 8.3 ms budget for 120
        /// FPS" and asking where the 120 came from; the answer was a value set months earlier in a different project,
        /// since Unity keeps EditorPrefs in one machine-wide hive.
        ///
        /// Lesson for the next time a container is retired: a slimming pass has to account for controls, not screens.
        /// Everything else in that card was a second opinion. This was the input.
        /// </summary>
        VisualElement BuildTargetRow(CurrentDiagnosis d)
        {
            var goal = d.Goal;
            var box = new VisualElement();

            // GenericMenu rather than PopupField: PopupField<T> sits in UnityEditor.UIElements on 2021.3 and moved to
            // UnityEngine.UIElements later, so naming it breaks the package's declared minimum. Same choice, and the
            // same reason, as the row this replaces.
            //
            // The label stays exactly "<n> FPS": it is what the layout guard looks for, and more usefully it is what
            // the reader is looking for — the number every verdict on this screen is computed from.
            var fps = new Button { text = $"{goal.TargetFps} FPS" };
            fps.style.fontSize = 13;
            fps.style.color = Ink;
            fps.style.backgroundColor = SurfaceSoft;
            fps.style.paddingLeft = 12;
            fps.style.paddingRight = 12;
            fps.style.paddingTop = 4;
            fps.style.paddingBottom = 4;
            fps.style.marginLeft = 0;
            fps.style.marginTop = 0;
            fps.style.marginBottom = 0;
            fps.tooltip = L.Tr("Every budget and verdict on this screen is computed from this number.",
                               "这一屏的预算与达标判断全部由这个数字算出。");
            Round(fps, 4);
            Border(fps, Line);
            fps.clicked += () =>
            {
                var menu = new GenericMenu();
                foreach (int rate in PerfGoalPrefs.FpsChoices)
                {
                    int captured = rate;
                    menu.AddItem(new GUIContent($"{captured} FPS"), captured == goal.TargetFps,
                        () => { PerfGoalPrefs.SetFps(captured); Render(); });
                }
                menu.ShowAsContext();
            };
            box.Add(MetaRow(L.Tr("My target", "我的目标"), fps));

            // Where the numbers came from, before any number. Its refusals live here too — a capped or Deep Profile
            // reading says so in this sentence rather than being quietly graded against the budget.
            if (!string.IsNullOrEmpty(d.GoalLine))
                box.Add(MetaText(L.Tr("Where you stand", "当前状况"), d.GoalLine));

            // Which scene all of that is about — a standing row beside the target, not something that appears with a
            // caveat and leaves with it.
            //
            // It used to live inside the "this hasn't been measured" block, on the reasoning that the plan belongs
            // wherever a measure button is. That reasoning was wrong in a way only the running editor showed: the
            // block disappears the moment a measurement counts, taking the only way to see or change the scene with
            // it, and the first thing Tim did on the screen that finally worked was look for it and not find it.
            // The two rows above are "what am I aiming at" and "where am I"; this is "what is being measured", and
            // it is the same kind of fact.
            box.Add(BuildScenePlanMetaRow());

            return box;
        }

        /// <summary>The scene plan as a labelled row, matching the target and status rows it sits under.</summary>
        VisualElement BuildScenePlanMetaRow()
        {
            var plan = BenchmarkScenePlan.Current;
            var box = new VisualElement();

            var row = MetaShell(L.Tr("Measuring", "测量对象"));
            // flexGrow 0, and that single value is the whole fix. Growing the sentence pushed the button to the far
            // right edge of the panel — on a wide window, fifteen hundred pixels from the thing it acts on, at
            // compact size, which is the styling for something safe to ignore. It is not: on a project that boots
            // through an entry scene this is the control that decides whether the next three minutes measure the
            // scene the user meant. The row wraps (MetaShell), so a narrow window puts the button on its own line
            // rather than squeezing it.
            row.Add(new Label(ScenePlanSummary(plan))
            {
                style = { flexGrow = 0, flexShrink = 1, minWidth = 0, fontSize = 13,
                          color = plan.AnyMissing ? Bad : Dim, whiteSpace = WhiteSpace.Normal, marginRight = 10 }
            });

            // Full size, like the same control on the first-run screen. Two screens, one job, one look.
            var toggle = Secondary(_editingScenePlan ? L.Tr("Done", "完成")
                                 : plan.IsEmpty ? L.Tr("Choose the scene", "设置场景")
                                 : L.Tr("Change the scene", "更改场景"),
                                   () => { _editingScenePlan = !_editingScenePlan; Render(); });
            toggle.style.marginRight = 0;
            toggle.style.flexShrink = 0;
            row.Add(toggle);
            box.Add(row);

            if (_editingScenePlan) box.Add(ScenePlanEditor(plan));
            return box;
        }

        /// <summary>
        /// The one thing the headline tells you to do, as a button.
        ///
        /// Two of the headline's forms end with an instruction — "turn it off and sample again", "turn off VSync in
        /// Quality Settings" — and both are stated because the reading is UNUSABLE until it is followed. So on this
        /// screen that sentence is not context, it is the only step that matters: every number under it, the ranking,
        /// the whole "priority #1" framing, is waiting on it. Leaving it as prose asks a beginner to find a profiler
        /// setting or a quality level from a description, which is where this audience stops.
        ///
        /// Deep Profile is turned off outright — DeepProfileControl owns the recompile and the Play-Mode
        /// confirmation, so the risky part is already handled and stated.
        ///
        /// VSync only OPENS Quality Settings. Turning it off writes a project-wide setting affecting every build and
        /// every developer on the project, and the other half of the instruction (an uncapped target frame rate) lives
        /// in the user's own code, which we cannot touch — a button that silently did the half it can reach would
        /// leave the reading capped anyway and claim otherwise.
        /// </summary>
        VisualElement BuildHeadlineAction(CurrentDiagnosis d)
        {
            if (!d.Measurement.HasData) return null;

            if (d.Measurement.TimingsInflated && DeepProfileControl.Enabled)
            {
                var b = Secondary(L.Tr("Turn Deep Profile off", "关闭 Deep Profile"), () =>
                {
                    if (DeepProfileControl.Set(false)) Render();
                });
                b.tooltip = L.Tr("Recompiles scripts (and leaves Play Mode if you are in it), then sample again for a frame time you can judge against your target.",
                                 "会重新编译脚本（若在 Play Mode 中会退出），之后重新采样，才能得到可以对着目标判断的帧时间。");
                b.style.marginBottom = 12;
                return b;
            }

            if (d.Measurement.FrameRateCapped)
            {
                var b = Secondary(L.Tr("Open Quality Settings", "打开 Quality Settings"), () =>
                    SettingsService.OpenProjectSettings("Project/Quality"));
                b.tooltip = L.Tr("Set VSync Count to \"Don't Sync\" on the quality level you are running, then sample again. If your own code sets Application.targetFrameRate, raise or remove that too.",
                                 "把当前使用的质量等级的 VSync Count 设为「Don't Sync」，然后重新采样。如果你自己的代码设了 Application.targetFrameRate，也要一并放开。");
                b.style.marginBottom = 12;
                return b;
            }

            return null;
        }

        VisualElement BuildRoadmap(CurrentDiagnosis d)
        {
            // A rule and a heading rather than another filled box: by this point the screen already has the facts
            // panel and possibly two caveats, and a fourth outlined block turns the whole card into a stack of boxes.
            var box = new VisualElement();
            box.Add(Divider(16, 12));
            box.Add(SectionHead(L.Tr("After that", "接下来")));

            for (int i = 1; i < d.Steps.Count; i++)
            {
                var s = d.Steps[i];
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, marginBottom = 8 }
                };
                var number = new Label((i + 1).ToString())
                {
                    style =
                    {
                        width = 24, height = 24, flexShrink = 0, fontSize = 12, color = Accent,
                        unityTextAlign = TextAnchor.MiddleCenter, unityFontStyleAndWeight = FontStyle.Bold,
                        backgroundColor = Fade(Accent, Pro ? 0.28f : 0.14f), marginRight = 8
                    }
                };
                Round(number, 12);
                row.Add(number);
                var b = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
                b.Add(new Label(s.Finding.GroupTitleOrTitle)
                { style = { fontSize = 13, color = Ink, whiteSpace = WhiteSpace.Normal } });
                b.Add(new Label(s.OffCriticalPath
                        ? L.Tr("real, but not what's limiting you right now", "确实存在，但当前不是瓶颈")
                        : s.WhyNow)
                { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal } });
                row.Add(b);
                box.Add(row);
            }

            // Unrankable, named. A position we cannot justify is worse than an admitted blank.
            if (d.Measurement.HasData && d.Measurement.Side == Bottleneck.Unknown)
            {
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, marginTop = 4 }
                };
                var unknown = new Label("?")
                {
                    style =
                    {
                        width = 24, height = 24, flexShrink = 0, fontSize = 12, color = Amber,
                        unityTextAlign = TextAnchor.MiddleCenter, unityFontStyleAndWeight = FontStyle.Bold,
                        backgroundColor = Fade(Amber, Pro ? 0.28f : 0.14f), marginRight = 8
                    }
                };
                Round(unknown, 12);
                row.Add(unknown);
                var b = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
                b.Add(new Label(L.Tr("Whether the GPU is the bottleneck", "GPU 是不是瓶颈"))
                { style = { fontSize = 13, color = Dim, whiteSpace = WhiteSpace.Normal } });
                b.Add(new Label(d.Measurement.TimingsInflated
                        ? L.Tr("can't be ranked — Deep Profile inflates the CPU side only, so any CPU-vs-GPU split would be manufactured",
                               "排不出来——Deep Profile 只放大 CPU 一侧，据此判 CPU/GPU 比例是凭空造出来的")
                        : L.Tr("can't be ranked — the GPU reading didn't pass its own integrity check",
                               "排不出来——GPU 读数没通过自身的完整性校验"))
                { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal } });
                row.Add(b);
                box.Add(row);
            }

            return box;
        }

        // ── s2: this round ────────────────────────────────────

        /// <summary>
        /// The three things this round is allowed to be, and what each of them actually costs you to accept.
        ///
        /// Three because a round that moves one axis is a round whose result can be attributed; more than that and
        /// the verification screen can only say "something changed". Each item states its real action — a one-click
        /// fix, a change only you can decide, or a report — rather than implying everything here is automatic.
        /// </summary>
        void RenderRound(VisualElement card, CurrentDiagnosis d, BenchmarkVerifyState st)
        {
            if (!d.HasSteps)
            {
                card.Add(Title(L.Tr("Nothing queued for this round", "本轮没有待处理项")));
                card.Add(Body(L.Tr("Scan or measure first — this screen is the top of that ranking, nothing more.",
                                   "先扫描或测量——这一屏只是那份排序的前几项。")));
                card.Add(Primary(L.Tr("Where you are", "看当前结论"), () => GoTab(TabConclusion)));
                return;
            }

            // 20, not 16: in the reference window a section heading ("Connection", "AI agent") is one of the
            // loudest things on screen, and it is what makes the window's structure legible at a glance. Only the
            // round screen gets it for now — this is the one screen being tried out.
            var heading = Title(L.Tr("This round: the best-evidenced items only", "这一轮只处理证据最充分的项"));
            heading.style.fontSize = 20;
            card.Add(heading);
            card.Add(Body(L.Tr("Anything that changes how the game plays or looks is never applied for you.",
                               "凡是会改变玩法或画面的项，绝不替你应用。"), 12));
            card.Add(Divider(2, 12));

            // Which figure this round will not be able to attribute, named before anything is applied.
            //
            // The screen used to claim "one axis per round is what makes the result attributable", which is backwards:
            // three items on three axes move three different figures and each movement has one plausible cause, while
            // three items on ONE axis all land on the same figure and none of them can be told apart. What matters is
            // the collision, not the count of axes — so it is reported rather than legislated.
            string collision = CollidingAxisNote(d.Steps);
            if (collision != null)
                card.Add(Notice(collision));

            // Why this round is about download size when the target you typed was a frame rate.
            //
            // Once the frame goal is met, Relevance drops the frame-time axes to 0.25 and lifts BuildSize to 0.55, so
            // the ranking turns to build size and memory on its own. That is the right thing to rank — but the user
            // chose exactly one goal, FPS, and nobody asked them about download size. Reading a screen headed "240 FPS"
            // whose entire round is asset dedup, with no sentence connecting the two, the switch looks like the tool
            // lost the plot rather than finished the job.
            //
            // Stated, not asked: the ranking still decides, this only stops it happening silently. Turning it into a
            // question ("what next — build size, memory, or keep hunting stutter?") is the larger version of this and
            // is not what was chosen.
            if (d.Measurement.StatusAgainst(d.Goal) == FrameStatus.Meeting)
            {
                bool anyFrameWork = false;
                foreach (var s in d.Steps)
                    foreach (var a in s.Axes)
                        if (a == PerfAxis.CpuFrameTime || a == PerfAxis.GpuFrameTime || a == PerfAxis.Stutter)
                            anyFrameWork = true;
                if (!anyFrameWork)
                    card.Add(new Label(L.Tr($"Your {d.Goal.TargetFps} FPS goal is met here in the Editor, so this round is ranked on what ships and what it holds in memory — not on frame time. Change the target above if that is not what you want next.",
                                            $"你的 {d.Goal.TargetFps} FPS 目标在本机 Editor 下已达标，所以这一轮按「发布体积」和「常驻内存」排序，不再按帧时间。若这不是你接下来想做的，改上面的目标即可。"))
                    {
                        style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
                    });
            }

            // Which of these buttons this reader can actually press.
            //
            // The target reader for this window is someone opening it for the first time, and a first install is Free.
            // Every one-click fix routes through FindingActions.ApplyRule(interactive: true) -> Entitlements.RequirePro,
            // and both optimize buttons through RunOptimizePlan -> RequirePro. Measured on a Free install of the
            // reference project: of everything offered on this screen, the number that changes the project is zero —
            // the rest navigate. Discovering that by clicking, one button at a time, is the worst way to learn it.
            //
            // Stated, not sold: what is gated, what is not, no price and no upgrade button. The pricing decision is
            // not this line's to argue.
            if (!PerfLint.Licensing.Entitlements.IsPro)
            {
                int gated = d.Applicable.Count;
                foreach (var s in d.Steps) if (d.IsOneClickFixable(s.Finding)) gated++;
                if (gated > 0)
                    card.Add(new Label(L.Tr($"You're on Free. The {gated} one-click fixes here — and the optimize buttons at the bottom — need Pro; clicking them shows an upgrade prompt instead of running. Locate and the full-panel links work as normal.",
                                            $"你当前是 Free 版。这一屏的 {gated} 项一键修复、以及底部的一键优化需要 Pro——点击会弹升级提示而不会执行。定位与「去完整面板处理」不受影响。"))
                    {
                        style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
                    });
            }

            var ctx = new FindingCardUI.Context
            {
                Scan = d.Scan,
                Diagnosis = d,
                OnApplied = (msg, updated) => { ShowNotification(new GUIContent(msg)); Render(); }
            };

            int n = 0;
            string saidAlready = null;
            foreach (var s in d.Steps)
            {
                n++;
                // The wash and the rule come with the card now — this window was where that treatment was worked out,
                // and undoing the shared shell to re-apply it here was the last trace of that. Only the roomier box
                // is this window's own: a round lists at most a handful of these and can afford the space.
                var item = FindingCardUI.Card(s.Finding.Severity);
                item.style.paddingLeft = 12;
                item.style.paddingRight = 12;
                item.style.paddingTop = 12;
                item.style.paddingBottom = 12;
                item.style.marginTop = 0;
                item.style.marginBottom = 8;
                Round(item, 12);

                // Wraps. Measured in the live editor at 600 px (this window's minimum is 460): the actions
                // container laid out at xMax 690 inside a 510-wide card, and the ScrollView is vertical-only — so
                // "Show the 2 allocating scripts" was not scrolled off, it was CLIPPED AWAY, and the only action
                // on the top-ranked item could not be clicked at all. minWidth = 0 on the title is necessary and
                // was not sufficient; letting the row wrap is what actually keeps the buttons inside.
                var head = new VisualElement
                { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, flexWrap = Wrap.Wrap } };
                var number = new Label(n.ToString())
                {
                    style =
                    {
                        width = 24, height = 24, flexShrink = 0, fontSize = 12, color = Ink,
                        unityTextAlign = TextAnchor.MiddleCenter, unityFontStyleAndWeight = FontStyle.Bold,
                        backgroundColor = Fade(Ink, Pro ? 0.16f : 0.09f), marginRight = 8
                    }
                };
                Round(number, 12);
                head.Add(number);

                // Title over its own identifier, the way the reference window titles an item ("Assets / Copy" with
                // "assets-copy" beneath it). The rule id is not decoration: it is what a support thread, the CLI, the
                // /r/ short links and the main panel's search all take as the name of this thing, and it was the one
                // place the ranking never said it.
                // marginRight is a gutter, not spacing. Measured in the live editor: the title's Label ends 3 px past
                // its own column while the actions container begins exactly where that column does, so the last
                // wrapped line of the title ran underneath the button — the two boxes touch, and a Label does not
                // clip. minWidth = 0 has to be on the intermediate row as well, or a flex item's default
                // min-width:auto stops the whole column shrinking however small the Label inside it is allowed to be.
                var titles = new VisualElement
                { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, marginRight = 12 } };
                var titleRow = new VisualElement
                { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart,
                            flexShrink = 1, minWidth = 0 } };
                titleRow.Add(FindingCardUI.Dot(s.Finding.Severity));
                // minWidth = 0 or the title never shrinks below its own text: a flex item defaults to
                // min-width:auto, and the Actions row beside it is flexShrink = 0 by design — so the buttons get
                // pushed out of the card and clipped by the window edge instead. Seen in a screenshot at 600 px,
                // which is above this window's own 460 px minimum: "Show the 2 allocating scripts" rendered as
                // "Sho", i.e. the one action on the item was unreachable. Same pair the main panel's rule header
                // is already held to by RuleHeader_TitleShrinksAndWraps_ButtonsDoNotShrink.
                titleRow.Add(new Label(s.Finding.GroupTitleOrTitle)
                { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 13, color = Ink,
                            whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold } });
                titles.Add(titleRow);
                titles.Add(new Label(s.Finding.RuleId)
                { style = { marginLeft = 18, fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal } });
                head.Add(titles);

                // The actions ride the title line, hard right, instead of sitting on a line of their own under the
                // paragraph. Two buttons were costing a whole row plus its margin per item — about a quarter of the
                // card's height — while the space beside a two-word title sat empty. Same buttons, same order, one
                // row fewer.
                //
                // Three things keep this safe at width, and all three are load-bearing (the same clipping this file
                // already fixed once is what happens if any of them goes):
                //   · `head` wraps, so when the title can shrink no further the whole action block drops to its own
                //     line rather than being cut off by the card edge -- the ScrollView is vertical-only, so
                //     anything past the right edge is gone, not scrollable.
                //   · the block never shrinks (flexShrink = 0), so a long title squeezes itself, never the buttons.
                //   · maxWidth 100% + wrap is the floor: once it is alone on its own line and STILL too wide -- three
                //     actions with a filename in one of them -- it wraps its buttons instead of overflowing. Without
                //     the maxWidth, flexShrink = 0 means it would simply never wrap.
                var actions = ThemedActions(FindingCardUI.Actions(s.Finding, ctx));
                if (actions.childCount > 0)
                {
                    actions.style.flexShrink = 0;
                    actions.style.flexWrap = Wrap.Wrap;
                    actions.style.maxWidth = Length.Percent(100);
                    actions.style.justifyContent = Justify.FlexEnd;
                    head.Add(actions);
                }
                item.Add(head);

                var body = new VisualElement { style = { marginLeft = 32, marginTop = 8 } };
                // WhyNow, not Expected: most findings have no honest estimate, so Expected is a sentence saying so —
                // correct in a four-field card, useless as a description, and it rendered as "No reliable estimate
                // for this one" under item after item while the informative sentence went unused.
                // WhyNow is generated per AXIS, so a round whose items share one produces the same paragraph word
                // for word under every item — three copies of "Memory climbed 741 MB during the sample…" was the
                // observed case. Repetition reads as three separate observations that happen to coincide, when it is
                // one observation printed three times; and the note at the top of this screen has already said these
                // items move the same figure. Said once, then referred back to.
                bool repeat = string.Equals(s.WhyNow, saidAlready, StringComparison.Ordinal);
                saidAlready = s.WhyNow;
                body.Add(new Label(repeat
                        ? L.Tr("Same reason as above.", "理由同上。")
                        : s.WhyNow)
                { style = { fontSize = 13, color = repeat ? Dimmer : Dim, whiteSpace = WhiteSpace.Normal } });
                if (s.HasEstimate)
                    body.Add(new Label(s.Expected)
                    { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal } });

                var tags = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
                // The category tag explains the ABSENCE of a plain Fix button. When there IS one, "one-click fix" next
                // to a "Fix" button is pure redundancy (Tim flagged it) — the button already says it, so skip the tag.
                if (!d.IsOneClickFixable(s.Finding))
                {
                    var (label, tint) = ActionKind(s.Finding, d);
                    tags.Add(MetaTag(label, tint));
                }
                if (s.OffCriticalPath) tags.Add(MetaTag(L.Tr("not the bottleneck", "当前不是瓶颈"), Amber));
                // Said on the item, not only in the footnote under the button: the reader decides what to do here,
                // and "the re-measurement will not be able to see this" is part of that decision.
                // BlindTag builds a chip; on this screen it joins the same metadata line as the rest.
                if (!PerfAxisInfo.AnyMeasurableInPlayMode(s.Axes))
                {
                    var primary = s.Axes != null && s.Axes.Count > 0 ? s.Axes[0] : PerfAxis.None;
                    tags.Add(MetaTag(L.Tr($"{AxisName(primary)} — re-measuring can't see it",
                                          $"{AxisName(primary)}——复测看不到"), Amber));
                }
                body.Add(tags);
                item.Add(body);
                card.Add(item);
            }

            // The three blocks below the round are ordered by what is IN them, not by how executable they are.
            //
            // They used to be laid out in a fixed order — one-click, then needs-a-decision, then no-button — which is
            // an ordering by how much PerfLint can do for you. The ranking is an ordering by how much the work is
            // worth, and on the reference project the two came out backwards:
            //
            //   本轮三项      top 0.349
            //   顺手可做      top 0.293   (8 items)
            //   需要你决定    top 0.138   (1 item)
            //   没有一键      top 0.302   <- highest of the three, drawn last, off the bottom of the screen
            //
            // ASSET.AARES001 ranks 4th overall, immediately behind the round itself, and sat under everything on the
            // screen because nobody can click it. Grouping still earns its place — "PerfLint can do this for you" is a
            // real distinction and the blocks keep it — but it is not allowed to decide what you read first.
            var blocks = new List<(double Top, VisualElement El)>();
            void Offer(IReadOnlyList<NextStep> steps, Func<VisualElement> build)
            {
                if (steps == null || steps.Count == 0) return;
                var el = build();
                if (el == null) return;   // the manual block returns null when its rows are all under the size bar
                double top = 0;
                foreach (var s in steps) if (s.Score > top) top = s.Score;
                // Insertion, strictly greater: keeps the declared order for ties, so an unchanged project never
                // reshuffles its own blocks between renders. List.Sort would not guarantee that.
                int at = blocks.Count;
                for (int i = 0; i < blocks.Count; i++) if (top > blocks[i].Top) { at = i; break; }
                blocks.Insert(at, (top, el));
            }

            Offer(d.Applicable, () => BuildApplicableBlock(d, ctx));
            Offer(d.Decisions, () => BuildDecisionsBlock(d, ctx));
            Offer(d.Manual, () => BuildManualBlock(d, ctx));

            foreach (var b in blocks) card.Add(b.El);

            // The sentence that keeps the next screen honest, said before anything is applied rather than after.
            var scope = Themed(SoftPanel(Color.clear), "pl-panel");
            scope.style.marginTop = 16;
            scope.style.marginBottom = 8;
            scope.Add(new Label(L.Tr("After applying, this will only claim whether the same hotspot fell in the Editor under the same conditions. With no device data it will not say your target device's frame rate or memory is met.",
                                     "应用后只宣称「Editor 同条件下这一项是否下降」。没有真机数据时，不会声称目标设备的 FPS 或内存已经达标。"))
            {
                style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal }
            });
            card.Add(scope);

            card.Add(BuildRoundActions(d, st));
        }

        /// <summary>
        /// "Worth doing, but not by me": the findings ranked below the round whose payoff needs a decision — a
        /// confirmation, a chooser, a verifier — so PerfLint will not apply them for you.
        ///
        /// This block exists because the previous pair of lists had a hole exactly the shape of the product's headline
        /// feature. The round is three items; <see cref="CurrentDiagnosis.Applicable"/> rescues the reversible one-click
        /// fixes below it. Everything Action-shaped in between appeared in neither, and the biggest one is
        /// ASSET.AADUP001 — 366 duplicate-packed assets on a real project, none of them on this screen, while the panel
        /// two tabs over listed every one. "The tool did not detect it" was the reasonable conclusion, and it was wrong:
        /// it detected it, ranked it 4th or later, and then had nowhere to put it.
        ///
        /// Deliberately below the applicable block and visually quieter than the round: these are real work, but they
        /// are not this round's work, and the ranking's one visual statement is that the top of the screen is what is
        /// costing you. The buttons come from <see cref="FindingCardUI.Actions"/>, which routes an Action to the full
        /// panel rather than running it inline — the rule that keeps ASSET.DUP001 from re-running its hashing scanner
        /// (and OOM-crashing the editor) from a card that never meant to own that flow.
        /// </summary>
        VisualElement BuildDecisionsBlock(CurrentDiagnosis d, FindingCardUI.Context ctx)
        {
            var box = new VisualElement();
            box.Add(Divider(14, 12));
            box.Add(new Label(L.Tr($"Worth doing, but needs your decision — {d.Decisions.Count} item(s)",
                                   $"值得做，但需要你来决定——{d.Decisions.Count} 项"))
            { style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = Ink, marginBottom = 4,
                        whiteSpace = WhiteSpace.Normal } });
            box.Add(new Label(L.Tr("PerfLint will NOT apply these for you: each needs a confirmation, a choice, or a verified run, which the full panel owns. Ranked below the round, so they are not what is limiting you right now.",
                                   "这些 PerfLint 不会替你执行：每一项都需要确认、选择或带验证的执行，由完整面板负责。它们排序在本轮之后，不是当前的瓶颈。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });

            foreach (var s in d.Decisions)
            {
                // How many the main panel says, which is findings under this rule — NOT paths, and not FixablePlaces
                // (always zero for an Action, since it counts what a one-click Fix would change).
                //
                // Paths was the first attempt and it disagreed with the panel out loud: ASSET.DUP001 renders as (107)
                // there — 107 duplicate GROUPS — while its paths add up to 292, because each group holds every copy.
                // Two windows quoting different totals for one rule reads as a bug in whichever one you saw second,
                // and this row exists to send you to that panel. Caught by dumping the rendered tree against a real
                // project, not by a test: both numbers are correct, so nothing could have contradicted itself.
                int n = 0;
                if (d.Scan != null)
                    foreach (var f in d.Scan.Findings)
                        if (string.Equals(f.RuleId, s.Finding.RuleId, StringComparison.Ordinal)) n++;

                var row = new VisualElement
                { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap,
                            marginTop = 8, paddingTop = 8, paddingBottom = 8, paddingLeft = 12, paddingRight = 12 } };
                row.AddToClassList("pl-card");
                Round(row, 12);

                row.Add(FindingCardUI.Dot(s.Finding.Severity));
                row.Add(new Label(n > 1 ? L.Tr($"{s.Finding.GroupTitleOrTitle} — {n} places", $"{s.Finding.GroupTitleOrTitle} — {n} 处")
                                        : s.Finding.GroupTitleOrTitle)
                { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 13, color = Dim, whiteSpace = WhiteSpace.Normal } });

                // Same two tags the applicable rows carry: which number this moves, and whether a re-measurement could
                // ever see it. Most of these are build-size work, where the honest answer is that it cannot.
                row.Add(BlindTag(s.Axes) ?? MetaTag(AxisName(s.Axes.Count > 0 ? s.Axes[0] : PerfAxis.None), Dimmer));
                // Stated per row rather than only in the paragraph above: irreversible is a different decision from
                // "needs a click", and the reader is deciding right here whether to open the panel.
                if (FindingActions.IsIrreversible(s.Finding))
                    row.Add(MetaTag(L.Tr("can't be undone", "不可撤销"), Amber));
                row.Add(ThemedActions(FindingCardUI.Actions(s.Finding, ctx)));
                box.Add(row);
            }
            return box;
        }

        /// <summary>Below this a manual row is not worth the line it costs — a real one on the reference project was 35.3 KB.</summary>
        const long ManualWorthShowing = 1L * 1024 * 1024;

        /// <summary>At most this many manual rows. Anything cut is COUNTED in the footnote, never dropped silently.</summary>
        const int ManualMaxRows = 3;

        /// <summary>
        /// "No one-click for these": findings with no button at all, listed because of how much they are worth.
        ///
        /// The tier exists because leaving it out was measured, on the project this whole screen was rebuilt against:
        ///
        ///     4.5 GB  91.6%  ASSET.AARES001   report-only   &lt;- was invisible on every screen but the findings list
        ///   249.9 MB   4.9%  ASSET.DUP001     action
        ///   178.5 MB   3.5%  ASSET.AADUP001   action
        ///
        /// Surfacing the Action tier reached 8.4% of the recoverable build size. The rule holding the other 91.6% has
        /// no fix and no action — Resources duplication is repaired by moving assets out of a Resources folder, which
        /// cannot be automated safely (the copies keep their GUIDs, but `Resources.Load("path")` is a string in your
        /// code and moving the file breaks it) — so "PerfLint can't do it" had quietly become "you will never hear
        /// about it".
        ///
        /// The size is the whole argument here: there is no button, so a row with no number would be asking the reader
        /// to care on faith. Stated as an estimate, in the same breath and the same units as the main panel's header,
        /// and never as an exact reclaim — the duplication scanners deliberately refuse to claim precise wasted bytes.
        /// </summary>
        VisualElement BuildManualBlock(CurrentDiagnosis d, FindingCardUI.Context ctx)
        {
            // The ranked step carries ONE representative finding, whose own estimate is one asset's worth. What makes
            // this row worth reading is the rule's total, so it is summed the same way the main panel's header sums it.
            var rows = new List<(NextStep Step, long Bytes, bool Build)>();
            foreach (var s in d.Manual)
            {
                long build = 0, mem = 0;
                if (d.Scan != null)
                    foreach (var f in d.Scan.Findings)
                        if (string.Equals(f.RuleId, s.Finding.RuleId, StringComparison.Ordinal))
                        {
                            build += f.EstimatedBuildSavingsBytes;
                            mem += f.EstimatedMemorySavingsBytes;
                        }
                bool isBuild = build >= mem;
                long bytes = isBuild ? build : mem;
                if (bytes < ManualWorthShowing) continue;
                rows.Add((s, bytes, isBuild));
            }
            if (rows.Count == 0) return null;   // never an empty heading
            rows.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));

            int shown = Math.Min(rows.Count, ManualMaxRows);
            var box = new VisualElement();
            box.Add(Divider(14, 12));
            box.Add(new Label(L.Tr($"No one-click for these — {shown} item(s)", $"这些没有一键可用——{shown} 项"))
            { style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = Ink, marginBottom = 4,
                        whiteSpace = WhiteSpace.Normal } });
            box.Add(new Label(L.Tr("PerfLint has no button for these — the repair changes how the project is laid out, so it has to be yours. They are here because of the size: figures are estimates, and a build is what settles them.",
                                   "PerfLint 没有按钮可给——它们的修法要动工程结构，只能由你来做。列在这里是因为省量可观：数字是估算，最终以一次构建为准。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });

            for (int i = 0; i < shown; i++)
            {
                var r = rows[i];
                int n = 0;
                if (d.Scan != null)
                    foreach (var f in d.Scan.Findings)
                        if (string.Equals(f.RuleId, r.Step.Finding.RuleId, StringComparison.Ordinal)) n++;

                var row = new VisualElement
                { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap,
                            marginTop = 8, paddingTop = 8, paddingBottom = 8, paddingLeft = 12, paddingRight = 12 } };
                row.AddToClassList("pl-card");
                Round(row, 12);

                row.Add(FindingCardUI.Dot(r.Step.Finding.Severity));
                row.Add(new Label(n > 1 ? L.Tr($"{r.Step.Finding.GroupTitleOrTitle} — {n} places", $"{r.Step.Finding.GroupTitleOrTitle} — {n} 处")
                                        : r.Step.Finding.GroupTitleOrTitle)
                { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 13, color = Dim, whiteSpace = WhiteSpace.Normal } });

                // The number, then what it is. "about", never a flat figure: these are ceilings derived from asset
                // sizes and copy counts, not something a build has confirmed.
                row.Add(MetaTag(L.Tr($"about {PerfLint.Scanners.ScannerUtil.Human(r.Bytes)}",
                                     $"约 {PerfLint.Scanners.ScannerUtil.Human(r.Bytes)}"), Ink));
                row.Add(BlindTag(r.Step.Axes) ?? MetaTag(AxisName(r.Build ? PerfAxis.BuildSize : PerfAxis.Memory), Dimmer));
                row.Add(ThemedActions(FindingCardUI.Actions(r.Step.Finding, ctx)));
                box.Add(row);
            }

            // Say what was left out, both ways it can happen. A capped list that reads as the whole list is the same
            // failure this block was added to fix, one level down.
            int cutForSize = d.Manual.Count - rows.Count, cutForRoom = rows.Count - shown;
            if (cutForSize > 0 || cutForRoom > 0)
            {
                string note = cutForRoom > 0
                    ? L.Tr($"{cutForRoom} more like this, and {cutForSize} under 1 MB — all in the full panel.",
                           $"另有 {cutForRoom} 项同类、{cutForSize} 项不足 1 MB——都在完整面板里。")
                    : L.Tr($"{cutForSize} more are under 1 MB and not worth a line here — the full panel lists them.",
                           $"另有 {cutForSize} 项不足 1 MB，不值得单列——完整面板里有。");
                box.Add(new Label(note)
                { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginTop = 8 } });
            }
            return box;
        }

        /// <summary>
        /// The two whole-dimension plans — "optimize memory", "optimize build size" — offered where the round is.
        ///
        /// They are execution, and execution belongs on this screen rather than in the reference view; they are
        /// literally "which items are we doing this round", just chosen by dimension instead of one rule at a time.
        /// The plan, its dialog, the Pro gate and the executor stay in the main panel, which owns the scan they are
        /// built from and shows the result of running them.
        ///
        /// Each button appears only when its plan has something executable in it. A memory plan is scene-scoped, so
        /// it is built against the open scenes' dependency set — the same set the main panel uses, shared rather than
        /// recomputed, because two windows disagreeing about what is in scope would be worse than either answer.
        /// </summary>
        VisualElement BuildOptimizeRow(CurrentDiagnosis d)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center } };
            if (d?.Scan == null) return row;

            void Offer(SavingsDimension dim, bool sceneScoped, string label)
            {
                var plan = OptimizePlan.Build(d.Scan.Findings, dim,
                    sceneScoped ? PerfLintWindow.OpenSceneDependencies() : null);
                if (plan.IsEmpty) return;   // never a button that cannot do anything
                var b = Secondary(label, () => PerfLintWindow.OpenWindow().OpenOptimizeDialog(dim));
                b.style.marginTop = 8;
                row.Add(b);
            }

            Offer(SavingsDimension.Memory, true, L.Tr("Optimize memory (this scene)…", "一键优化内存（当前场景）…"));
            Offer(SavingsDimension.Build, false, L.Tr("Optimize build size…", "一键优化包体…"));
            return row;
        }

        /// <summary>
        /// "While you are here": the reversible fixes PerfLint can apply by itself, which the ranking put below the
        /// top slots.
        ///
        /// Kept visually and verbally secondary, because they are secondary — on the project this was built against,
        /// the three items above are what is costing frames and these are import settings. Merging them upward would
        /// have made the tick-boxes reachable at the price of the ranking meaning anything, which is the trade the
        /// ranking already refuses to make.
        /// </summary>
        VisualElement BuildApplicableBlock(CurrentDiagnosis d, FindingCardUI.Context ctx)
        {
            int rules = d.Applicable.Count, places = 0;
            foreach (var s in d.Applicable) places += d.FixablePlaces(s.Finding.RuleId);

            var box = new VisualElement();
            box.Add(Divider(14, 12));
            box.Add(new Label(L.Tr($"Also safe to apply now — {rules} fix(es), {places} place(s)",
                                   $"顺手可做——{rules} 项修复，共 {places} 处"))
            { style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = Ink, marginBottom = 4,
                        whiteSpace = WhiteSpace.Normal } });
            box.Add(new Label(L.Tr("Not what is limiting you — that is the list above. These are further down the ranking, reversible, and PerfLint can do them for you.",
                                   "这些不是当前的瓶颈——瓶颈在上面那份列表。它们排序更靠后、可撤销，而且 PerfLint 能替你做。"))
            { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });

            foreach (var s in d.Applicable)
            {
                string rid = s.Finding.RuleId;
                int n = d.FixablePlaces(rid);
                // Wraps, because this row carries the most content of any on the screen — title, the axis tag, and
                // up to two buttons — and it is the one that overflowed first once the tag grew a sentence. Below
                // the width where they all fit side by side the tag and the buttons drop to a second line, which
                // is the difference between a cramped row and a Fix button painted outside the window.
                var row = new VisualElement
                { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap,
                            marginTop = 8, paddingTop = 8, paddingBottom = 8, paddingLeft = 12, paddingRight = 12 } };
                // The theme's card surface, like every other block in the window. This row was the last one still
                // painted on a hand-picked grey LIGHTER than the window — the same plate that was removed from the
                // screen container and the footer, left behind because it is small.
                //
                // Deliberately NOT severity-tinted, unlike the round's own items: these are the ones the ranking put
                // below the top slots, and colouring them the same would undo the ranking's only visual statement.
                row.AddToClassList("pl-card");
                Round(row, 12);

                row.Add(FindingCardUI.Dot(s.Finding.Severity));
                row.Add(new Label(n > 1 ? L.Tr($"{s.Finding.GroupTitleOrTitle} — {n} places", $"{s.Finding.GroupTitleOrTitle} — {n} 处")
                                        : s.Finding.GroupTitleOrTitle)
                { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, fontSize = 13, color = Dim, whiteSpace = WhiteSpace.Normal } });

                // What it moves, so nobody reads an import setting as a frame-rate fix. The first axis is the one the
                // rule is mapped to primarily; these rules each have exactly one.
                row.Add(BlindTag(s.Axes) ?? MetaTag(AxisName(s.Axes.Count > 0 ? s.Axes[0] : PerfAxis.None), Dimmer));
                row.Add(ThemedActions(FindingCardUI.Actions(s.Finding, ctx)));
                box.Add(row);
            }
            return box;
        }

        /// <summary>
        /// The button that actually starts or closes a round.
        ///
        /// It used to be "Measure, then fix, then measure again", which navigated to another tab — a description of a
        /// procedure where an action belonged. What the round needs depends on where you are in it, and only one of
        /// those states involves applying anything:
        ///
        ///   nothing measurable -> measuring is not the next step whatever else is true, so it is asked FIRST. See
        ///                       below: this branch used to sit behind the baseline one and never ran for the round
        ///                       that needed it most.
        ///   no baseline      -> measure FIRST. Without a before, nothing done in this round can be verified at all,
        ///                       so this outranks even applying the fixes we could apply right now.
        ///   fixes to apply   -> apply them and re-measure in one go, which is the shortest gap between a change and
        ///                       its measurement — and the gap is what a verdict has to beat.
        ///   nothing to apply -> the common case, measured: a real project had 651 findings and ZERO reversible
        ///                       fixes (import-setting rules that COULD be automatic were report-only by design, and
        ///                       the eleven actionable ones all needed a decision). So "I have done these, measure
        ///                       now" is the main path, not the fallback.
        ///
        /// Whether a Play Mode sample can see this round's work at all (<see cref="RoundVisibility"/>) decides how
        /// the offer is worded and weighted rather than whether it appears: a round of build-size work measures back
        /// as "no measurable change" no matter how well it went, so the offer is demoted and says what it can
        /// actually answer — but never withdrawn, because "did I break something else" is still a real question.
        ///
        /// The ORDER of those two is the correction. Asking "is there a baseline" first meant a round holding
        /// nothing but build-size work — the exact round RoundVisibility exists for — was told "Measure this first ·
        /// about 2 min" in the primary button, for a measurement that could not speak for one item in it. The two
        /// tests are independent, so the one that can rule measuring out entirely has to be asked first.
        ///
        /// A missing baseline is not urgent in that round, for a reason that holds rather than as a concession:
        /// nothing in it moves a figure a sample can read, so a baseline pinned after the work describes the same
        /// scene as one pinned before it. What it does cost is the "did I break anything else" check on THIS round,
        /// which is what the note says — a trade, offered, not decided for them.
        /// </summary>
        VisualElement BuildRoundActions(CurrentDiagnosis d, BenchmarkVerifyState st)
        {
            var box = Themed(SoftPanel(Color.clear), "pl-note--accent");
            var acts = Row();

            bool hasBaseline = st != null && st.HasBaseline && st.BaselineDescribesSceneToMeasure();
            string blocked = MeasurementBlockedReason();
            var vis = RoundVisibility.Of(d?.Steps, d?.Applicable);

            // Every button below starts a run, so what those runs are about belongs here too — see the note in
            // BuildEvidenceGapNotice. Both screens ask the same question and the answer is one line.
            RenderScenePlan(box);

            if (vis.NothingVisible)
            {
                // Not the primary action here, and not called "measure and compare": there is nothing in this round
                // for it to compare. Kept on offer because "did I break anything" is still a real question, and the
                // label is the answer it can give.
                acts.Add(hasBaseline
                    ? Secondary(L.Tr($"Measure anyway — checks nothing broke · about {MinutesFor(CompareRuns)}", $"仍然测一次——只为确认没改坏 · 约 {MinutesFor(CompareRuns)}"),
                                () => StartMeasurement(CompareRuns, baseline: false))
                    // Without a baseline that check needs a before first, so the button is the baseline one — at the
                    // same demoted weight, because this round still cannot be verified by measuring it.
                    : Secondary(L.Tr($"Record a baseline · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"记录基线 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                                () => StartMeasurement(BaselineRuns, baseline: true)));
                box.Add(acts);
                box.Add(new Label(BlindRoundNote(vis))
                {
                    style = { fontSize = 12, color = Amber, whiteSpace = WhiteSpace.Normal, marginTop = 8 }
                });
                if (!hasBaseline)
                    box.Add(Foot(L.Tr("There is no baseline yet. Pinning one before or after this work gives the same numbers — nothing here moves them — so it only buys the \"did I break anything else\" check on this round. Otherwise it can wait for work a measurement can see.",
                                      "还没有基线。这一轮的活不会改变这些数字，所以改之前钉和改之后钉是一样的——现在钉只多买到「这一轮有没有改坏别的东西」这一个检查。否则等到有复测看得见的活时再钉即可。")));
            }
            else if (!hasBaseline)
            {
                acts.Add(Primary(L.Tr($"Measure this first · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"先测一次作为基线 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                                 () => StartMeasurement(BaselineRuns, baseline: true)));
                box.Add(acts);
                // "There is a baseline, for a different scene" is not the same as "there is no baseline", and reads as
                // a bug to anyone who remembers measuring. Say which one this is before they conclude we lost it.
                box.Add(Foot(st != null && st.HasBaseline
                    ? L.Tr("The baseline on record was taken in a different scene, so it cannot be compared against this one — measuring here replaces it.",
                           "已有的基线记录在另一个场景，无法与当前场景对比——在这里测量会替换它。")
                    : L.Tr("Without a \"before\", nothing you do this round can be verified — this is the step that makes the rest mean anything.",
                           "没有「之前」，这一轮做的任何事都无法验证——正是这一步让后面的结论有意义。")));
            }
            else
            {
                acts.Add(Primary(L.Tr($"I have done these — measure and compare · about {MinutesFor(CompareRuns)}", $"我改完了——测量并对比 · 约 {MinutesFor(CompareRuns)}"),
                                 () => StartMeasurement(CompareRuns, baseline: false)));
                box.Add(acts);
                box.Add(Foot(L.Tr("Measure as soon as you have made the changes: the longer the gap, the more the machine drifts on its own, and a real improvement has to beat that drift first.",
                                  "改完就测：间隔越久机器自己漂得越多，而真实的改善必须先超过那个漂移。")));

                // Named before the click, not discovered afterwards in the shape of a flat result.
                if (vis.PartlyBlind)
                    box.Add(new Label(BlindRoundNote(vis))
                    {
                        style = { fontSize = 12, color = Amber, whiteSpace = WhiteSpace.Normal, marginTop = 8 }
                    });
            }

            box.Add(BuildOptimizeRow(d));

            // Blocked means every button above drives Play Mode and cannot. Say so and stop them being clickable,
            // rather than letting the click land in a dialog that explains what the screen could have said first.
            if (blocked != null)
            {
                acts.SetEnabled(false);
                box.Add(Foot(blocked));
            }

            // No "Open the full panel" button here. The footer already carries one, under a line that says what it is
            // for ("Want every figure, or the whole findings list?") — rendering the round showed the two of them one
            // above the other, the same words twice, one of them unexplained.
            return box;
        }


        /// <summary>
        /// Names the figure that this round will not be able to split, or null when every item moves a different one.
        ///
        /// Not a restriction — the user may fix whatever they like. It is the same honesty the regression advice
        /// already carries ("undo them one at a time; with more than one applied, nothing here can say which did it"),
        /// moved to before the work instead of after it.
        /// </summary>
        static string CollidingAxisNote(IReadOnlyList<NextStep> steps)
        {
            var seen = new Dictionary<PerfAxis, int>();
            foreach (var s in steps)
                foreach (var a in s.Axes)
                {
                    if (a == PerfAxis.None) continue;
                    // A figure no sample can read cannot collide: this note ends "fix them one at a time if that
                    // matters to you", which promises that separating the work would let the comparison attribute
                    // it. For build size that is false however they are done — the comparison never sees it at all,
                    // and BlindRoundNote is what says so. Caught in a screenshot of a build-size-only round, where
                    // the two notes sat six lines apart contradicting each other.
                    if (!PerfAxisInfo.MeasurableInPlayMode(a)) continue;
                    seen[a] = (seen.TryGetValue(a, out int c) ? c : 0) + 1;
                }

            foreach (var kv in seen)
                if (kv.Value >= 2)
                    return L.Tr($"{kv.Value} of these move the same figure ({AxisName(kv.Key)}). If it changes, this comparison won't be able to say which of them did it — fix them one at a time if that matters to you.",
                                $"其中 {kv.Value} 项影响的是同一个数字（{AxisName(kv.Key)}）。它若有变化，这次对比分不出是哪一项造成的——在意的话就一项一项来。");
            return null;
        }

        /// <summary>
        /// The "a measurement cannot see this" mark for one item, or null when a Play Mode sample can report on it.
        ///
        /// Amber rather than grey on purpose: it is not extra detail about the item, it is the reason the button
        /// below will have nothing to say about it. The axis is still named — "build size" is what the work IS —
        /// so the tag replaces the plain axis tag rather than being added next to it.
        /// </summary>
        static VisualElement BlindTag(IReadOnlyList<PerfAxis> axes)
        {
            if (PerfAxisInfo.AnyMeasurableInPlayMode(axes)) return null;
            var primary = axes != null && axes.Count > 0 ? axes[0] : PerfAxis.None;
            return MetaTag(L.Tr($"{AxisName(primary)} — re-measuring can't see it", $"{AxisName(primary)}——复测看不到"), Amber);
        }

        /// <summary>
        /// What the re-measurement will and will not be able to answer about the work in this round.
        ///
        /// Deliberately does not tell the user to skip the work: build size is real work with a real payoff, and the
        /// screen offers one-click fixes for it. What it refuses to do is let a measurement be read as the verdict on
        /// it. Null when every item is visible, so the caller adds nothing in the ordinary case.
        /// </summary>
        static string BlindRoundNote(RoundVisibility v)
        {
            if (v.NothingVisible)
                return L.Tr($"Nothing in this round moves a figure a Play Mode sample can read — build size is decided by a build, not by a running scene. A comparison taken after this work can only come back \"no measurable change\", and that would not mean the work didn't land. Verify build size in a build; measure here only to confirm you broke nothing.",
                            $"这一轮里没有任何一项会改变 Play Mode 能读到的数字——包体由一次构建决定，不是由运行中的场景决定。做完这些再对比，只会得到「无可测出的变化」，而那并不代表改动没生效。包体请在构建里验证；在这里测量只是为了确认没有改坏别的东西。");

            if (v.PartlyBlind)
                return L.Tr($"{v.Blind} of these {v.Total} can't appear in the comparison whatever you do to them — build size is decided by a build, not by a running scene. The measurement speaks for the other {v.Visible}.",
                            $"其中 {v.Blind} 项（共 {v.Total} 项）无论怎么改都不会出现在这次对比里——包体由一次构建决定，不是由运行中的场景决定。这次测量只能为另外 {v.Visible} 项作证。");

            return null;
        }

        static string AxisName(PerfAxis a) => a switch
        {
            PerfAxis.CpuFrameTime => L.Tr("CPU frame time", "CPU 帧时间"),
            PerfAxis.GpuFrameTime => L.Tr("GPU frame time", "GPU 帧时间"),
            PerfAxis.Stutter => L.Tr("stutter", "卡顿"),
            PerfAxis.Memory => L.Tr("memory", "内存"),
            PerfAxis.BuildSize => L.Tr("build size", "包体"),
            _ => L.Tr("that figure", "该指标")
        };

        /// <summary>
        /// What acting on this finding actually means. Stated per item because "apply" is not uniform: most of what a
        /// real project surfaces has no one-click fix at all, and a screen that implies otherwise is a screen whose
        /// button does nothing.
        /// </summary>
        static (string, Color) ActionKind(Finding f, CurrentDiagnosis d)
        {
            // Only a reversible IFix earns "one-click" — asked through the diagnosis because a restored scan has lost
            // the delegate. Actions (delete-files, module-disable, chooser-required) are emphatically NOT one-click;
            // calling them that and wiring a Fix button crashed the editor on ASSET.DUP001.
            if (d.IsOneClickFixable(f)) return (L.Tr("one-click fix", "可一键修复"), Good);

            // An Action needs a decision and the full panel's flow. Irreversible ones (delete files) say so outright.
            if (FindingActions.NeedsDecision(f))
                return FindingActions.IsIrreversible(f)
                    ? (L.Tr("deletes files — review in the full panel", "会删文件 · 去完整面板处理"), Bad)
                    : (L.Tr("needs a decision — do it in the full panel", "需要确认 · 去完整面板处理"), Amber);

            // CodeFile is the AI-Fix opt-in, not the location — script scanners put the position in TargetPath as
            // "Assets/X.cs:42" and leave CodeFile empty for rules with no safe automatic rewrite. Reading only
            // CodeFile labelled a finding pointing at MouseLock.cs:20 exactly like one that knows nothing.
            var loc = FindingActions.LocationOf(f);
            // Names its destination, like the two above it do. This window deliberately has no AI Fix panel of its
            // own — the panel, the credit gate and the diff review all live in the full one, and the button beside
            // this tag ("Line-level analysis") already goes there. But the tag used to announce a capability without
            // saying where it is, next to a button whose label is about something else, so the two did not read as
            // one thing. Every other kind on this screen says where the work happens; this was the exception.
            if (f.AiFixable) return (L.Tr("your call — AI Fix, in the full panel", "需你决定 · AI Fix 在完整面板"), Accent);
            if (loc.HasLine) return (L.Tr($"your call — we know the line ({loc.Display})", $"需你决定 · 已定位到 {loc.Display}"), Accent);
            return (L.Tr("report only — decide and change it yourself", "报告类 · 需你自己判断并修改"), Dim);
        }

        /// <summary>
        /// What is happening, for the case where somebody can still see this window.
        ///
        /// Usually they cannot: entering Play Mode brings the Game view forward, and on the default layout this
        /// window is docked in the same tab group — so this screen is behind the game within a second of the button
        /// being pressed. That is why the same progress is painted across the top of the Game view, and why this one
        /// says so instead of pretending it is the only readout.
        /// </summary>
        void RenderMeasuring(VisualElement card)
        {
            var p = BenchmarkRunner.CurrentProgress;
            bool waiting = p.Phase == BenchmarkRunner.Phase.AwaitScene;

            card.Add(Title(waiting ? p.Headline : L.Tr("Measuring…", "正在测量……")));

            string where = p.Repetitions > 1
                ? L.Tr($"{p.Headline} · run {p.RunNumber} of {p.Repetitions}", $"{p.Headline} · 第 {p.RunNumber}/{p.Repetitions} 轮")
                : p.Headline;

            card.Add(Body(waiting
                ? L.Tr($"{where}. Keep playing — nothing is sampled until that scene is loaded, and it starts by itself once it is.",
                       $"{where}。继续玩就行——场景加载出来之前不会采样，加载出来就自动开始。")
                : L.Tr($"{where}. Stay in the Game view until it finishes — anything you do elsewhere in the editor lands in the numbers.",
                       $"{where}。结束前请勿离开游戏窗口——你在编辑器别处做的任何事都会落进这次的数字里。")));

            card.Add(Foot(L.Tr("The same progress, with the clock, is on the strip across the top of the Game view.",
                               "同样的进度和倒计时也显示在 Game 视图顶部的横条上。")));

            card.Add(Secondary(L.Tr("Cancel", "取消"), () => { BenchmarkRunner.Cancel(); ClearPending(); Render(); }));
        }

        void RenderPending(VisualElement card)
        {
            card.Add(Title(L.Tr("Waiting for your changes to finish importing", "正在等待改动导入完成")));
            card.Add(Body(L.Tr("Measuring now would time the editor putting itself back together rather than your game. It starts by itself the moment things go quiet.",
                               "现在测量测到的是编辑器在收尾，而不是你的游戏。一旦安静下来它会自己开始。")));
            card.Add(Secondary(L.Tr("Cancel", "取消"), () => { ClearPending(); Render(); }));
        }

        void RenderBlockedByEditorState(VisualElement card, string reason)
        {
            card.Add(Title(L.Tr("Can't measure right now", "现在无法测量")));
            card.Add(Body(reason));
        }

        void RenderNeedsBaseline(VisualElement card, BenchmarkVerifyState st)
        {
            // Names the scene rather than saying "this scene", because with a plan set it is not necessarily the one
            // on screen — that is the whole point of a plan, and a screen that says "this scene" while aiming at a
            // different one is the misdirection the plan was added to end.
            string scene = BenchmarkVerifyState.SceneToMeasureName();

            card.Add(Title(L.Tr("First, record how it runs today", "第一步：记录它现在跑得怎么样")));
            card.Add(Body(string.IsNullOrEmpty(scene)
                ? L.Tr("PerfLint measures this scene a few times and keeps the result. Later, after you change something, it measures again under identical conditions and tells you whether the change actually helped — or admits that it couldn't tell.",
                       "PerfLint 会把当前场景测几遍并留下结果。之后你改了东西，它会在同等条件下再测一次，告诉你这次改动到底有没有用——或者如实说它没能判断出来。")
                : L.Tr($"PerfLint measures {scene} a few times and keeps the result. Later, after you change something, it measures again under identical conditions and tells you whether the change actually helped — or admits that it couldn't tell.",
                       $"PerfLint 会把 {scene} 测几遍并留下结果。之后你改了东西，它会在同等条件下再测一次，告诉你这次改动到底有没有用——或者如实说它没能判断出来。")));

            var row = Row();
            row.Add(Primary(L.Tr($"Record a baseline · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"记录基线 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                            () => StartMeasurement(BaselineRuns, baseline: true)));
            card.Add(row);
            card.Add(Foot(L.Tr("Enters Play Mode, turns VSync off while measuring, and puts your settings back afterwards.",
                               "会进入 Play Mode，测量期间关闭 VSync，结束后还原你的设置。")));
        }

        /// <summary>
        /// The baseline on record is about a different scene than the one a measurement would be about now.
        ///
        /// Which "now" that is depends on the plan, and so does the way out. Under a plan, the editor's open scene is
        /// irrelevant and the mismatch is between two settings — so the offer is to re-aim the plan, not to open
        /// anything. Without one, the mismatch is simply which scene is open, and the way back is to open the other
        /// one — which this screen used to describe in a sentence instead of doing, leaving the reader to go and
        /// find a scene PerfLint already knew the path to.
        /// </summary>
        void RenderWrongScene(VisualElement card, BenchmarkVerifyState st)
        {
            var plan = BenchmarkScenePlan.Current;
            bool planned = !string.IsNullOrEmpty(BenchmarkVerifyState.PlannedSceneGuid());
            string aiming = BenchmarkVerifyState.SceneToMeasureName();
            string baselinePath = st.Baseline.ScenePath;
            bool canReopen = !string.IsNullOrEmpty(baselinePath) && System.IO.File.Exists(baselinePath);

            card.Add(Title(planned
                ? L.Tr("The plan aims somewhere else", "计划指向的是别的场景")
                : L.Tr("A different scene is open", "当前打开的是另一个场景")));
            card.Add(Body(planned
                ? L.Tr($"Your baseline was recorded in {st.Baseline.SceneName}, but measuring is set to {aiming}. A measurement only describes the scene it was taken in, so these two can't be compared.",
                       $"你的基线记录自 {st.Baseline.SceneName}，但现在设定要测的是 {aiming}。测量只描述它所在的那个场景，所以这两者无法对比。")
                : L.Tr($"Your baseline was recorded in {st.Baseline.SceneName}. A measurement only describes the scene it was taken in, so these two can't be compared.",
                       $"你的基线记录自 {st.Baseline.SceneName}。测量只描述它所在的那个场景，所以这两者无法对比。")));

            var row = Row();
            row.Add(Primary(planned
                    ? L.Tr($"Record a baseline for {aiming} · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"为 {aiming} 记录基线 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}")
                    : L.Tr($"Use this scene instead · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"改用当前场景 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                () => StartMeasurement(BaselineRuns, baseline: true)));

            // The other half of the choice, done rather than described. Gated on the scene still existing — offering
            // to open a deleted asset is the "sentence naming an action the reader cannot take" trap.
            if (planned)
            {
                string baselineGuid = st.SceneGuid;
                if (!string.IsNullOrEmpty(baselineGuid) && canReopen)
                    row.Add(Secondary(L.Tr($"Aim the plan back at {st.Baseline.SceneName}", $"把计划改回 {st.Baseline.SceneName}"),
                        () =>
                        {
                            // Re-aims whichever half of the plan decides what gets measured, so the two stay consistent.
                            if (plan.HasTarget) BenchmarkScenePlan.Save(plan.StartGuid, baselineGuid);
                            else BenchmarkScenePlan.Save(baselineGuid, "");
                            Render();
                        }));
            }
            else if (canReopen)
            {
                row.Add(Secondary(L.Tr($"Open {st.Baseline.SceneName}", $"打开 {st.Baseline.SceneName}"),
                    () =>
                    {
                        if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                            baselinePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                        Render();
                    }));
            }

            card.Add(row);
            if (!canReopen)
                card.Add(Foot(L.Tr($"{st.Baseline.SceneName} is no longer in the project, so there is nothing to go back to.",
                                   $"{st.Baseline.SceneName} 已不在工程里，没有可回退的对象。")));
        }

        void RenderNeedsAfter(VisualElement card, BenchmarkVerifyState st)
        {
            bool needsCalibration = !st.Drift.HasData;

            if (needsCalibration)
            {
                // Before anything can be proved, we need to know how much these numbers move when nobody touches them.
                // Asking for it as its own step is honest and it is cheap; skipping it is what made a real change
                // unprovable once already.
                card.Add(Title(L.Tr("One more measurement, with nothing changed", "再测一次，什么都别改")));
                card.Add(Body(L.Tr("Numbers on a computer move a little by themselves. Measuring twice without touching anything shows how much — and every later answer has to beat that before PerfLint will call it a real improvement.",
                                   "电脑上的数字自己就会有小幅波动。什么都不改地测两次，就能看出它有多大——之后任何一次改动都必须超过这个幅度，PerfLint 才会认定它是真的改善。")));
                var r = Row();
                r.Add(Primary(L.Tr($"Measure again · about {MinutesFor(CompareRuns)}", $"再测一次 · 约 {MinutesFor(CompareRuns)}"),
                              () => StartMeasurement(CompareRuns, baseline: false)));
                card.Add(r);
                card.Add(Foot(L.Tr("It takes as long as any other measurement, and it is the step that makes the rest mean anything.",
                                   "它和其他测量一样耗时，但正是它让之后的结论有意义。")));
                return;
            }

            card.Add(Title(L.Tr("Now change something", "现在去改点东西")));
            card.Add(Body(L.Tr($"Baseline recorded: {st.Baseline.SceneName}, {Fmt(st.Baseline.FrameMsMedian)} per frame. Fix something, then measure again straight away — the longer you wait, the more this machine drifts on its own, and a real improvement has to beat that drift first.",
                               $"基线已记录：{st.Baseline.SceneName}，每帧 {Fmt(st.Baseline.FrameMsMedian)}。去修一处，然后立刻复测——等得越久，这台机器自己漂得越多，而真实改善必须先超过这个漂移。")));

            // Said here, before the work, because it is knowable here. A baseline whose repetitions disagree about a
            // figure cannot ever prove anything in it, and the reader would otherwise find that out only after
            // doing the work and reading "too unsteady to judge" on the row it was about.
            string unstable = st.Baseline.StabilityWarning;
            if (unstable != null)
                card.Add(new Label(unstable)
                {
                    style = { fontSize = 12, color = Amber, whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
                });

            var row = Row();
            row.Add(Primary(L.Tr($"Measure and compare · about {MinutesFor(CompareRuns)}", $"测量并对比 · 约 {MinutesFor(CompareRuns)}"),
                            () => StartMeasurement(CompareRuns, baseline: false)));
            row.Add(Secondary(L.Tr("Pick something to fix", "去挑一处来修"), () => PerfLintWindow.OpenWindow()));
            card.Add(row);
        }

        /// <summary>Renders the verdict. Returns true when it has already said what to do about Deep Profile, so the generic mode note stays out of its way.</summary>
        bool RenderResult(VisualElement card, BenchmarkVerifyState st)
        {
            var report = st.BuildReport(PerfGoalPrefs.Current);

            if (!report.HasComparison)
            {
                card.Add(Title(L.Tr("These two measurements can't be compared", "这两次测量无法对比")));
                card.Add(Body(report.Blocker));

                // Naming the offending condition is a diagnosis, not a way out. This screen was giving the first and
                // offering to throw the baseline away, while the actual fix was one toggle it had already identified.
                bool hasRemedy = !string.IsNullOrEmpty(report.Advice);
                if (hasRemedy)
                    card.Add(new Label(report.Advice)
                    {
                        style = { fontSize = 13, color = Ink, whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
                    });

                var r = Row();

                // The remedy is a sentence with nothing to press, which is the trap this project keeps having to
                // re-close: "Turn it off (it needs a script reload) and measure again" printed above one button, and
                // that button replaces the baseline instead. The same action already exists on the first screen, so
                // it is the wording and the gate that matter — gated on Deep Profile being on RIGHT NOW, because the
                // mismatch can equally be a baseline that had it on while the current state does not, and then this
                // button would do nothing to the thing the sentence is about.
                bool deepMismatch = report.Blocker != null && report.Blocker.Contains("Deep Profile");
                if (deepMismatch && DeepProfileControl.Enabled)
                {
                    var off = Primary(L.Tr("Turn Deep Profile off", "关闭 Deep Profile"), () =>
                    {
                        if (DeepProfileControl.Set(false)) Render();
                    });
                    off.tooltip = L.Tr("Recompiles scripts (and leaves Play Mode if you are in it). Then measure again — the baseline is still good.",
                                       "会重新编译脚本（若在 Play Mode 中会退出）。之后重新测量即可——基线仍然有效。");
                    r.Add(off);
                }

                r.Add(hasRemedy
                    ? Secondary(L.Tr($"Or start over from now · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"或以当前状态重新开始 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                                () => StartMeasurement(BaselineRuns, baseline: true))
                    : Primary(L.Tr($"Start over from now · about {MinutesFor(BaselineRunsIncludingCalibration)}", $"以当前状态重新开始 · 约 {MinutesFor(BaselineRunsIncludingCalibration)}"),
                              () => StartMeasurement(BaselineRuns, baseline: true)));
                card.Add(r);
                if (!hasRemedy)
                    card.Add(Foot(L.Tr("Or put the earlier conditions back and measure again.", "或者把之前的条件改回去再测一次。")));
                // The remedy names Deep Profile when that is the mismatch, so the generic note would repeat it and
                // then contradict it.
                return hasRemedy && report.Blocker != null && report.Blocker.Contains("Deep Profile");
            }

            var tint = report.Result switch
            {
                BenchmarkComparison.Outcome.Proved => Good,
                BenchmarkComparison.Outcome.Worse => Bad,
                BenchmarkComparison.Outcome.Unproven => Amber,
                // A clean null comparison is the calibration working, not a failure — so it must not wear the warning
                // colour of "I couldn't prove your change" either. Neutral is the third answer: see NoteClassFor.
                BenchmarkComparison.Outcome.Calibrated => Dimmer,
                _ => Dimmer
            };

            // The verdict wears its outcome, in the same grammar as every other coloured block on these screens:
            // a dark fill of the hue with a brighter rule of the same hue. It used to be a pale wash with a 3 px
            // bar down its left edge — the bar was the only thing carrying the colour, and a bar is a marker,
            // not a state.
            var head = Themed(SoftPanel(Color.clear), NoteClassFor(report.Result));
            head.style.marginBottom = 12;
            head.style.paddingTop = 12;
            head.style.paddingBottom = 12;
            var verdictTitle = Title(report.Title);
            verdictTitle.style.fontSize = 20;
            head.Add(verdictTitle);
            head.Add(Body(report.Advice ?? "", 13, 0.9f));
            card.Add(head);

            // A disturbed sample, in amber and before everything else: it is the strongest reason on this screen to
            // disbelieve what follows, and unlike Deep Profile it is not a mode the user chose — they need to know it
            // happened at all. Same placement logic as the note below: it says what the figures ARE, not a footnote.
            if (report.SampleDisturbed)
                card.Add(Notice(report.SampleDisturbedNote));

            // The other end of the round screen's warning: the work this round recorded was work no sample can see,
            // so "no measurable change" below is a property of the question, not an answer about the work. Above the
            // figures for the same reason the two notes above it are — it says what the figures can be about.
            if (report.RoundWasInvisible)
                card.Add(Notice(report.BlindRoundNote));

            // Deep Profile: said before the figures, because it changes what the figures below ARE (there are no
            // timing rows at all in that mode) rather than adding a footnote to them.
            if (report.TimingsInflated)
                card.Add(new Label(report.TimingsInflatedNote)
                {
                    style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginBottom = 8 }
                });

            // The call path leads when there is one. This window exists to answer "what should I do and did it
            // work", and "the method you edited runs in 28% of frames instead of 92%" is a better answer than any
            // global counter — it is about the user's code rather than about this PC.
            //
            // Which is exactly why it is now gated on the path being under Assets/ (HotspotComparison.Row.IsUserCode,
            // read through Result.Actionable). An engine marker is none of that: "Inl_On Record Render Graph, 0.24 ->
            // 0.22 ms" sat directly under a 1.4 ms headline it accounted for 0.07 ms of, named something no reader
            // can open, and could not be acted on by anyone. The same list decides the outcome, so nothing is being
            // hidden from the verdict — it is out of both.
            var lead = report.LeadHotspot;
            if (lead != null) card.Add(BuildHotspotLead(lead));

            // The lead is scored as cost x (changed ? 4 : 1) on purpose, so a 0.05 ms path that moved does not
            // outrank a 1.5 ms one sitting still — that ordering is right and is not what this changes. What it
            // changes is that the headline names whichever path MOVED, and on GardenScene that was
            // CinemachineBrain.LateUpdate while the only row on screen was GfxDeviceD3D12.WaitForLastPresentation.
            // A sentence about a method the reader cannot see is the same defect as a verdict whose evidence is
            // off-screen; here the fix is one more row rather than a different ordering.
            if (report.HotspotResult?.Actionable != null)
                foreach (var h in report.HotspotResult.Actionable)
                    if (h.Moved && !ReferenceEquals(h, lead)) { card.Add(BuildHotspotLead(h, isLead: false)); break; }

            // The figures get a heading of their own, because by this point the screen has already said the verdict,
            // possibly two caveats and a call path — and without a label the rows underneath read as a continuation
            // of the sentence above them rather than as the evidence for it.
            if (report.Highlights != null && report.Highlights.Count > 0)
            {
                card.Add(Divider(12, 10));
                card.Add(SectionHead(L.Tr("Before and after", "前后对比")));
            }
            foreach (var row in report.Highlights) card.Add(BuildFigure(row));

            // Which of the three is the primary depends on what just happened.
            //
            // They used to be a fixed row — "Measure again" always primary — which asks the same question of four
            // different situations. After a proved improvement the next move is to lock it in as the baseline, or the
            // next round measures against a stale one; after a regression it is to go undo something; after an
            // unproven run another sample is genuinely the answer; and after a calibration the answer is not on this
            // screen at all, it is to go make a change.
            //
            // All three stay available — "I just want to measure again" is a legitimate wish in every state, and the
            // council's "delete the other two" would remove it. Only the emphasis moves.
            var actions = Row();
            actions.style.marginTop = 12;

            var measureAgain = L.Tr($"Measure again · about {MinutesFor(CompareRuns)}", $"再测一次 · 约 {MinutesFor(CompareRuns)}");
            var setBaseline = L.Tr("Make this the new baseline", "以当前状态为新基线");
            Action doMeasure = () => StartMeasurement(CompareRuns, baseline: false);
            Action doBaseline = () => StartMeasurement(BaselineRuns, baseline: true);

            switch (report.Result)
            {
                case BenchmarkComparison.Outcome.Proved:
                    // Deliberately NOT automatic. Doing it for them would mean PerfLint picking which reading becomes
                    // the bar, and the screen lets you re-measure as often as you like — auto-adopting the run that
                    // happened to look best is textbook baseline-shopping. Offered as the obvious next move instead.
                    actions.Add(Primary(setBaseline, doBaseline));
                    actions.Add(Secondary(measureAgain, doMeasure));
                    break;

                case BenchmarkComparison.Outcome.Worse:
                    // The advice already says "undo them one at a time and re-measure"; the emphasis follows it.
                    // No Undo button: this screen does not know what was changed, and Edit > Undo does not cover
                    // import settings anyway — the sentence is the instruction, the button just gets out of its way.
                    actions.Add(Primary(measureAgain, doMeasure));
                    actions.Add(Secondary(setBaseline, doBaseline));
                    break;

                case BenchmarkComparison.Outcome.Calibrated:
                    // Nothing was changed, so nothing on this screen can be the next step — the answer is on the round
                    // screen. Note this lands on the ROUND (what to change), not the conclusion: "Re-rank" below
                    // already covers going back to the ranking, and two buttons to two different screens is the useful
                    // pair. Measuring again is still offered, it is just not what to do.
                    actions.Add(Primary(L.Tr("Go make one change", "回去改一处"), () => { Tab = TabRound; Render(); }));
                    actions.Add(Secondary(measureAgain, doMeasure));
                    break;

                default:
                    actions.Add(Primary(measureAgain, doMeasure));
                    actions.Add(Secondary(setBaseline, doBaseline));
                    break;
            }
            // Closing the loop is the whole point of the three screens: a verdict is not an end state, it is the
            // input to the next round's ranking.
            // Reload before showing the ranking, not just switch tabs. GoTab deliberately reuses the cached models
            // (parsing JSON and re-sorting hundreds of findings on every tab click was worth avoiding), which is right
            // for a tab click and wrong for this button: its label promises a fresh judgement, and applying a fix or
            // rescanning a rule between here and there is exactly when it would matter. Measured cost is one
            // CurrentDiagnosis.Load, on a click that already means "I am done with this round".
            //
            // What this still does NOT do: skip a finding you have already looked at. Nothing records "seen", so an
            // unchanged project ranks the same item first again — correctly, but it will not feel like "the next one".
            // That needs a per-finding handled state; noted rather than faked here.
            // Named for what it does: reload the scan and the measurement, re-run the ranking, show the result.
            //
            // It was "Rank the next one", which promises to move past what you just looked at — and it cannot: nothing
            // records that you have seen a finding, so an unchanged project ranks the same item first again. That is
            // the correct answer (if the most valuable thing has not changed, naming a runner-up would be a lie) but
            // it is not what the old label promised, and the gap read as a broken button.
            //
            // Skipping is a real feature — per-finding handled state, its own storage, its own UI — and is not being
            // faked with a label. When it exists, this button gets its old name back.
            actions.Add(Secondary(L.Tr("Re-rank", "重新排序"), () => { Tab = TabConclusion; Render(); }));
            card.Add(actions);

            var provenance = Themed(SoftPanel(Color.clear), "pl-panel");
            provenance.style.marginTop = 12;
            provenance.Add(new Label(string.Join(" · ", report.GapLine, report.ChangesLine == null
                        ? L.Tr("nothing recorded as changed", "期间没有记录到改动")
                        : L.Tr($"changed: {report.ChangesLine}", $"期间改动：{report.ChangesLine}")))
            {
                style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal }
            });

            // This screen prints the gap but never the calibration line, so the rule that reads the two together had
            // no way of reaching the reader here: the memory rows were being shown, and quietly not judged, on the
            // one screen that says what the round proved. Found by walking the live window rather than by reading
            // the code — the logic was right and invisible.
            if (report.DriftSpanTooShort)
                provenance.Add(new Label(report.DriftSpanNote)
                {
                    style = { fontSize = 12, color = Amber, whiteSpace = WhiteSpace.Normal, marginTop = 4 }
                });
            card.Add(provenance);

            // A rendered verdict already carries its own Deep Profile note when the timings were inflated.
            return report.TimingsInflated;
        }

        /// <summary>
        /// The heaviest call path, before and after. One row, not a table — the rest is one click away in the full
        /// panel, which is the whole design rule of this window.
        /// </summary>
        VisualElement BuildHotspotLead(HotspotComparison.Row r, bool isLead = true)
        {
            // Colour follows what was MEASURED. A self-time verdict and a hit-rate change are both measurements — one
            // against the figure's own noise band, one against non-overlapping confidence intervals. Presence is
            // neither: the type's own remarks are emphatic that a marker missing from the "after" side has not been
            // shown to cost zero, it has dropped out of a top-N list, and those are different statements. The text
            // honoured that and the colour did not.
            //
            // It read as "the marker used to be one of the most expensive and no longer is", which is a fair reading
            // when the marker was expensive. Real case: Inl_RenderPipeline.BeginCameraRendering at 0.077 ms/frame —
            // the third CHEAPEST thing on a list topped by 0.214 — rendered as a green "off the list" and led the
            // screen as "busiest call path". Membership of a top-12 list, at that size, is noise wearing a verdict.
            bool good = r.Improved || r.Hit == HotspotComparison.HitChange.Fell;
            bool bad = r.Regressed || r.Hit == HotspotComparison.HitChange.Rose;

            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                          paddingLeft = 12, paddingRight = 12, paddingTop = 8, paddingBottom = 8,
                          marginBottom = 8 }
            };
            row.AddToClassList("pl-card");
            if (good) row.AddToClassList("pl-card--good");
            else if (bad) row.AddToClassList("pl-card--critical");
            Round(row, 12);

            // Overflow.Hidden is not decoration: a UIElements Label does not clip by default, and a marker is an
            // unbroken dotted identifier that no amount of word-wrapping can break. Without it
            // "GfxDeviceD3D12.WaitForLastPresentation" simply paints across the two columns to its right, and the
            // row becomes three overlapping sentences. The main panel's hotspot list already had this; this one is
            // a later copy that did not inherit it. The tooltip is what keeps clipping from losing the identity.
            var name = new VisualElement { style = { width = 224, flexShrink = 0, overflow = Overflow.Hidden } };
            name.Add(new Label(r.Marker) { tooltip = r.Marker, style = { fontSize = 13, whiteSpace = WhiteSpace.Normal } });
            // Only one row can be the busiest. A second row is drawn when a call path MOVED without being the lead —
            // reusing this builder for it captioned both "busiest call path", which was on screen the moment a round
            // produced two movers.
            name.Add(new Label(isLead
                    ? L.Tr("busiest call path", "最耗时的调用路径")
                    : L.Tr("also changed", "另一条也变了"))
            { style = { fontSize = 12, color = Dimmer } });
            row.Add(name);

            // minWidth = 0 because a flex item defaults to min-width:auto, which refuses to shrink below its own
            // content — so a long hit-rate line pushes instead of wrapping, and pushes into a column that cannot
            // shrink back (the verdict on the right is flexShrink = 0 by design, to stay readable).
            var mid = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
            // The hit rate leads because it is the figure that survives Deep Profile. But the millisecond pair is
            // shown UNDER it either way: hiding the number while still printing the word derived from it produced a
            // row reading "100% -> 100%" next to a green "cheaper", with nothing on screen to support the claim.
            mid.Add(new Label(r.HitText ?? r.PairText) { style = { fontSize = 13, whiteSpace = WhiteSpace.Normal } });
            if (r.HitText != null)
                mid.Add(new Label(r.TimingsInflated
                            ? L.Tr($"{r.PairText} · Deep Profile 口径，方向可信、幅度不可信",
                                   $"{r.PairText} · Deep Profile 口径，方向可信、幅度不可信")
                            : r.PairText)
                { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal } });
            row.Add(mid);

            row.Add(new Label(r.Hit == HotspotComparison.HitChange.Fell
                        ? L.Tr("runs in fewer frames", "出现在更少的帧里")
                        : r.Hit == HotspotComparison.HitChange.Rose ? L.Tr("runs in more frames", "出现在更多的帧里")
                        : r.Presence == HotspotComparison.Presence.DroppedOut ? L.Tr("off the list", "已掉出榜单")
                        : r.Presence == HotspotComparison.Presence.Appeared ? L.Tr("new to the list", "新进入榜单")
                        // Under Deep Profile the direction is real and the size is not, so the word says direction
                        // and the line above says how much to trust it — rather than a bare "cheaper".
                        : r.Improved ? (r.TimingsInflated ? L.Tr("self time down", "自耗时降了") : L.Tr("cheaper", "变便宜了"))
                        : r.Regressed ? (r.TimingsInflated ? L.Tr("self time up", "自耗时升了") : L.Tr("more expensive", "变贵了"))
                        : L.Tr("no measurable change", "无可测出的变化"))
            {
                style = { width = 164, flexShrink = 0, fontSize = 12, whiteSpace = WhiteSpace.Normal,
                          unityTextAlign = TextAnchor.MiddleRight,
                          color = good ? Good : bad ? Bad : Dimmer }
            });
            return row;
        }

        VisualElement BuildFigure(BenchmarkComparison.MetricRow r)
        {
            bool good = r.Improved && r.CountsAsResult;
            bool bad = r.Regressed && r.CountsAsResult;

            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                          paddingLeft = 12, paddingRight = 12, paddingTop = 8, paddingBottom = 8,
                          marginBottom = 8 }
            };
            // A figure that moved says so in its own surface. Only when the movement COUNTS — a figure the
            // comparison refuses to rule on stays neutral rather than being coloured by its raw direction.
            row.AddToClassList("pl-card");
            if (good) row.AddToClassList("pl-card--good");
            else if (bad) row.AddToClassList("pl-card--critical");
            Round(row, 12);

            var name = new VisualElement { style = { width = 168, flexShrink = 0 } };
            name.Add(new Label(r.ShortLabel) { style = { fontSize = 13, color = Ink } });
            name.Add(new Label(r.PairText) { style = { fontSize = 12, color = Dimmer } });
            row.Add(name);

            row.Add(new Label(string.IsNullOrEmpty(r.DeltaText) ? "—" : r.DeltaText)
            {
                style = { width = 74, flexShrink = 0, fontSize = 13, unityTextAlign = TextAnchor.MiddleRight,
                          unityFontStyleAndWeight = r.Moved ? FontStyle.Bold : FontStyle.Normal,
                          color = good ? Good : bad ? Bad : Dim, marginRight = 12 }
            });

            var verdict = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
            verdict.Add(new Label(r.VerdictText)
            {
                style = { fontSize = 12, color = r.Moved ? Ink : Dim, whiteSpace = WhiteSpace.Normal }
            });

            // The row's own caveat, which was being generated and then never drawn. NoteFor exists to say why a
            // big-looking delta is being refused, or what a figure includes besides the game — and its most important
            // case is exactly the one on screen here: a green "improved" on Managed heap, which is the whole editor's
            // heap and is collected on a schedule of its own. It returns null for ordinary rows, so this adds a line
            // only where there is something to warn about.
            if (!string.IsNullOrEmpty(r.Note))
                verdict.Add(new Label(r.Note)
                { style = { fontSize = 12, color = Dimmer, whiteSpace = WhiteSpace.Normal, marginTop = 0 } });

            row.Add(verdict);
            return row;
        }

        /// <summary>
        /// Where the detail went. The window is allowed to be this short only because none of it was deleted — every
        /// figure, the readings that set the bar, and the findings list are all in the main panel.
        /// </summary>
        VisualElement BuildFooter()
        {
            var foot = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    paddingLeft = 0,
                    paddingRight = 0,
                    paddingTop = 12,
                    paddingBottom = 4
                }
            };
            // A rule and a row, no fill. This was the last block in the window still painted on a grey plate
            // LIGHTER than the window itself — the same construction that was removed from the screen container,
            // left behind because it is small. It is also what the reference does with its own "Found an issue?"
            // footer: a divider, then the content, and nothing drawn around it.
            Border(foot, Color.clear, 0);
            foot.style.borderTopWidth = 1;
            foot.style.borderTopColor = Hair;
            foot.Add(new Label(L.Tr("Want every figure, or the whole findings list?", "想看全部指标或完整问题清单？"))
            {
                style = { fontSize = 12, color = Dimmer, flexGrow = 1, flexShrink = 1, minWidth = 120, whiteSpace = WhiteSpace.Normal }
            });
            foot.Add(Secondary(L.Tr("Open the full panel", "打开完整面板"), () => PerfLintWindow.OpenWindow()));
            return foot;
        }

        // ── driving a measurement ─────────────────────────────
        //
        // Deliberately the same SessionState keys the main panel uses, so a run started from either window is picked up
        // and finished by both. Two independent flags would let one window think a measurement is idle while the other
        // is mid-run.

        // The keys and the "what happens when it finishes" decision now live in BenchmarkIntent, once. They were
        // duplicated here as three consts, and this window recorded the intent without ever reading it back — so
        // "Make this the new baseline" measured three repetitions and threw them away.
        // Owned by the runner now, because the calibration that follows a baseline is started with no window in the
        // picture. Kept as local names so every call site reads the same as it did.
        const int BaselineRuns = BenchmarkRunner.BaselineRepetitions;
        const int CompareRuns = BenchmarkRunner.CompareRepetitions;
        const float WarmupSeconds = BenchmarkRunner.DefaultWarmupSeconds;
        const float SampleSeconds = BenchmarkRunner.DefaultSampleSeconds;

        /// <summary>
        /// Everything a "record a baseline" press actually costs: the baseline itself plus the calibration that now
        /// follows it without asking.
        ///
        /// One number on the button, because the user is committing to one uninterrupted sequence. Promising two
        /// minutes and then taking three and a half — even correctly, even with a good reason — is the button lying.
        /// </summary>
        const int BaselineRunsIncludingCalibration = BaselineRuns + CompareRuns;

        static bool HasPending => BenchmarkIntent.HasPendingSpec;

        static void ClearPending() => BenchmarkIntent.Clear();

        static string MinutesFor(int reps)
        {
            // Asked of the runner rather than recomputed, so the "about 70s" on a button and the countdown on the
            // Game view strip cannot drift apart. They were the same arithmetic in two files until the strip needed it.
            int seconds = Mathf.RoundToInt(
                (float)BenchmarkRunner.EstimatedSessionSeconds(reps, WarmupSeconds, SampleSeconds));
            return seconds < 90
                ? L.Tr($"{Mathf.Max(10, Mathf.RoundToInt(seconds / 10f) * 10)}s", $"{Mathf.Max(10, Mathf.RoundToInt(seconds / 10f) * 10)} 秒")
                : L.Tr($"{Math.Round(seconds / 30.0) / 2.0:0.#} min", $"{Math.Round(seconds / 30.0) / 2.0:0.#} 分钟");
        }

        static string Fmt(double ms) => double.IsNaN(ms) ? "—" : $"{ms:0.00} ms";

        /// <summary>Conditions that make any measurement meaningless, asked before a button is drawn rather than after it is clicked.</summary>
        static string MeasurementBlockedReason()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                return L.Tr("Leave Play Mode first — measuring drives Play Mode itself.",
                            "请先退出 Play Mode——测量本身会自己进出 Play Mode。");
            // Deep Profile is a trade, not a blocker: it costs the millisecond figures and buys per-method markers.
            // Stated by MeasurementModeNote where the button is; the timing half is dropped from the result rather
            // than reported wrong.
            return null;
        }

        /// <summary>What a measurement taken right now can and cannot answer. Delegated so the caption above a button and the runner behind it cannot disagree.</summary>
        static string MeasurementModeNote() => BenchmarkRunner.MeasurementModeNote();

        void StartMeasurement(int repetitions, bool baseline)
        {
            var spec = BenchmarkScenePlan.BuildSpec(WarmupSeconds, SampleSeconds, repetitions,
                saveRuntimeSession: true, out var problem);
            if (spec == null)
            {
                if (problem == BenchmarkScenePlan.LaunchProblem.PlanSceneMissing)
                {
                    EditorUtility.DisplayDialog(L.Tr("A scene in the plan is gone", "计划里的场景已不存在"),
                        L.Tr("One of the scenes set for measuring no longer exists. Pick it again before measuring — carrying on would file the run under a different scene than the one you meant.",
                             "设定用于测量的场景之一已不存在。请重新选择后再测量——否则这次测量会被记到另一个场景名下。"),
                        "OK");
                    _editingScenePlan = true;
                    Render();
                }
                else
                {
                    EditorUtility.DisplayDialog(L.Tr("Nothing to measure", "无可测量内容"),
                        L.Tr("Open and save a scene first — a measurement describes the scene it was taken in, so an unsaved one has nothing to compare against later.",
                             "请先打开并保存一个场景——测量只描述它所在的那个场景，未保存的场景之后无从对比。"),
                        "OK");
                }
                return;
            }

            string targetName = BenchmarkScenePlan.NameOf(spec.targetScenePath);
            string startName = !string.IsNullOrEmpty(spec.startScenePath)
                ? BenchmarkScenePlan.NameOf(spec.startScenePath)
                : UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            // Asked before the confirmation, and only when a different scene is about to replace this one. The
            // runner refuses the session outright in this state (see RefusalFor) precisely so that the question is
            // put here, where there is a user to answer it, rather than from inside the state machine.
            if (BenchmarkScenePlan.WouldDiscardUnsavedWork(spec)
                && !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            // The calibration is built from the SAME plan snapshot, now, rather than rebuilt when it starts: the
            // plan is editable while the baseline runs, and a calibration of a different scene is not a calibration.
            var calibration = baseline
                ? BenchmarkScenePlan.BuildSpec(WarmupSeconds, SampleSeconds, CompareRuns, saveRuntimeSession: true, out _)
                : null;

            string calibrationNote = calibration == null ? "" : L.Tr(
                $"\n\nThis runs {BaselineRuns} times to record the baseline and then {CompareRuns} more with nothing changed — that second part is how PerfLint learns how far these numbers move on their own, which every later result has to beat. It starts by itself; there is nothing to press in between.",
                $"\n\n它会先测 {BaselineRuns} 次建立基线，接着在什么都不改的情况下再测 {CompareRuns} 次——后半段是用来量出「这些数字自己会飘多少」，之后任何一次结论都必须先超过这个幅度。中间不用你点任何东西，会自动接着跑。");

            int announcedRuns = calibration != null ? BaselineRunsIncludingCalibration : repetitions;

            string what = spec.WaitsForScene
                // "Then stop moving" is the half that was missing, and leaving it out was actively producing
                // unusable baselines: sampling begins five seconds after the scene loads, so a camera still being
                // driven at that moment is driven through the window. Measured on this project — the same scene,
                // same run length, camera moving vs camera parked, differed by 30-40% in vertices and draw calls.
                ? L.Tr($"PerfLint will boot {startName}, wait until the game has loaded {targetName}, and measure there — {announcedRuns} time(s), about {MinutesFor(announcedRuns)} of measuring plus however long it takes to reach it.\n\nIf getting to {targetName} needs you to play — a menu, a level select — go ahead and play. Sampling starts by itself five seconds after it loads, so park where you want to measure and then keep still: moving the camera changes what is on screen, and the next comparison would read that as your change. The strip across the top of the Game view says which step you are in.",
                       $"PerfLint 会启动 {startName}，等游戏加载出 {targetName} 后在那里测量——共 {announcedRuns} 次，测量本身约 {MinutesFor(announcedRuns)}，另加你走到那里花的时间。\n\n{targetName} 要过菜单、选关才能进的话，照常玩就是了。加载出来 5 秒后会自动开始采样，所以走到你要测的位置就停下别动：镜头一动，画面里的东西就变了，下一次对比会把它读成你的改动。Game 视图顶部的横条会告诉你现在是哪一步。")
                : L.Tr($"PerfLint will enter and leave Play Mode {announcedRuns} time(s) in {startName}, about {MinutesFor(announcedRuns)} in total. Repeating it is what shows how much the numbers wobble by themselves, which is what a real improvement has to beat.\n\nDon't use the editor while it runs.",
                       $"PerfLint 会在 {startName} 里进出 Play Mode {announcedRuns} 次，总计约 {MinutesFor(announcedRuns)}。重复多次是为了看出数字自己波动多少——真实改善必须超过这个幅度。\n\n运行期间请不要操作编辑器。");

            if (!EditorUtility.DisplayDialog(
                    spec.WaitsForScene
                        ? L.Tr($"Measure {targetName}", $"测量 {targetName}")
                        : L.Tr("Measure this scene", "测量当前场景"),
                    what + calibrationNote + L.Tr("\n\nVSync is turned off while measuring and restored afterwards — a capped frame rate measures your monitor, not your game.",
                                "\n\n测量期间会关闭 VSync 并在结束后还原：帧率被钳制时测的是你的显示器，不是你的游戏。"),
                    L.Tr("Measure", "开始测量"), L.Tr("Cancel", "取消")))
                return;

            BenchmarkIntent.Arm(baseline ? BenchmarkIntent.Baseline : BenchmarkIntent.Compare);
            // Queued before the run starts, so it survives the two domain reloads the baseline costs. Consumed by
            // BenchmarkIntent's window-independent completion, never by this window: the Autopilot is behind the
            // Game view for the whole measurement and cannot be relied on to be alive when it lands.
            BenchmarkIntent.ClearChained();
            if (calibration != null) BenchmarkIntent.ChainCalibrationAfterBaseline(calibration);

            // Parked rather than refused when the editor is busy: the user pressed the button, and their intent should
            // survive a recompile.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                BenchmarkIntent.Park(spec);
                Render();
                return;
            }

            string refusal = BenchmarkRunner.Begin(spec);
            if (refusal != null)
            {
                ClearPending();
                EditorUtility.DisplayDialog(L.Tr("Can't measure right now", "现在无法测量"), refusal, "OK");
                return;
            }
            Render();
        }
    }
}
