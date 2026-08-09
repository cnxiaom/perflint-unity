using System;
using PerfLint.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// The one place PerfLint's five windows get their look from.
    ///
    /// All of it was worked out inside the Autopilot, against a reference editor window Tim picked as the bar, and
    /// for a while it lived there — a private palette and a stylesheet named after that one window. The other four
    /// panels kept their own hand-picked greys, which is how a product ends up with two of everything: two card
    /// fills, two button looks, two blues, and buttons that answer the mouse in one window and are dead to the touch
    /// in the other four.
    ///
    /// The split between the two halves is not tidiness, it is what each half can do:
    ///
    ///  · <b>The stylesheet</b> (<c>uss/PerfLint.uss</c>) owns surfaces, buttons and every <c>:hover</c>/<c>:active</c>.
    ///    An inline style OUTRANKS a USS rule in UIElements, so a Button that sets its own background in C# can never
    ///    show a hover — the pseudo-state resolves to a rule the inline value beats. Anything with a pointer state
    ///    must therefore be a class, and <see cref="Themed{T}"/> exists to clear the inline values out of the way.
    ///
    ///  · <b>This file</b> owns the tints a stylesheet cannot reach: the colour of a severity dot, of a verdict, of a
    ///    figure that went the right way. Those are chosen per finding at build time, so they have to be values.
    ///
    /// Both halves follow the editor skin. That is the part the other four windows were missing outright: a colour
    /// hand-picked against a dark 6.3 is correct on exactly that skin, and <c>rgba(255,255,255,0.03)</c> — the fill
    /// four of these panels used for every card — is invisible on a light one.
    /// </summary>
    public static class PerfLintStyle
    {
        public const string SheetPath = "Packages/com.perflint.unity/Editor/UI/uss/PerfLint.uss";

        // ── palette ───────────────────────────────────────────
        //
        // Sampled from the reference window rather than chosen: its page background is #383838 — Unity's own editor
        // window grey — and its item cards sit only 0.02 above it. Start at the editor's own grey and build UPWARDS;
        // every level below is lighter than the one it sits on, except Track, which is the recessed groove a
        // segmented control sits in.
        public static bool Pro => EditorGUIUtility.isProSkin;

        public static Color Ink => Pro ? new Color(0.945f, 0.949f, 0.960f) : new Color(0.10f, 0.11f, 0.13f);
        public static Color Dim => Pro ? new Color(0.780f, 0.792f, 0.816f) : new Color(0.29f, 0.31f, 0.35f);
        public static Color Dimmer => Pro ? new Color(0.635f, 0.647f, 0.678f) : new Color(0.42f, 0.44f, 0.48f);
        public static Color SurfaceRaised => Pro ? new Color(0.325f, 0.333f, 0.353f) : new Color(0.980f, 0.984f, 0.992f);
        public static Color SurfaceSoft => Pro ? new Color(0.365f, 0.373f, 0.396f) : Color.white;
        public static Color Track => Pro ? new Color(0.169f, 0.173f, 0.184f) : new Color(0.855f, 0.863f, 0.878f);
        public static Color Line => Pro ? new Color(1f, 1f, 1f, 0.11f) : new Color(0.09f, 0.12f, 0.18f, 0.13f);
        public static Color Hair => Pro ? new Color(1f, 1f, 1f, 0.15f) : new Color(0.09f, 0.12f, 0.18f, 0.16f);
        public static Color Good => Pro ? new Color(0.42f, 0.80f, 0.53f) : new Color(0.09f, 0.55f, 0.29f);
        public static Color Bad => Pro ? new Color(0.92f, 0.50f, 0.44f) : new Color(0.72f, 0.20f, 0.15f);
        public static Color Amber => Pro ? new Color(0.85f, 0.71f, 0.35f) : new Color(0.60f, 0.44f, 0.06f);
        public static Color Accent => Pro ? new Color(0.38f, 0.62f, 0.98f) : new Color(0.08f, 0.42f, 0.90f);

        public static Color Fade(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha);

        // ── geometry helpers ──────────────────────────────────

        public static void Round(VisualElement element, float radius)
        {
            if (element == null) return;
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        /// <summary>Set all four border-side colors at once — UIElements' inline style has no 'borderColor' shorthand.</summary>
        public static void SetBorderColor(VisualElement element, Color color)
        {
            if (element == null) return;
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        public static void Border(VisualElement element, Color color, float width = 1)
        {
            if (element == null) return;
            element.style.borderTopWidth = width;
            element.style.borderRightWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            SetBorderColor(element, color);
        }

        /// <summary>A hairline rule. Zones are separated by a line and a gap rather than by boxing each one.</summary>
        public static VisualElement Divider(float top = 10, float bottom = 10) => new VisualElement
        {
            style = { height = 1, flexShrink = 0, backgroundColor = Hair, marginTop = top, marginBottom = bottom }
        };

        // ── the stylesheet ────────────────────────────────────

        static StyleSheet _sheet;

        /// <summary>
        /// Gives a window the shared look: the theme's own window background, and every <c>pl-*</c> rule below it.
        ///
        /// Called from each window's CreateGUI. Guarded rather than assumed at every step — a missing sheet must
        /// degrade to whatever the inline styles say, never throw. The first-run Getting Started window opens while
        /// the package is still importing, which is exactly when the load can come back null.
        /// </summary>
        public static void Apply(VisualElement root)
        {
            if (root == null) return;
            if (!root.ClassListContains("pl-window")) root.AddToClassList("pl-window");

            var sheet = Sheet();
            if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
        }

        /// <summary>The sheet, cached. Re-resolved while it is still null so an import in flight is not fatal for the session.</summary>
        static StyleSheet Sheet()
        {
            if (_sheet != null) return _sheet;
            try { _sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(SheetPath); }
            catch { _sheet = null; }
            return _sheet;
        }

        // ── classes ───────────────────────────────────────────

        /// <summary>
        /// Hands a control's colours over to the stylesheet.
        ///
        /// Necessary, not tidy-up: an inline style beats a USS rule in UIElements, so a Button that sets its own
        /// background in C# can never show a <c>:hover</c> — the pseudo-state resolves to a rule the inline value
        /// outranks. Clearing them is what makes the class able to speak at all.
        /// </summary>
        public static T Themed<T>(T element, string className) where T : VisualElement
        {
            if (element == null) return null;
            element.style.backgroundColor = new StyleColor(StyleKeyword.Null);
            element.style.color = new StyleColor(StyleKeyword.Null);
            element.style.borderTopColor = new StyleColor(StyleKeyword.Null);
            element.style.borderRightColor = new StyleColor(StyleKeyword.Null);
            element.style.borderBottomColor = new StyleColor(StyleKeyword.Null);
            element.style.borderLeftColor = new StyleColor(StyleKeyword.Null);
            element.style.borderTopWidth = new StyleFloat(StyleKeyword.Null);
            element.style.borderRightWidth = new StyleFloat(StyleKeyword.Null);
            element.style.borderBottomWidth = new StyleFloat(StyleKeyword.Null);
            element.style.borderLeftWidth = new StyleFloat(StyleKeyword.Null);
            element.style.borderTopLeftRadius = new StyleLength(StyleKeyword.Null);
            element.style.borderTopRightRadius = new StyleLength(StyleKeyword.Null);
            element.style.borderBottomLeftRadius = new StyleLength(StyleKeyword.Null);
            element.style.borderBottomRightRadius = new StyleLength(StyleKeyword.Null);
            if (!string.IsNullOrEmpty(className) && !element.ClassListContains(className))
                element.AddToClassList(className);
            return element;
        }

        /// <summary>
        /// <see cref="Themed{T}"/> plus the inline height these panels set on every button.
        ///
        /// A fixed <c>height</c> is not overridden by the class's <c>min-height</c> — the two are different
        /// properties and both apply — so a 26 px inline height on a button that also wants to wrap a two-line
        /// label clips the second line off. Buttons are the one place the height has to go as well.
        /// </summary>
        static T ThemedControl<T>(T element, string className) where T : VisualElement
        {
            if (element == null) return null;
            element.style.height = new StyleLength(StyleKeyword.Null);
            element.style.minHeight = new StyleLength(StyleKeyword.Null);
            element.style.paddingTop = new StyleLength(StyleKeyword.Null);
            element.style.paddingRight = new StyleLength(StyleKeyword.Null);
            element.style.paddingBottom = new StyleLength(StyleKeyword.Null);
            element.style.paddingLeft = new StyleLength(StyleKeyword.Null);
            return Themed(element, className);
        }

        /// <summary>The one thing to do on a screen. Sized and coloured to be the obvious target rather than one button among six.</summary>
        public static Button AsPrimary(Button b)
        {
            if (b == null) return null;
            // Removed, not merely not-added: one button flips between the two (Start Sampling / Stop & Analyze), and
            // a leftover .pl-danger would keep painting whichever of the two rules the stylesheet lists last.
            b.RemoveFromClassList("pl-danger");
            ThemedControl(b, "pl-primary");
            b.style.fontSize = 13;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            return b;
        }

        /// <summary>The same box as <see cref="AsPrimary"/> in the colour of something running that you can stop.</summary>
        public static Button AsDanger(Button b)
        {
            if (b == null) return null;
            b.RemoveFromClassList("pl-primary");
            ThemedControl(b, "pl-danger");
            b.style.fontSize = 13;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            return b;
        }

        /// <summary>Everything else you can click, standing on its own line.</summary>
        public static Button AsSecondary(Button b)
        {
            if (b == null) return null;
            b.RemoveFromClassList("pl-danger");
            b.RemoveFromClassList("pl-primary");
            ThemedControl(b, "pl-secondary");
            b.style.fontSize = 12;
            return b;
        }

        /// <summary>The same button at toolbar density — nine of them on a row that does not wrap.</summary>
        public static Button AsToolbar(Button b)
        {
            if (b == null) return null;
            AsSecondary(b);
            if (!b.ClassListContains("pl-secondary--dense")) b.AddToClassList("pl-secondary--dense");
            return b;
        }

        /// <summary>A compact action, for the buttons that hang off a finding rather than off the screen.</summary>
        public static Button AsCompact(Button b)
        {
            if (b == null) return null;
            ThemedControl(b, "pl-compact");
            // Never bold HERE and normal there. The shared card UI used to mark only Fix bold, so a row with two
            // actions had one Normal and one Bold at the same size, on the same fill, with the same text colour —
            // and side by side the Normal one reads as disabled. Which action is the consequential one is said by
            // the word on it. The class carries the weight for all of them.
            b.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(StyleKeyword.Null);
            return b;
        }

        public static Button Primary(string text, Action onClick) => AsPrimary(new Button(onClick) { text = text });
        public static Button Secondary(string text, Action onClick) => AsSecondary(new Button(onClick) { text = text });
        public static Button Toolbar(string text, Action onClick) => AsToolbar(new Button(onClick) { text = text });

        /// <summary>Gives every button inside a finding's action row the compact look, in one call.</summary>
        public static VisualElement CompactActions(VisualElement actions)
        {
            if (actions == null) return null;
            foreach (var child in actions.Children())
                if (child is Button b) AsCompact(b);
            return actions;
        }

        /// <summary>Gives every button on a toolbar the dense look, in one call — including any a helper injected.</summary>
        public static VisualElement ToolbarButtons(VisualElement toolbar)
        {
            if (toolbar == null) return null;
            foreach (var child in toolbar.Children())
                if (child is Button b && !b.ClassListContains("pl-primary") && !b.ClassListContains("pl-danger"))
                    AsToolbar(b);
            return toolbar;
        }

        // ── blocks ────────────────────────────────────────────

        /// <summary>
        /// A plain card: the theme's own helpbox surface, rounded, with room to breathe.
        ///
        /// No border. A tinted block gets one (the rule is what stops a low-alpha wash reading as grey); a neutral
        /// one does not need it, and outlining every block is what made these panels read as a stack of boxes.
        /// </summary>
        public static VisualElement Card(float radius = 10)
        {
            var card = new VisualElement
            {
                style =
                {
                    marginBottom = 8,
                    paddingTop = 10, paddingBottom = 10, paddingLeft = 14, paddingRight = 14
                }
            };
            card.AddToClassList("pl-card");
            Round(card, radius);
            return card;
        }

        /// <summary>A block of supporting detail inside a card: darker than what it sits on, with a trace of blue.</summary>
        public static VisualElement Panel(float radius = 8)
        {
            var panel = new VisualElement
            {
                style =
                {
                    paddingTop = 8, paddingBottom = 8, paddingLeft = 12, paddingRight = 12
                }
            };
            panel.AddToClassList("pl-panel");
            Round(panel, radius);
            return panel;
        }

        /// <summary>
        /// A coloured callout: a dark fill of its hue plus a rule of the same hue one step brighter.
        ///
        /// Both halves matter. The fill alone, at the alpha a wash needs, is a smudge on grey — the border is what
        /// makes it read as a block with a colour rather than as grey with a hint of one.
        /// </summary>
        public static VisualElement Note(string noteClass)
        {
            var note = new VisualElement
            {
                style =
                {
                    marginBottom = 6,
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 10, paddingRight = 10
                }
            };
            if (!string.IsNullOrEmpty(noteClass)) note.AddToClassList(noteClass);
            return note;
        }

        public const string NoteGood = "pl-note--good";
        public const string NoteBad = "pl-note--bad";
        public const string NoteWarning = "pl-note--warning";
        public const string NoteAccent = "pl-note--accent";

        /// <summary>A keyboard shortcut, a mode, a count — something to read, never something to click.</summary>
        public static Label Chip(string text)
        {
            var chip = new Label(text) { style = { flexShrink = 0, marginLeft = 8 } };
            chip.AddToClassList("pl-chip");
            return chip;
        }

        // ── severity ──────────────────────────────────────────

        /// <summary>The class that washes a card in its own severity. Info is the quietest on purpose — most findings are Info.</summary>
        public static string SeverityClass(Severity severity) => severity switch
        {
            Severity.Critical => "pl-card--critical",
            Severity.Warning => "pl-card--warning",
            _ => "pl-card--info"
        };

        /// <summary>
        /// The severity dot's colour, and the accent anything else severity-shaped uses.
        ///
        /// Follows the skin, unlike the three fixed copies this replaces: the old Critical red (0.93, 0.30, 0.30)
        /// is fine on a dark editor and glares on a light one.
        /// </summary>
        public static Color SeverityColor(Severity severity) => severity switch
        {
            Severity.Critical => Pro ? new Color(0.90f, 0.35f, 0.33f) : new Color(0.74f, 0.18f, 0.15f),
            Severity.Warning => Pro ? new Color(0.93f, 0.71f, 0.20f) : new Color(0.62f, 0.45f, 0.05f),
            _ => Accent
        };
    }
}
