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
    /// deleted). Defaults to the most-referenced copy (fewest references to rewrite). The window scans the project
    /// **once** into a <see cref="DuplicateReferenceIndex"/> (for the per-copy counts) and reuses that same index for
    /// the merge, so the destructive step rewrites only the referencing files instead of sweeping the project again.
    /// The re-scan after merging is the caller's <c>onDone</c>. Pro gating is checked before this window opens.
    /// </summary>
    public sealed class PerfLintDuplicateMergeWindow : EditorWindow
    {
        private IReadOnlyList<string> _group;
        private DuplicateReferenceIndex _index;
        private Action _onDone;
        private string _selected;
        private readonly List<(string path, Toggle toggle)> _rows = new List<(string, Toggle)>();

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
            // Default: most-referenced (ties → the group's given/ordinal order).
            int best = -1;
            foreach (var p in _group)
            {
                int c = _index != null ? _index.ReferenceCount(p) : 0;
                if (c > best) { best = c; _selected = p; }
            }
            if (_selected == null && _group.Count > 0) _selected = _group[0];

            var root = rootVisualElement;
            root.Clear();
            root.style.paddingTop = 8; root.style.paddingLeft = 8; root.style.paddingRight = 8; root.style.paddingBottom = 8;

            root.Add(new Label(L.Tr("Choose the copy to keep", "选择要保留的副本"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            root.Add(new Label(L.Tr(
                "All copies are byte-for-byte identical. References to the others are redirected to the kept one, then the others are deleted. Keeping the most-referenced copy rewrites the fewest references.",
                "所有副本逐字节相同。对其余副本的引用会重定向到保留副本，随后删除其余文件。保留被引用最多的那份，需要改写的引用最少。"))
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, fontSize = 11, marginTop = 2, marginBottom = 6 } });

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            foreach (var path in _group)
            {
                int c = _index != null ? _index.ReferenceCount(path) : 0;
                scroll.Add(BuildRow(path, c));
            }
            root.Add(scroll);

            root.Add(new Label(
                L.Tr("This rewrites serialized files. Requires Asset Serialization = Force Text. ",
                     "此操作会改写序列化文件。要求 Asset Serialization = Force Text。") + PerfLintWarnings.Irreversible)
            { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.7f, fontSize = 11, marginTop = 6, marginBottom = 4 } });

            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0 } };
            footer.Add(new VisualElement { style = { flexGrow = 1 } });
            footer.Add(new Button(Close) { text = L.Tr("Cancel", "取消") });
            var merge = new Button(DoMerge) { text = L.Tr("Merge", "合并") };
            merge.style.marginLeft = 6;
            footer.Add(merge);
            root.Add(footer);
        }

        private VisualElement BuildRow(string path, int refCount)
        {
            // Column wrapper: the selectable row on top, an expandable "which files reference this copy" list below.
            var wrapper = new VisualElement { style = { marginBottom = 3 } };

            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                          paddingTop = 3, paddingBottom = 3, paddingLeft = 6, paddingRight = 6,
                          backgroundColor = new Color(1, 1, 1, 0.04f) }
            };

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
                style = { flexGrow = 1, whiteSpace = WhiteSpace.Normal, color = new Color(0.55f, 0.78f, 1f) }
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

            string refText = refCount == 1
                ? L.Tr("1 reference", "1 处引用")
                : L.Tr($"{refCount} references", $"{refCount} 处引用");
            row.Add(new Label(refText)
            {
                tooltip = L.Tr("Counts GUID occurrences across serialized files (Assets + ProjectSettings, incl. importer .meta). Expand below to see the files.",
                               "统计 GUID 在序列化文件中出现的次数（Assets + ProjectSettings，含导入 .meta）。展开下方可看具体文件。"),
                style = { opacity = 0.7f, fontSize = 11, marginLeft = 8, unityTextAlign = TextAnchor.MiddleRight }
            });

            wrapper.Add(row);

            // Expandable list of the actual files that reference this copy (the same set the merge would rewrite).
            // Answers "where are my N references?" without leaving the dialog. Lazily populated on first expand.
            if (refCount > 0)
                wrapper.Add(BuildReferencesFoldout(path));

            _rows.Add((path, toggle));
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
                    { style = { opacity = 0.6f, fontSize = 11 } });
                    return;
                }
                foreach (var physical in files)
                {
                    string disp = ToProjectRelative(physical);
                    bool inAssets = disp.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                    var item = new Label("• " + disp)
                    {
                        style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginLeft = 2, marginTop = 1,
                                  opacity = inAssets ? 1f : 0.6f,
                                  color = inAssets ? new Color(0.55f, 0.78f, 1f) : (StyleColor)StyleKeyword.Null }
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
            foreach (var (p, t) in _rows) t.SetValueWithoutNotify(p == path);
            ScannerUtil.PingAsset(path); // locate the to-be-kept asset in the Project window
        }

        private void DoMerge()
        {
            if (string.IsNullOrEmpty(_selected)) return;

            // Final confirmation: this is destructive and not Edit>Undo-able (unified back-up-first warning).
            bool ok = EditorUtility.DisplayDialog(
                L.Tr("PerfLint — Merge Duplicates", "PerfLint — 合并去重"),
                L.Tr($"Keep:\n{_selected}\n\nThe other identical copies will have their references redirected here and then be deleted.\n\n",
                     $"保留：\n{_selected}\n\n其余相同副本的引用将重定向到这里，随后被删除。\n\n") + PerfLintWarnings.Irreversible,
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
