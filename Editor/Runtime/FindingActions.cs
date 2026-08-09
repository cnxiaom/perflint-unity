using System;
using System.Collections.Generic;
using PerfLint.Ci;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Licensing;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Acting on a finding, without owning a findings list.
    ///
    /// The Autopilot could show what to do and could not do any of it: every button led to "open the full panel".
    /// The capability was never missing — the main panel applies fixes through <see cref="PerfLintOps.ApplyFixList"/>,
    /// revives lost fixes through <see cref="ScanRunner.RescanRules"/>, and jumps to a line through
    /// <see cref="ScannerUtil"/>. What was missing was a way to reach any of it from a window that holds a ranking
    /// rather than a result. This is that way, and it is shared so the two windows cannot apply fixes differently.
    /// </summary>
    public static class FindingActions
    {
        /// <summary>
        /// The source location a finding points at, read from where the scanners actually put it.
        ///
        /// The two fields are NOT interchangeable, and mistaking them for each other is the trap here. Script
        /// scanners always encode the position into <see cref="Finding.TargetPath"/> as "Assets/X.cs:42";
        /// <see cref="Finding.CodeFile"/>/<see cref="Finding.CodeLine"/> are set only when the rule opts INTO AI Fix
        /// — <see cref="Finding.AiFixable"/> is defined as CodeFile being present. PERF.LOG001 leaves them empty on
        /// purpose (there is no safe automatic rewrite of a Debug.Log), and ScriptGcScanner sets them per issue via
        /// AllowAiFix. So CodeFile is an opt-in flag that happens to carry a path, not "the location".
        ///
        /// Which means the location was never lost — only unreadable to anything that asked CodeFile for it, which is
        /// how a finding pointing at MouseLock.cs:20 came to be labelled the same as one that knows nothing at all.
        /// </summary>
        public readonly struct Location
        {
            public readonly string Path;
            public readonly int Line;
            public bool HasPath => !string.IsNullOrEmpty(Path);
            public bool HasLine => HasPath && Line > 0;
            public bool IsScript => HasPath && Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            public Location(string path, int line) { Path = path; Line = line; }

            public string Display => HasLine ? $"{System.IO.Path.GetFileName(Path)}:{Line}"
                                   : HasPath ? System.IO.Path.GetFileName(Path) : null;
        }

        public static Location LocationOf(Finding f)
        {
            if (f == null) return default;
            if (!string.IsNullOrEmpty(f.CodeFile)) return new Location(f.CodeFile, f.CodeLine);

            string tp = f.TargetPath;
            if (string.IsNullOrEmpty(tp)) return default;

            return ScannerUtil.TryParsePathLine(tp, out string path, out int line)
                ? new Location(path, line)
                : new Location(tp, 0);
        }

        /// <summary>
        /// Goes to whatever the finding points at.
        ///
        /// The scanner's own Ping comes FIRST, and getting that order wrong is what made this land on line 1: a
        /// runtime finding's Ping is <c>OpenScript(path, "Update")</c>, which re-resolves the method declaration at
        /// click time, while reconstructing from TargetPath alone knew only the file and passed a null method. The
        /// author of a finding always knows at least as much as can be recovered from its fields — for a grouped
        /// asset finding the Ping selects the whole group, which no reconstruction can do at all.
        /// </summary>
        public static void Reveal(Finding f)
        {
            if (f?.Ping != null) { f.Ping(); return; }

            var loc = LocationOf(f);
            if (loc.IsScript && loc.HasLine) { ScannerUtil.OpenScriptAtLine(loc.Path, loc.Line); return; }
            if (loc.IsScript) { ScannerUtil.OpenScript(loc.Path, null); return; }
            if (loc.HasPath) ScannerUtil.PingAsset(loc.Path);
        }

        /// <summary>
        /// Every distinct asset a rule points at, in scan order.
        ///
        /// A rule that matched twenty-four textures is stored as twenty-four separate findings, not as one grouped
        /// finding — so the ranking, which keeps a single representative per rule, has no way to reach the other
        /// twenty-three. That is why Locate on a twenty-four place rule highlighted one arbitrary lightmap.
        /// </summary>
        public static List<string> PathsFor(string ruleId, ScanResult scan)
        {
            var paths = new List<string>();
            if (scan == null || string.IsNullOrEmpty(ruleId)) return paths;
            foreach (var f in scan.Findings)
            {
                if (!string.Equals(f.RuleId, ruleId, StringComparison.Ordinal)) continue;
                var loc = LocationOf(f);
                if (loc.HasPath && !paths.Contains(loc.Path)) paths.Add(loc.Path);
                // Group holds asset paths directly (all copies of a duplicate, say). Its own doc comment has always
                // described the action being built here — "the UI uses this to provide a Select all in Project
                // action, addressing the case where a single Locate is not sufficient" — which nothing implemented.
                if (f.Group == null) continue;
                foreach (var g in f.Group)
                    if (!string.IsNullOrEmpty(g) && !paths.Contains(g)) paths.Add(g);
            }
            return paths;
        }

        /// <summary>
        /// How many places applying this rule would actually change — the fixable subset of <see cref="PathsFor"/>.
        ///
        /// The two counts differ on purpose for rules that withhold their own fix from some matches. PERF.MSH002 is
        /// the live example: 254 models have compression off, and 239 get the one-click fix, because compression
        /// quantizes UV2 and the 15 that generate lightmap UVs would bake visible seams. Both numbers are true and
        /// they answer different questions, so a surface showing both has to say which is which.
        /// </summary>
        public static int FixablePlacesFor(string ruleId, ScanResult scan)
        {
            if (scan == null || string.IsNullOrEmpty(ruleId)) return 0;
            int n = 0;
            foreach (var f in scan.Findings)
                if (string.Equals(f.RuleId, ruleId, StringComparison.Ordinal) && IsFixable(f))
                    n += f.Group != null ? f.Group.Count : 1;
            return n;
        }

        /// <summary>
        /// Locate, for a rule that covers more than one place: opens the full panel on that rule.
        ///
        /// It used to select all the matched assets in the Project window, on the theory that the Inspector
        /// multi-edits a selection. Tim's verdict was that this is pointless, and he is right — now that each row has
        /// its own Fix button, scattering 254 assets into a selection neither fixes them nor takes you anywhere. What
        /// you actually want from "locate" on a rule you cannot see the members of is the LIST of them, which the
        /// main panel already renders, one row per instance, each with its own Locate, Fix, Explain and AI Fix.
        ///
        /// Arity was the first cut of that rule — one place, go to that place — with a caveat already noted here: "a
        /// single hit still routes through Reveal, it may be a script, which wants opening at its line". The caveat
        /// is the whole of it. What decides the destination is WHAT the one place is, not how many there are:
        ///
        ///   a script with a line   -> open it there. The line IS the answer.
        ///   a scene object         -> select it. It is in reach, and pointing at it beats listing it.
        ///   an asset file          -> the panel. The subject of these findings is a SETTING, and the file does not
        ///                             say which one. Locate on the single Read/Write model selected
        ///                             Boids_Mesh.fbx in the Project window — correct, and useless: the reader is
        ///                             left in front of twenty import settings with no hint that Read/Write is the
        ///                             one, because the round card shows the ranking's reasoning rather than the
        ///                             finding's own text. The panel row carries that text, and its own Locate, so
        ///                             the asset is one click further rather than lost.
        /// </summary>
        public static void RevealRule(string ruleId, ScanResult scan, Finding representative = null)
        {
            var loc = LocationOf(representative);

            // A line to put the cursor on.
            if (representative != null && loc.HasLine) { Reveal(representative); return; }

            // Something selectable that is not a file — a scene object reached through Ping. Nothing to look up in a
            // list; it is right there. Guarded on the Ping existing, or "no path" would send a finding with neither
            // to a Reveal that does nothing.
            if (representative != null && representative.Ping != null && !loc.HasPath) { Reveal(representative); return; }

            PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnRule(ruleId);
        }

        /// <summary>
        /// Whether this finding has a SAFE, recoverable, one-click fix — an <see cref="IFix"/>, eligible for Fix All.
        /// Counts the ones whose delegate was lost to disk (<see cref="Finding.WasAutoFixable"/>).
        ///
        /// Recoverable, NOT undoable: an IFix writes import settings and reimports, which Unity's undo stack does not
        /// record (see <see cref="PerfLint.Core.IFix"/>). This line used to promise Ctrl+Z, and being the definition
        /// of "fixable" it is where the same promise in eight user-facing strings came from — so it now says what is
        /// actually true. UndoPromiseTests keeps it that way, and cannot tell a quotation from a claim, which is why
        /// the old phrasing is described here rather than repeated.
        ///
        /// It used to also return true for <see cref="Finding.HasAction"/>, and that was the bug behind a crash: an
        /// Action is not a one-click fix. The FindingAction contract says so in its own remarks — "cannot be Unity
        /// Undone, should not be swept up in a one-click bulk fix, require explicit confirmation". Some Actions delete
        /// files (ASSET.DUP001, irreversible), some need a chooser (which duplicate to keep), some need a package
        /// re-resolve + domain-reload verifier (PKG001/2/3). Labelling all of those "one-click fix" and wiring a Fix
        /// button to them offered to run — inline, no chooser — the exact operations the main panel guards behind a
        /// dedicated window. Applying ASSET.DUP001 that way re-ran the duplicate scanner (which hashes every asset)
        /// and OOM-crashed the editor. Actions are <see cref="NeedsDecision"/>, and route to the full panel.
        /// </summary>
        public static bool IsFixable(Finding f) => f != null && (f.CanAutoFix || f.WasAutoFixable);

        /// <summary>
        /// Whether acting on this finding needs a confirmation and, for some, a dedicated flow — it is an Action, not
        /// a reversible one-click Fix. Never applied inline; the full panel owns the chooser / verifier / confirm.
        /// </summary>
        public static bool NeedsDecision(Finding f) => f != null && (f.HasAction || f.WasActionable) && !IsFixable(f);

        /// <summary>Deletes files and cannot be undone. A surface offering it must say so and must not run it inline.</summary>
        public static bool IsIrreversible(Finding f) => f != null && OptimizePlan.IsIrreversible(f.RuleId);

        /// <summary>
        /// Applies every one-click fix for a rule, reviving the fix first if this scan came back from disk.
        ///
        /// Returns what happened, in the user's words. The rescan is not an optimisation — without it there is no
        /// <see cref="Finding.Fix"/> to call at all, which is the difference between a button that works after a
        /// domain reload and one that silently does nothing.
        ///
        /// <paramref name="interactive"/> is what a BUTTON passes. Batch auto-fix is a Pro feature and the main panel
        /// has always gated it (<c>Entitlements.RequirePro("One-click fix")</c>); the Autopilot reached the same
        /// operation through here and gated nothing, which made every paid batch fix free from the other window. It
        /// also skipped the confirmation that names how many assets are about to be re-imported and says to commit
        /// first, and the progress bar that makes a 239-asset re-import something other than a frozen editor.
        ///
        /// The gate lives behind this flag rather than inside the method because a modal dialog cannot be shown from
        /// a batchmode test — the same reason PerfLintOps.ApplyFixList is documented as leaving the gate, the consent
        /// and the rescan to its caller.
        /// </summary>
        public static string ApplyRule(string ruleId, ScanResult current, out ScanResult updated, bool interactive = false)
        {
            updated = current;
            if (string.IsNullOrEmpty(ruleId) || current == null)
                return L.Tr("Nothing to apply.", "没有可应用的项。");

            // Refuse BEFORE the rescan. Reviving a fix means re-running its scanner, and some of those are ruinous to
            // run for this: ASSET.DUP001's DuplicateAssetScanner hashes every asset and OOM-crashed the editor. If no
            // finding under this rule is a genuine reversible Fix, there is nothing safe to apply inline — the full
            // panel owns Actions. This is defence in depth; the caller should not offer the button in the first place.
            bool anyFixable = false;
            foreach (var f in current.Findings)
                if (string.Equals(f.RuleId, ruleId, StringComparison.Ordinal) && IsFixable(f)) { anyFixable = true; break; }
            if (!anyFixable)
                return L.Tr("That one needs the full panel — it isn't a plain one-click fix.",
                            "这一项需要在完整面板里做——它不是普通的一键修复。");

            ScanResult live;
            try
            {
                live = ScanRunner.RescanRules(new[] { ruleId }, current) ?? current;
            }
            catch (Exception e)
            {
                return L.Tr($"Couldn't re-check that rule: {e.Message}", $"无法重新检查该规则：{e.Message}");
            }

            var fixable = new List<Finding>();
            foreach (var f in live.Findings)
                if (string.Equals(f.RuleId, ruleId, StringComparison.Ordinal) && f.Fix != null) fixable.Add(f);

            if (fixable.Count == 0)
            {
                updated = live;
                return L.Tr("Nothing left to fix for that rule — it may already be done.",
                            "该规则没有可修复项了——可能已经修过。");
            }

            // Asked AFTER the rescan, so the count in the dialog is what is really there rather than what the screen
            // last remembered — the two differ the moment anything else touched those assets.
            if (interactive)
            {
                updated = live;
                if (!Entitlements.RequirePro(L.Tr("One-click fix", "一键修复")))
                    return L.Tr("Batch auto-fix is a Pro feature.", "批量自动修复是 Pro 功能。");
                if (!ConfirmApply(ruleId, fixable.Count))
                    return L.Tr("Cancelled.", "已取消。");
            }

            var (applied, failed) = interactive
                ? PerfLintOps.ApplyFixList(fixable, (done, total) =>
                    EditorUtility.DisplayProgressBar(L.Tr("PerfLint — Batch Fix", "PerfLint — 批量修复"),
                                                    $"{done}/{total}", total > 0 ? (float)done / total : 0f))
                : PerfLintOps.ApplyFixList(fixable);
            if (interactive) EditorUtility.ClearProgressBar();

            // Re-check after applying, so what the caller redraws is the state on disk rather than the state it hoped for.
            try { updated = ScanRunner.RescanRules(new[] { ruleId }, live) ?? live; }
            catch { updated = live; }
            Persist(updated);

            if (failed > 0)
                return L.Tr($"Fixed {applied}, {failed} failed — see the Console.",
                            $"修复 {applied} 处，{failed} 处失败——详见 Console。");
            return L.Tr($"Fixed {applied}.", $"已修复 {applied} 处。");
        }

        /// <summary>
        /// The confirmation the main panel has always shown before a batch fix, reused verbatim.
        ///
        /// Verbatim on purpose. It is not merely "are you sure": it names how many assets are about to be re-imported,
        /// says the change is undoable, and tells you to commit first — and a second dialog with the same job and
        /// different words is how two surfaces start making different promises about the same operation.
        /// </summary>
        static bool ConfirmApply(string ruleId, int count)
        {
            // Read off a real click on a one-asset fix: "Will apply auto-fix to 1 items (PERF.MSH001):" over a
            // bullet repeating the same rule and count, and then "with this many the practical undo is version
            // control" — advice written for a batch, given about a single import setting, where changing it back
            // in the Inspector is genuinely the easy way. The last sentence of a confirmation is the one people
            // act on, so it says what is true for the number in front of them.
            bool one = count == 1;

            string what = one
                ? L.Tr($"Will apply the auto-fix for {ruleId} to 1 asset.\n\n", $"将对 1 个资源应用 {ruleId} 的自动修复。\n\n")
                // The per-rule bullet earns its place only when the header cannot carry the whole story; for one
                // rule it repeats the line above it.
                : L.Tr($"Will apply auto-fix to {count} assets ({ruleId}).\n\n", $"将对 {count} 个资源（{ruleId}）应用自动修复。\n\n");

            string undo = one
                ? L.Tr("This modifies an asset import setting and reimports it. Edit > Undo will NOT revert it — set it back in the Inspector, or restore from version control.",
                       "这会修改一项资源导入设置并触发重新导入。Edit > Undo 撤销不了——在 Inspector 里改回来，或从版本控制恢复。")
                : L.Tr("These changes modify asset import settings and trigger reimport. Edit > Undo will NOT revert them — each setting can be changed back in the Inspector, but with this many the practical undo is version control.\nCommit your project first.",
                       "这些改动会修改资源导入设置并触发重新导入。Edit > Undo 撤销不了——每一项都可以在 Inspector 里改回来，但数量一多，实际能依靠的是版本控制。\n请先提交你的工程。");

            return EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Batch Fix", "PerfLint — 批量修复"),
                what + undo,
                one ? L.Tr("Fix", "修复") : $"{L.Tr("Fix", "修复")} ({count})",
                L.Tr("Cancel", "取消"));
        }

        /// <summary>
        /// Writes the re-checked scan back to the store, so the screen that redraws from disk sees what just happened.
        ///
        /// RescanRules deliberately does not persist — it returns a result and lets the caller decide. The Autopilot
        /// redraws by re-loading CurrentDiagnosis, which reads the STORE, so without this the round kept listing the
        /// rules it had just applied until the background auto-rescan happened to catch up. The notification said
        /// "Fixed 27" while the list said there were still 27 to fix.
        /// </summary>
        static void Persist(ScanResult r)
        {
            if (r == null) return;
            try { ScanResultStore.Save(r); }
            catch (Exception e) { Debug.LogWarning("[PerfLint] Could not save the updated scan: " + e.Message); }
        }

        /// <summary>
        /// Where to go next for a finding that has no location — a measurement of the whole frame.
        ///
        /// The top of a measured round is runtime findings, and they have nothing to Locate: "median 2 FPS" and
        /// "10.6M triangles per frame" are properties of the frame, not of a file. So the three most important cards
        /// on the screen had no button at all, which for someone who does not already know Unity is a dead end
        /// dressed as a diagnosis.
        ///
        /// They are not directionless, though — each one's own detail text already names where to look ("Expand the
        /// CPU Hotspots below to find the most expensive methods, then optimize line by line with the script GC
        /// analysis in the main panel"). Prose pointing at a capability with no way to reach it is the cross-reference
        /// trap this project has hit before, so the prose becomes a button.
        ///
        /// Every destination is gated on actually existing and is described by what is really there — "Show the 12 CPU
        /// hotspots", not "Show CPU hotspots" — because a button promising a list that turns out to be empty is worse
        /// than no button. Returns null when nothing can be offered honestly.
        /// </summary>
        public readonly struct NextPlace
        {
            public readonly string Label;
            public readonly string Tooltip;
            public readonly Action Go;
            /// <summary>
            /// True when the destination IS the Runtime Profiler. That window offers these routes too, and a button
            /// there that opens the window you are already looking at is a loop, so it skips them.
            /// </summary>
            public readonly bool OpensRuntimePanel;
            /// <summary>
            /// True when the destination is the full panel focused on THIS rule — the same place Locate lands for an
            /// asset finding, and the same place the decision tier's "Do this in full panel" goes. A row offering two
            /// of those is offering one button twice under two names, which is what it looked like to Tim.
            /// </summary>
            public readonly bool OpensPanelOnThisRule;
            public NextPlace(string label, string tooltip, Action go, bool opensRuntimePanel = false, bool opensPanelOnThisRule = false)
            { Label = label; Tooltip = tooltip; Go = go; OpensRuntimePanel = opensRuntimePanel; OpensPanelOnThisRule = opensPanelOnThisRule; }
            public bool Exists => Go != null;
        }

        public static NextPlace WhereToLook(Finding f, CurrentDiagnosis d)
        {
            if (f == null || d == null) return default;

            bool aboutAllocation = f.RuleId != null &&
                (f.RuleId.StartsWith("RUN.GC", StringComparison.Ordinal) ||
                 f.RuleId.StartsWith("RUN.FPS001", StringComparison.Ordinal) ||
                 f.RuleId.StartsWith("RUN.FPS003", StringComparison.Ordinal));

            // Every finding whose own text asks for Deep Profile, found by reading all of them rather than one
            // complaint at a time: RUN.FPS002's unattributed variant ("to pinpoint the method, enable Deep Profile
            // and re-sample") and RUN.HOT003 ("Enable Deep Profile and re-sample"). HOT003 is why this sits ABOVE the
            // hotspot hand-off — its advice is the toggle, not Unity's Profiler.
            //
            // RUN.GC* used to be first in this list and is deliberately absent now. Allocation findings are attributed
            // from GC.Alloc callstacks, which every sample records, so the toggle is no longer their remedy — offering
            // it would cost a recompile and a re-sample to arrive at the same answer this sample already had.
            bool asksForDeepProfile = f.RuleId != null &&
                (f.RuleId.StartsWith("RUN.FPS002", StringComparison.Ordinal) ||
                 f.RuleId.StartsWith("RUN.HOT003", StringComparison.Ordinal));


            // A finding that named something specific points AT it, not at the panel that lists it. The triangle
            // rule knows the heaviest meshes in the scene and can select them — now that those targets survive
            // being written to disk — so sending someone to "the runtime results" instead would be handing them the
            // container when the contents were already in reach. Heaviest first, because that is the order the
            // analyser ranks them in.
            if (f.LocateTargets != null && f.LocateTargets.Count > 0)
            {
                var top = f.LocateTargets[0];
                if (top.Ping != null)
                {
                    int rest = f.LocateTargets.Count - 1;
                    return new NextPlace(
                        L.Tr($"Locate {top.Label}", $"定位 {top.Label}"),
                        rest > 0
                            ? L.Tr($"Selects it in the open scene(s). The other {rest} it named are in the Runtime Profiler, one button each.",
                                   $"在已打开的场景中选中它。它点名的另外 {rest} 个在运行时分析器里，每个一个按钮。")
                            : L.Tr("Selects it in the open scene(s).", "在已打开的场景中选中它。"),
                        top.Ping);
                }
            }

            // A hotspot PerfLint could not map to a project script hands over to Unity's own Profiler, because that
            // is genuinely where the next step is and the finding's own text already says so — "to dig deeper, expand
            // the call stack for this marker in the Unity Profiler".
            //
            // Sending it to our Runtime Profiler instead would show the same card again: it is the panel that
            // produced the sentence, so it has nothing to add to it. Only the unmappable ones arrive here at all —
            // a hotspot with a script gets a Ping, and a finding with a Ping never asks this question.
            //
            // This used to carry a second clause — step aside when the finding is about allocation and the static
            // scan named allocating scripts — which is dead now that RUN.GC* no longer asks for the toggle at all.
            // It is removed rather than left to rot, and the reason is worth recording: that clause routed the
            // allocation findings to PERF.GC/PERF.UPD rules on the reasoning that named code beats a re-sample. On
            // urp3dsample it sent Tim to fix two static rules that had nothing to do with the measured allocation,
            // and the verified result was 55.6 -> 55.9 KB/frame. A static scan knows an allocation pattern EXISTS;
            // it cannot know whether that code ran, or how hot it was. Runtime attribution answers that, and now
            // succeeds without a toggle, so the trade the clause encoded no longer needs making.
            if (asksForDeepProfile && !DeepProfileControl.Enabled)
                return DeepProfileOffer();

            // The Unity Memory Profiler is a separate package, so this is offered only when it is actually installed
            // — RUN.MEM003 says "use the Unity Memory Profiler to confirm the exact type", and naming a window the
            // reader does not have would be worse than saying nothing.
            if (f.RuleId != null && f.RuleId.StartsWith("RUN.MEM003", StringComparison.Ordinal) && MemoryProfilerInstalled)
                return new NextPlace(
                    L.Tr("Open the Memory Profiler", "打开 Memory Profiler"),
                    L.Tr("Take a snapshot there to confirm which type is holding the memory — this measurement can say how much, not what.",
                         "在那里抓一次快照即可确认是哪个类型占住了内存——这次测量只能说多少，说不出是什么。"),
                    () => { if (!EditorApplication.ExecuteMenuItem("Window/Analysis/Memory Profiler"))
                                Debug.LogWarning("[PerfLint] " + L.Tr("Open it via Window > Analysis > Memory Profiler.", "请通过 Window > Analysis > Memory Profiler 打开。")); });

            // The streaming finding ends by naming a place to go — "tune Memory Budget / Max Level Reduction in the
            // Runtime Profiler's Texture Streaming section" — and had no button for it, which is the sentence-with-no-
            // button pattern this method exists to close. It matters more here than in most: the finding also reports
            // that on a scene whose streamable pool sits under the budget, the realistic saving is 0 B until the budget
            // comes down. Enabling the import flags without going there can genuinely change nothing, which is exactly
            // what Tim asked about.
            if (f.RuleId != null && f.RuleId.StartsWith("PERF.TEXSTR", StringComparison.Ordinal))
                return new NextPlace(
                    L.Tr("Tune the streaming budget", "去调串流预算"),
                    L.Tr("Opens the Runtime Profiler's Texture Streaming section, expanded — where Memory Budget and Max Level Reduction are. Streaming only evicts mips once demand exceeds the budget, so on a scene under it nothing changes until you lower it.",
                         "打开运行时分析器的 Texture Streaming 区并展开——Memory Budget 与 Max Level Reduction 都在那里。串流只有在需求超过预算时才会淘汰 Mip，所以场景低于预算时，不调低它就不会有任何变化。"),
                    PerfLint.UI.TextureStreamingSection.Reveal, opensRuntimePanel: true);

            if (f.RuleId != null && f.RuleId.StartsWith("RUN.HOT", StringComparison.Ordinal))
                return new NextPlace(
                    L.Tr("Open the Unity Profiler", "打开 Unity Profiler"),
                    L.Tr("This marker is engine or third-party code, so PerfLint has taken it as far as it can. Unity's Profiler can expand its call stack — record a frame there to see what leads into it.",
                         "这个 marker 属于引擎或第三方代码，PerfLint 已经查到头了。Unity 自带的 Profiler 能展开它的调用栈——在那里录一帧即可看到是什么调用了它。"),
                    OpenUnityProfiler);

            // "2508 draw calls per frame. Consider static/dynamic batching, GPU Instancing, sprite atlasing..." —
            // the finding names the remedies and the scan already holds findings for them, so the card can point at
            // those instead of at the panel that printed the sentence. Gated on them existing, like every other route.
            bool aboutBatching = f.RuleId != null &&
                (f.RuleId.StartsWith("RUN.DRAW", StringComparison.Ordinal) ||
                 f.RuleId.StartsWith("RUN.SETPASS", StringComparison.Ordinal));
            int batching = CountRules(d.Scan, "MAT", "PERF.MAT", "PERF.INST", "PERF.SBATCH");
            if (aboutBatching && batching > 0)
                return new NextPlace(
                    L.Tr($"Show the {batching} batching findings", $"查看 {batching} 条合批相关结论"),
                    L.Tr("Opens the full panel on the instancing and batching rules — the ones that reduce this count. The measurement says how many draw calls; these say which settings are producing them.",
                         "在完整面板中打开 Instancing 与合批相关规则——正是能降低这个数字的那些。测量告诉你有多少 draw call，它们告诉你是哪些设置造成的。"),
                    () => PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnRuleFamily(
                        L.Tr("Batching / GPU Instancing", "合批 / GPU Instancing"),
                        "MAT", "PERF.MAT", "PERF.INST", "PERF.SBATCH"));

            int hotspots = d.RuntimeApplies && d.Runtime?.Hotspots != null ? d.Runtime.Hotspots.Count : 0;
            int allocScripts = CountRules(d.Scan, "PERF.GC", "PERF.UPD");

            // Allocation first for the GC findings: the allocating scripts are the culprit list, and the hotspot list
            // is a rung more abstract.
            // "enable Deep Profile to pinpoint the source" is the GC finding own title when it could not attribute
            // the allocation, and the analyser calls it "the one action that unlocks function-level GC attribution".
            // It was a sentence with no button — the fourth time that pattern has turned up on this screen.
            //
            // Ranked BELOW the allocating scripts on purpose: when the static scan already names code that allocates,
            // that is actionable now, while this costs a recompile and another sample. Offered only when Deep Profile
            // is actually off, read from the profiler rather than from the wording of a title.
            if (aboutAllocation && allocScripts > 0)
                return new NextPlace(
                    L.Tr(allocScripts == 1 ? "Show the allocating script" : $"Show the {allocScripts} allocating scripts",
                         $"查看 {allocScripts} 处分配点"),
                    L.Tr("Opens the full panel filtered to the allocation rules in your scripts — the code this measurement is the consequence of.",
                         "在完整面板中筛出脚本里的内存分配规则——这次测量正是它们造成的结果。"),
                    () => PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnScriptGcRules());

            // Hotspots are CPU call paths, so only a CPU-axis finding may be sent to them. Caught live: the triangle
            // -count rule went to "Show the 12 CPU hotspots", which answers a GPU geometry question with a list of
            // main-thread methods — a confident hand-off to the wrong place, which is worse than none. The axis map
            // already knows which is which, so it decides rather than a per-rule guess.
            bool cpuBound = false;
            foreach (var a in NextSteps.AxesOf(f))
                if (a == PerfAxis.CpuFrameTime || a == PerfAxis.Stutter) cpuBound = true;

            // A runtime finding belongs in the runtime panel. That was not true until the panel learned to restore its
            // own session from disk — it saved one and never loaded it, so it was empty after Play Mode and sending
            // anyone there would have been the empty-destination mistake in a new place.
            //
            // The label names the findings rather than the hotspots. This window has no per-hotspot list: the merge
            // folds them into RUN.HOT* findings, so "Show the 12 CPU hotspots" promised a list that does not exist
            // as one — the honest count is what the destination will actually render.
            // The frame-rate and stutter findings say, in their own text, to expand the CPU hotspots and find the
            // main cost centres. When the same session already mapped one to a SCRIPT, that is the most actionable
            // thing on the screen — a line of the reader's own code — and it was sitting there unlinked while all
            // three cards offered the same "Read the details". Tim's words: a beginner is stuck here.
            //
            // Deliberately not offered for the allocation finding. RUN.GC001 says the allocation is spread across
            // many sites with no single method dominating, so pointing at the top CPU hotspot would answer "where is
            // my GC" with "here is the busiest method" — a different question, confidently.
            bool aboutFrameTime = f.RuleId != null && f.RuleId.StartsWith("RUN.FPS", StringComparison.Ordinal);
            if (aboutFrameTime && d.RuntimeApplies && d.Runtime?.Findings != null)
            {
                foreach (var hot in d.Runtime.Findings)
                {
                    if (hot == null || !string.Equals(hot.RuleId, "RUN.HOT001", StringComparison.Ordinal)) continue;
                    var where = LocationOf(hot);
                    if (hot.Ping == null && !where.HasPath) continue;
                    var captured = hot;
                    return new NextPlace(
                        // Name the file even without a line. RUN.HOT001 carries the script path but no line, and
                        // "Open the hotspot in your code" told the reader nothing they could recognise — Display
                        // already falls back from "Foo.cs:42" to "Foo.cs" for exactly this case.
                        where.HasPath ? L.Tr($"Open {where.Display}", $"打开 {where.Display}")
                                      : L.Tr("Open the hotspot in your code", "打开代码里的热点"),
                        L.Tr($"The costliest call path this measurement mapped to your own code: {captured.Title}",
                             $"这次测量中能映射到你自己代码的最耗时调用路径：{captured.Title}"),
                        () => Reveal(captured));
                }
            }

            // An "Open the Unity Profiler / record a GC.Alloc sample" route used to sit here, mirroring a sentence
            // GC001 used to end with. Both are gone together, which is the point: the button existed only because the
            // finding's own text asked for it, and that text asked for it only because we could not record allocation
            // callstacks ourselves. We can now — every sample does — so sending someone to record by hand would ask
            // them to redo work this measurement already did. A cross-reference outliving the thing it points at is
            // exactly the dangling-reference trap, so it is deleted at the same time as its sentence, not later.

            // Nothing more specific exists for this one, and the label should say what you actually get: the
            // finding's own explanation, in the panel that holds it. "Open the runtime results (5)" described the
            // CONTAINER, which made two findings with different answers read as the same card — Tim asked why twice.
            //
            // This is a real answer, not a shrug: for the draw-call and allocation findings the remaining content IS
            // the advice in their detail text, which the card has no room for. And it goes to the Runtime Profiler,
            // never the scan panel — that panel is static-only, so focusing it on a RUN.* rule lands on zero results.
            if (f.Domain == Domain.Runtime)
                return new NextPlace(
                    L.Tr("Read the details", "查看详情"),
                    hotspots > 0 && cpuBound
                        ? L.Tr($"Opens this measurement in the Runtime Profiler, where the finding's full text is — including what it recorded across {hotspots} call paths.",
                               $"在运行时分析器中打开这次测量，那里有这条结论的完整正文——包括它在 {hotspots} 条调用路径上记录到的内容。")
                        : L.Tr("Opens this measurement in the Runtime Profiler, where the finding's full text is — including the remedies it lists.",
                               "在运行时分析器中打开这次测量，那里有这条结论的完整正文——包括它列出的应对办法。"),
                    PerfLint.UI.PerfLintRuntimeWindow.Open, opensRuntimePanel: true);

            // A static finding with no location: its own detail is the most useful thing in reach.
            string rid = f.RuleId;
            return new NextPlace(
                L.Tr("Read the details", "查看详情"),
                L.Tr("Opens the full panel on this finding, where its complete text is — including any culprits it named.",
                     "在完整面板中打开这条结论，那里有它的完整正文——包括它点名的元凶。"),
                () => PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnRule(rid),
                opensPanelOnThisRule: true);
        }

        /// <summary>
        /// Opens Unity's own Profiler window, saying so in the console if the menu path is not there.
        /// </summary>
        static void OpenUnityProfiler()
        {
            // Menu paths are not API and have moved before; a silent no-op would look like a broken button, so a
            // failure says which window to open by hand.
            if (EditorApplication.ExecuteMenuItem("Window/Analysis/Profiler")) return;
            if (EditorApplication.ExecuteMenuItem("Window/Profiler")) return;
            Debug.LogWarning("[PerfLint] " + L.Tr(
                "Couldn't open the Profiler from here — open it via Window > Analysis > Profiler.",
                "无法从这里打开 Profiler——请通过 Window > Analysis > Profiler 打开。"));
        }

        /// <summary>Whether Unity's Memory Profiler package is present — its window type only exists when it is.</summary>
        static bool MemoryProfilerInstalled
        {
            get
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetType("Unity.MemoryProfiler.Editor.MemoryProfilerWindow", false) != null) return true;
                return false;
            }
        }

        /// <summary>The one action several findings name in their own text, with the whole trade stated.</summary>
        static NextPlace DeepProfileOffer() => new NextPlace(
            L.Tr("Turn on Deep Profile", "开启 Deep Profile"),
            L.Tr("Without it this cannot be traced to a method. Turning it on recompiles scripts, and ends Play Mode if you are in it — then sample again and the finding will name the source. It also inflates every timing, so turn it back off before measuring a frame rate.",
                 "不开它就无法追到具体方法。开启会重新编译脚本，若正在 Play Mode 会退出——之后重新采样，这条结论就能点出来源。它同时会放大所有耗时数字，量帧率前记得关掉。"),
            () => DeepProfileControl.Set(true));

        static int CountRules(ScanResult scan, params string[] prefixes)
        {
            if (scan == null) return 0;
            int n = 0;
            foreach (var f in scan.Findings)
                foreach (var p in prefixes)
                    if (f.RuleId != null && f.RuleId.StartsWith(p, StringComparison.Ordinal)) { n++; break; }
            return n;
        }

        /// <summary>
        /// Hands a script finding to the main panel's line-level analysis — the one place that can narrow a report to
        /// a single file and offer AI Fix on it. Deliberately a hand-off rather than a reimplementation: that panel is
        /// the detail view by design, and duplicating it here is how the two would start disagreeing.
        /// </summary>
        public static void OpenLineLevelAnalysis(Finding f)
        {
            var loc = LocationOf(f);
            if (!loc.IsScript) return;

            // The destination's empty state depends on why we arrived, and only this side knows. Arriving from a CPU
            // hotspot, "no allocation patterns here" means the hotspot is compute-bound — a real conclusion. Arriving
            // from an allocation finding it means the opposite: the allocation was MEASURED, on a named line, and the
            // static patterns simply do not describe its shape (boxing, a closure, a package call). Same empty list,
            // opposite readings.
            bool aboutAllocation = f.RuleId != null &&
                (f.RuleId.StartsWith("RUN.GC", StringComparison.Ordinal) ||
                 f.RuleId.StartsWith("PERF.GC", StringComparison.Ordinal));
            PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnScript(loc.Path, aboutAllocation);
        }
    }
}
