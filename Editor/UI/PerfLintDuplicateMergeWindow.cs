using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Scanners;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.UI
{
    /// <summary>
    /// Lets the user pick **which copy to keep** before an ASSET.DUP001 merge (the others are reference-redirected and
    /// deleted). The window scans the project **once** into a <see cref="DuplicateReferenceIndex"/> (for the per-copy
    /// counts) and reuses that same index for the merge, so the destructive step rewrites only the referencing files
    /// instead of sweeping the project again. The re-scan after merging is the caller's <c>onDone</c>. Pro gating is
    /// checked before this window opens.
    ///
    /// **Every survivor-dependent thing on screen comes from <see cref="DuplicateAssetMerger.PreviewMerge"/>** — the
    /// default selection, each row's status word, the "keeping this removes N" line, and whether Merge is clickable.
    /// It used to default to the most-referenced copy on its own, which is how a real group offered a Merge button
    /// that could only ever merge zero: a plain copy with 10 references beat an Addressable copy with 3, and an
    /// Addressable copy is exactly the one that can never be deleted. The scanner had already been taught this
    /// (7e6bfec, ASSET.DUP001 <c>PlanMerge</c>); this window had not, so the rule promised one deletable copy and the
    /// dialog defaulted to none. A per-row status word carries it the last inch: a checkbox alone cannot say whether
    /// ticking it keeps or deletes, and in a dialog that deletes files, reading it backwards is not recoverable.
    /// </summary>
    public sealed class PerfLintDuplicateMergeWindow : EditorWindow
    {
        private IReadOnlyList<string> _group;
        private DuplicateReferenceIndex _index;
        private Action _onDone;
        private string _selected;
        // The row element and its path label are carried alongside the controls because BOTH change with the
        // selection: the kept copy's row is washed rather than only ticked (in a dialog that deletes files, "which
        // one survives" must be legible without reading a checkbox), and its path has to stop being blue when the
        // block under it turns blue. See RefreshOutcome.
        private readonly List<(string path, Toggle toggle, Label status, VisualElement row, Label link)> _rows =
            new List<(string, Toggle, Label, VisualElement, Label)>();
        private Label _summary;      // "keeping this removes N of the M other copies" — refreshed on every selection
        private Button _mergeButton; // disabled while the selection would delete nothing

        public static void Open(Finding finding, Action onDone)
        {
            if (finding == null || finding.Group == null || finding.Group.Count < 2) return;

            var w = CreateInstance<PerfLintDuplicateMergeWindow>();
            w.titleContent = new GUIContent(L.Tr("Merge Duplicates", "合并去重"));
            w._group = finding.Group;
            w._onDone = onDone;
            // Single project scan: powers the displayed counts AND is reused by the merge (no second sweep).
            try { w._index = DuplicateAssetMerger.BuildReferenceIndex(finding.Group); }
            catch { w._index = null; }
            w.minSize = new Vector2(560, 360);
            w.BuildUi();
            w.ShowUtility();
        }

        private void BuildUi()
        {
            // Default: whichever copy the most others can actually be merged INTO — the same choice the batch path
            // makes, so the two cannot disagree about a group. Reference count survives inside it as the tie-break.
            _selected = DuplicateAssetMerger.ChooseDefaultKeep(_group, _index);
            if (_selected == null && _group.Count > 0) _selected = _group[0];

            var root = rootVisualElement;
            root.Clear();
            _rows.Clear(); // BuildUi can run twice on one instance; stale rows would leave the outcome pass repainting dead elements
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 12; root.style.paddingLeft = 14; root.style.paddingRight = 14; root.style.paddingBottom = 10;

            root.Add(new Label(L.Tr("Choose the copy to keep", "选择要保留的副本"))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, whiteSpace = WhiteSpace.Normal,
                          color = PerfLintStyle.Ink }
            });
            root.Add(new Label(L.Tr(
                "All copies are byte-for-byte identical. References to the others are redirected to the kept one, then the others are deleted. The default keeps whichever copy the most others can be merged into — a copy marked ⚠ is loaded by path/name/address and can never be deleted, so keeping it is what lets the others go.",
                "所有副本逐字节相同。对其余副本的引用会重定向到保留副本，随后删除其余文件。默认保留「能装下最多其他副本」的那份——带 ⚠ 的副本按路径/名称/address 加载、永远不会被删除，所以保留它才能让其余副本走掉。"))
            { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginTop = 4, marginBottom = 8,
                        color = PerfLintStyle.Dim } });

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            foreach (var path in _group)
            {
                int c = _index != null ? _index.ReferenceCount(path) : 0;
                scroll.Add(BuildRow(path, c));
            }
            root.Add(scroll);

            // A block rather than a grey line. This paragraph is the one that says files get deleted and Undo will
            // not bring them back; it was set at 0.7 opacity and 11 px directly above the button that does it, which
            // is the typography of a footnote.
            var warn = PerfLintStyle.Note(PerfLintStyle.NoteWarning);
            warn.style.marginTop = 8;
            warn.Add(new Label(
                L.Tr("This rewrites serialized files. Requires Asset Serialization = Force Text. ",
                     "此操作会改写序列化文件。要求 Asset Serialization = Force Text。") + PerfLintWarnings.Irreversible)
            { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, color = PerfLintStyle.Ink } });
            root.Add(warn);

            var footer = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0,
                          flexWrap = Wrap.Wrap, marginTop = 4 }
            };
            // What pressing Merge will actually do, in the same row as the button that does it. Wraps rather than
            // pushing the buttons off the right edge (the clipped-button failure tools/eval-window-layout.cs found).
            _summary = new Label { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, whiteSpace = WhiteSpace.Normal,
                                             fontSize = 11, marginRight = 8 } };
            footer.Add(_summary);
            var cancel = PerfLintStyle.Secondary(L.Tr("Cancel", "取消"), Close);
            cancel.style.flexShrink = 0;
            footer.Add(cancel);
            // Primary, not the danger fill. That one means "something is running that you can stop" (see the
            // stylesheet); what makes this destructive is said by the amber block above it and by the confirmation
            // that follows, not by painting the only useful button on the screen red.
            _mergeButton = PerfLintStyle.Primary(L.Tr("Merge", "合并"), DoMerge);
            _mergeButton.style.marginLeft = 8;
            _mergeButton.style.flexShrink = 0;
            footer.Add(_mergeButton);
            root.Add(footer);

            RefreshOutcome();
        }

        /// <summary>
        /// Re-asks <see cref="DuplicateAssetMerger.PreviewMerge"/> for the current survivor and repaints everything
        /// that depends on it: each row's status word, the summary line, and whether Merge can be pressed at all.
        /// Merge is disabled at zero — the PKG001 rule (don't offer an action whose only possible outcome is "did
        /// nothing"), applied to a choice the user is free to make wrong.
        /// </summary>
        private void RefreshOutcome()
        {
            Dictionary<string, DuplicateAssetMerger.CopyOutcome> preview;
            try { preview = DuplicateAssetMerger.PreviewMerge(_group, _selected); }
            catch { preview = null; }

            int deletable = 0, others = 0;
            bool anotherIsPathLoaded = false;
            foreach (var (path, _, status, row, link) in _rows)
            {
                var outcome = preview != null && preview.TryGetValue(path, out var o)
                    ? o : DuplicateAssetMerger.CopyOutcome.KeptUnresolved;
                if (path != _selected)
                {
                    others++;
                    if (outcome == DuplicateAssetMerger.CopyOutcome.WillBeDeleted) deletable++;
                    if (outcome == DuplicateAssetMerger.CopyOutcome.KeptPathLoaded) anotherIsPathLoaded = true;
                }
                status.text = StatusWord(outcome);
                status.tooltip = StatusExplanation(outcome);
                status.style.color = outcome == DuplicateAssetMerger.CopyOutcome.WillBeDeleted
                    ? PerfLintStyle.Amber                           // about to be deleted: the one row worth a colour
                    : PerfLintStyle.Dim;

                // The survivor is washed, not merely ticked. A tick mark cannot say which of "keep" and "delete" it
                // means, and this dialog deletes files — so the one row that lives through the merge is the one the
                // eye lands on first, in the same accent every other "this is the selected thing" uses.
                bool keep = path == _selected;
                row.EnableInClassList("pl-card--info", keep);

                // …and its path stops being blue while it is. The block carries the hue; the writing on it is
                // writing. Blue-on-blue is the exact defect the Scan panel's banners were fixed for, and washing
                // this row reintroduced it under a different class — a path in the link colour, on an accent fill,
                // ruled in accent. The OTHER rows keep the link colour, which is also the honest signal: those are
                // the ones still worth clicking.
                link.style.color = keep ? PerfLintStyle.Ink : PerfLintStyle.Accent;
            }

            if (_summary != null)
            {
                bool ok = deletable > 0;
                _summary.text = ok
                    ? L.Tr($"Keeping this copy removes {deletable} of the {others} other copy/copies.",
                           $"保留这份可删除另 {others} 份副本中的 {deletable} 份。")
                    : (anotherIsPathLoaded
                        ? L.Tr("⚠ Keeping this copy removes nothing — the others are loaded by path/name/address, which a GUID redirect cannot repair. Keep a ⚠ copy instead.",
                               "⚠ 保留这份删不掉任何副本——其余副本按路径/名称/address 加载，GUID 重定向修不了这类加载。改选带 ⚠ 的那份。")
                        : L.Tr("⚠ Keeping this copy removes nothing — the others expose sub-objects it lacks (different import settings), or have changed since the scan.",
                               "⚠ 保留这份删不掉任何副本——其余副本暴露了它没有的子对象（导入设置不同），或自扫描后已改动。"));
                _summary.style.color = ok ? PerfLintStyle.Dim : PerfLintStyle.Amber;
            }

            if (_mergeButton != null)
            {
                _mergeButton.SetEnabled(deletable > 0);
                _mergeButton.tooltip = deletable > 0
                    ? ""
                    : L.Tr("Nothing would be deleted with this copy kept — pick a different one to keep.",
                           "保留这一份的话不会删除任何文件——请改选要保留的副本。");
            }
        }

        /// <summary>The row's fate in two or three words. Deliberately says "kept"/"deleted" rather than relying on the
        /// checkbox: a tick mark cannot tell you which of the two it means, and this dialog deletes files.</summary>
        private static string StatusWord(DuplicateAssetMerger.CopyOutcome outcome)
        {
            switch (outcome)
            {
                case DuplicateAssetMerger.CopyOutcome.Kept: return L.Tr("kept", "保留");
                case DuplicateAssetMerger.CopyOutcome.WillBeDeleted: return L.Tr("will be deleted", "将被删除");
                case DuplicateAssetMerger.CopyOutcome.KeptPathLoaded: return L.Tr("kept (can't be deleted)", "保留（删不掉）");
                case DuplicateAssetMerger.CopyOutcome.KeptDiverged: return L.Tr("kept (changed since scan)", "保留（自扫描后已改动）");
                case DuplicateAssetMerger.CopyOutcome.KeptSubObjects: return L.Tr("kept (sub-objects differ)", "保留（子对象不一致）");
                default: return L.Tr("kept (GUID unavailable)", "保留（GUID 不可用）");
            }
        }

        private static string StatusExplanation(DuplicateAssetMerger.CopyOutcome outcome)
        {
            switch (outcome)
            {
                case DuplicateAssetMerger.CopyOutcome.Kept:
                    return L.Tr("The copy that survives; every other copy's references are redirected here.",
                                "保留下来的那份；其余副本的引用都会重定向到它。");
                case DuplicateAssetMerger.CopyOutcome.WillBeDeleted:
                    return L.Tr("References to this file are redirected to the kept copy, then this file is deleted.",
                                "对该文件的引用会重定向到保留副本，随后删除该文件。");
                case DuplicateAssetMerger.CopyOutcome.KeptPathLoaded:
                    return L.Tr("Loaded by path/name/address (Resources / StreamingAssets / AssetBundle / Addressables). Those loads are not GUID references, so a redirect cannot repair them and this file is never deleted.",
                                "按路径/名称/address 加载（Resources / StreamingAssets / AssetBundle / Addressables）。这类加载不是 GUID 引用，重定向修不了，因此该文件永远不会被删除。");
                case DuplicateAssetMerger.CopyOutcome.KeptDiverged:
                    return L.Tr("No longer byte-identical to the kept copy — it changed since the scan. Never deleted on doubt; re-scan the rule.",
                                "与保留副本已不再逐字节相同——自扫描后被改动过。存疑不删；请重扫该规则。");
                case DuplicateAssetMerger.CopyOutcome.KeptSubObjects:
                    return L.Tr("Exposes sub-objects the kept copy lacks (different import settings), so redirecting would leave dangling references. Align the import settings and re-scan.",
                                "它暴露了保留副本没有的子对象（导入设置不同），重定向会留下悬空引用。请统一导入设置后重扫。");
                default:
                    return L.Tr("No usable GUID for this path, or the same asset is listed twice.",
                                "该路径没有可用的 GUID，或同一资源被列了两次。");
            }
        }

        private VisualElement BuildRow(string path, int refCount)
        {
            // Column wrapper: the selectable row on top, an expandable "which files reference this copy" list below.
            var wrapper = new VisualElement { style = { marginBottom = 3 } };

            // The shared card surface at row density: the theme's own helpbox fill rather than a white overlay at
            // 0.04, which lifts a dark editor by a hair and is invisible on a light one. RefreshOutcome adds
            // pl-card--info to whichever row survives the merge.
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap,
                          paddingTop = 4, paddingBottom = 4, paddingLeft = 8, paddingRight = 8 }
            };
            row.AddToClassList("pl-card");
            PerfLintStyle.Round(row, 6);

            // Toggle acts as a radio: turning one on turns the others off; the selected one can't be turned off.
            var toggle = new Toggle { value = path == _selected };
            toggle.style.marginRight = 6;
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue) Select(path);
                else if (path == _selected) toggle.SetValueWithoutNotify(true); // can't unselect the only choice
            });
            row.Add(toggle);

            // Clickable path → highlight/select the asset in the Project window (doesn't change the kept selection).
            var label = new Label($"{path}")
            {
                tooltip = L.Tr("Click to locate in the Project window", "点击在 Project 窗口中定位"),
                style = { flexGrow = 1, flexShrink = 1, minWidth = 0, whiteSpace = WhiteSpace.Normal,
                          color = PerfLintStyle.Accent }
            };
            label.RegisterCallback<ClickEvent>(_ => ScannerUtil.PingAsset(path));
            row.Add(label);

            // Copies loaded by string path/name (Resources/StreamingAssets/AssetBundle) are never deleted by the merge —
            // flag them so the user knows that choosing to keep a *different* copy won't remove this one.
            if (DuplicateAssetMerger.IsLoadedByStringPath(path))
            {
                label.text += "  ⚠";
                label.tooltip = L.Tr(
                    "Loaded by path/name/address (Resources/StreamingAssets/AssetBundle/Addressables) — won't be merged or deleted. Click to locate.",
                    "按路径/名称/address 加载（Resources/StreamingAssets/AssetBundle/Addressables）——不会被合并或删除。点击定位。");
            }

            // Kept / will be deleted, in words. Filled by RefreshOutcome once every row exists.
            var status = new Label
            {
                style = { fontSize = 11, marginLeft = 8, flexShrink = 0, unityFontStyleAndWeight = FontStyle.Bold }
            };
            row.Add(status);

            string refText = refCount == 1
                ? L.Tr("1 reference", "1 处引用")
                : L.Tr($"{refCount} references", $"{refCount} 处引用");
            row.Add(new Label(refText)
            {
                tooltip = L.Tr("Counts GUID occurrences across serialized files (Assets + ProjectSettings, incl. importer .meta). Expand below to see the files.",
                               "统计 GUID 在序列化文件中出现的次数（Assets + ProjectSettings，含导入 .meta）。展开下方可看具体文件。"),
                style = { fontSize = 11, marginLeft = 8, flexShrink = 0, unityTextAlign = TextAnchor.MiddleRight,
                          color = PerfLintStyle.Dimmer }
            });

            wrapper.Add(row);

            // Expandable list of the actual files that reference this copy (the same set the merge would rewrite).
            // Answers "where are my N references?" without leaving the dialog. Lazily populated on first expand.
            if (refCount > 0)
                wrapper.Add(BuildReferencesFoldout(path));

            _rows.Add((path, toggle, status, row, label));
            return wrapper;
        }

        /// <summary>A collapsed foldout that, on first expand, lists the project files referencing this copy's GUID
        /// (Assets paths are clickable → ping in Project; ProjectSettings paths shown as plain text). Same files the merge rewrites.</summary>
        private VisualElement BuildReferencesFoldout(string path)
        {
            string guid = _index?.GuidOf(path);
            var files = guid != null ? _index.FilesReferencing(guid) : (IReadOnlyList<string>)Array.Empty<string>();

            var foldout = new Foldout
            {
                value = false,
                text = files.Count == 1
                    ? L.Tr("1 referencing file", "1 个引用文件")
                    : L.Tr($"{files.Count} referencing files", $"{files.Count} 个引用文件")
            };
            foldout.style.marginLeft = 24;
            foldout.style.fontSize = 11;

            bool built = false;
            foldout.RegisterValueChangedCallback(evt =>
            {
                if (!evt.newValue || built) return;
                built = true;
                if (files.Count == 0)
                {
                    foldout.Add(new Label(L.Tr("(no files — references may be in binary assets the text scan skips)", "（无文件——引用可能在文本扫描跳过的二进制资源里）"))
                    { style = { fontSize = 11, color = PerfLintStyle.Dimmer } });
                    return;
                }
                foreach (var physical in files)
                {
                    string disp = ToProjectRelative(physical);
                    bool inAssets = disp.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                    // Two tints, not a tint plus an opacity: a ProjectSettings path is not clickable, so it is
                    // written as ordinary quiet text rather than as a faded link.
                    var item = new Label("• " + disp)
                    {
                        style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginLeft = 2, marginTop = 1,
                                  color = inAssets ? PerfLintStyle.Accent : PerfLintStyle.Dimmer }
                    };
                    if (inAssets)
                    {
                        // A .meta reference belongs to its asset (strip the suffix so Ping can resolve it).
                        string pingPath = disp.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? disp.Substring(0, disp.Length - 5) : disp;
                        item.tooltip = L.Tr("Click to locate in the Project window", "点击在 Project 窗口中定位");
                        item.RegisterCallback<ClickEvent>(_ => ScannerUtil.PingAsset(pingPath));
                    }
                    foldout.Add(item);
                }
            });
            return foldout;
        }

        /// <summary>Absolute physical path → project-relative ("Assets/…" or "ProjectSettings/…") for display/Ping. Prefers the deepest match.</summary>
        private static string ToProjectRelative(string physical)
        {
            if (string.IsNullOrEmpty(physical)) return physical;
            string n = physical.Replace('\\', '/');
            int i = n.LastIndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n.Substring(i + 1);
            i = n.LastIndexOf("/ProjectSettings/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n.Substring(i + 1);
            return n;
        }

        private void Select(string path)
        {
            _selected = path;
            foreach (var (p, t, _, _, _) in _rows) t.SetValueWithoutNotify(p == path);
            RefreshOutcome();            // every row's status and the Merge button depend on WHICH copy is kept
            ScannerUtil.PingAsset(path); // locate the to-be-kept asset in the Project window
        }

        private void DoMerge()
        {
            if (string.IsNullOrEmpty(_selected)) return;

            // Re-asked rather than reused from the last RefreshOutcome: the window can sit open while the project
            // changes underneath it, and this is the last point before files are deleted. The button is disabled at
            // zero, so reaching here with none is a state change, not a misclick.
            int deletable = DuplicateAssetMerger.DeletableWith(_group, _selected);
            if (deletable <= 0) { RefreshOutcome(); return; }

            // Final confirmation: this is destructive and not Edit>Undo-able (unified back-up-first warning).
            // States the COUNT, not "the other copies" — with a path-loaded copy in the group those are not the same
            // number, and the dialog immediately before a delete is the wrong place to round up.
            bool ok = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Merge Duplicates", "PerfLint — 合并去重"),
                L.Tr($"Keep:\n{_selected}\n\n{deletable} identical copy/copies will have their references redirected here and then be deleted.\n\n",
                     $"保留：\n{_selected}\n\n将有 {deletable} 份相同副本的引用重定向到这里，随后被删除。\n\n") + PerfLintWarnings.Irreversible,
                L.Tr("Merge", "合并"), L.Tr("Cancel", "取消"));
            if (!ok) return;

            var group = _group;
            var index = _index;
            var onDone = _onDone;
            string keep = _selected;
            Close();

            // Reuse the index built at open time → rewrites only the referencing files (no second project scan).
            FixResult r = DuplicateAssetMerger.Merge(group, keep, index);
            if (r.Success) EditorUtility.DisplayDialog(L.Tr("Merged", "已合并"), r.Message, "OK");
            else EditorUtility.DisplayDialog(L.Tr("Merge failed", "合并失败"), r.Message, "OK");
            onDone?.Invoke();
        }
    }
}
