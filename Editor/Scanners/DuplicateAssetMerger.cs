using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// The **executable** counterpart of ASSET.DUP001 (byte-for-byte identical asset files): the "real dedup" that
    /// keeps one canonical copy, **redirects every project-wide GUID reference** from the other copies to it, and
    /// **deletes the redundant files** — the heaviest, highest-value Pro action. DUP001 the scanner only reports;
    /// this is the one-click merge.
    ///
    /// **Performance**: the project is scanned **once** into a <see cref="DuplicateReferenceIndex"/> (parallelized, with
    /// a progress bar). The chooser shows the per-copy reference counts from it; the merge reuses the same index and
    /// rewrites **only the files that actually reference a deleted copy** — not a second full-project sweep.
    ///
    /// **Which copy is kept**: the caller may pass an explicit <c>keepPath</c> (the user picks it in the chooser); when
    /// omitted, the **most-referenced** copy is kept (fewest references to rewrite), ties broken by ordinal path order.
    ///
    /// Why text-level GUID redirect (not an AssetDatabase API): Unity exposes no public "remap GUID" call, so the
    /// canonical technique is to rewrite the 32-hex GUID string inside the serialized YAML / .meta files that
    /// reference a duplicate. That requires **ForceText** serialization (binary YAML can't be safely string-edited),
    /// which is guarded below.
    ///
    /// Safety (this op is destructive and **not** Edit&gt;Undo-able — the confirm dialog tells users to commit first):
    ///   1. ForceText serialization required, else refuse.
    ///   2. Each copy is **re-verified byte-identical** to the kept asset at merge time (the project may have changed
    ///      since the scan) — a diverged file is skipped, never deleted.
    ///   3. A copy is merged only when its set of local fileIDs (main + sub-assets) is a **subset** of the kept
    ///      asset's. Identical source bytes produce identical fileIDs only when import settings also match; if a copy
    ///      carries a sub-asset fileID the kept asset lacks, references through that fileID would dangle after
    ///      redirect — so that copy is skipped (reported, not deleted). This guarantees no broken references.
    ///   4. A copy **loaded by string path / name** (under <c>Resources/</c> or <c>StreamingAssets/</c>, or explicitly
    ///      assigned to an AssetBundle) is never deleted — those loads (<c>Resources.Load("…")</c>, streaming reads,
    ///      <c>bundle.LoadAsset("name")</c>) are not GUID references, so a redirect can't fix them; deleting such a
    ///      copy would silently break runtime loading. See <see cref="IsLoadedByStringPath"/>.
    /// </summary>
    public static class DuplicateAssetMerger
    {
        /// <summary>
        /// Optional probe, injected by the Addressables sub-module (gated by PERFLINT_ADDRESSABLES) at domain load:
        /// "is this asset an Addressables entry?". Addressable assets are loaded by address (a string), which a GUID
        /// redirect can't fix, so such a copy must never be deleted — see <see cref="IsLoadedByStringPath"/>. Null when
        /// the Addressables package isn't installed (then there are no address-based loads to worry about).
        /// </summary>
        public static System.Func<string, bool> AddressableEntryHook;

        /// <summary>
        /// Extensions whose payload is opaque binary (or plain source) and can never contain an ASCII GUID reference —
        /// skipped when sweeping files, purely for speed. Everything else (YAML scenes/prefabs/assets, .meta importer
        /// blocks that remap materials/sub-objects by GUID, ShaderGraph/JSON, …) is scanned.
        /// </summary>
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".gif", ".bmp", ".tif", ".tiff", ".exr", ".hdr", ".pdf",
            ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".flac", ".mod", ".it", ".s3m", ".xm",
            ".fbx", ".blend", ".max", ".mb", ".ma", ".3ds", ".dae", ".obj",
            ".ttf", ".otf", ".dfont",
            ".dll", ".so", ".a", ".dylib", ".bytes", ".bin", ".pdb", ".mdb",
            ".mp4", ".mov", ".webm", ".avi", ".zip", ".gz", ".7z",
            ".cs", ".hlsl", ".cginc", ".glslinc", ".hlslinc",
        };

        /// <summary>
        /// Matches a standalone 32-hex GUID token (Unity asset GUIDs). Boundaries reject longer hex runs (e.g. a 64-hex
        /// hash) so we don't false-match a substring. Lets the scan extract every GUID in one pass and look each up in
        /// the target set — far cheaper than an IndexOf per candidate GUID when the set is large (batch "merge all").
        /// </summary>
        private static readonly Regex GuidToken =
            new Regex("(?<![0-9a-fA-F])[0-9a-f]{32}(?![0-9a-fA-F])", RegexOptions.Compiled);

        /// <summary>
        /// Keep <paramref name="keepPath"/> (or, when null, the most-referenced copy), redirect references of the
        /// remaining byte-identical copies to it, and delete them. The group must come from ASSET.DUP001 (already
        /// proven byte-identical); <paramref name="keepPath"/> must be a member of <paramref name="group"/>.
        /// Pass a prebuilt <paramref name="index"/> (e.g. from the chooser) to avoid re-scanning the project.
        /// </summary>
        public static FixResult Merge(IReadOnlyList<string> group, string keepPath = null, DuplicateReferenceIndex index = null, bool refreshAfter = true)
        {
            if (group == null || group.Count < 2)
                return FixResult.Fail(L.Tr("Nothing to merge (need at least two identical copies).", "无可合并项（至少需要两份相同副本）。"));

            if (EditorSettings.serializationMode != SerializationMode.ForceText)
                return FixResult.Fail(L.Tr(
                    "Asset Serialization must be set to \"Force Text\" (Project Settings > Editor) for reference redirection. Binary/mixed serialization can't be safely edited.",
                    "引用重定向要求 Asset Serialization 设为「Force Text」（Project Settings > Editor）。二进制/混合序列化无法安全编辑。"));

            if (index == null) index = BuildReferenceIndex(group);

            string canonical = string.IsNullOrEmpty(keepPath) ? ChooseDefaultKeep(group, index) : keepPath;
            if (string.IsNullOrEmpty(canonical) || !group.Contains(canonical))
                return FixResult.Fail(L.Tr($"The asset to keep is not part of this duplicate group: {keepPath}", $"要保留的资源不在该重复组内：{keepPath}"));

            string canonGuid = AssetDatabase.AssetPathToGUID(canonical);
            if (string.IsNullOrEmpty(canonGuid))
                return FixResult.Fail(L.Tr($"Canonical asset GUID not found: {canonical}", $"找不到保留资源的 GUID：{canonical}"));

            var canonIds = LocalFileIds(canonical);

            // 1) Decide which copies are safe to merge (byte-identical + fileID subset); build the GUID remap.
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            var toDelete = new List<string>();
            var skipped = new List<string>();
            var skippedPathLoaded = new List<string>();
            foreach (var dup in group)
            {
                if (dup == canonical) continue;
                string dupGuid = AssetDatabase.AssetPathToGUID(dup);
                if (string.IsNullOrEmpty(dupGuid) || dupGuid == canonGuid) { skipped.Add(dup); continue; }

                // Loaded by string path/name (Resources/StreamingAssets/AssetBundle): a GUID redirect can't fix those
                // loads, so deleting this copy would silently break runtime loading. Never delete it.
                if (IsLoadedByStringPath(dup)) { skippedPathLoaded.Add(dup); continue; }

                // Re-verify identical at merge time: the file may have diverged since the scan. Never delete on doubt.
                if (!FilesEqual(canonical, dup)) { skipped.Add(dup); continue; }

                // fileID subset: every object the copy exposes must also exist in the kept asset, or a reference
                // through a copy-only sub-asset fileID would dangle once we point its GUID at the kept asset.
                var dupIds = LocalFileIds(dup);
                if (!IsSubset(dupIds, canonIds)) { skipped.Add(dup); continue; }

                remap[dupGuid] = canonGuid;
                toDelete.Add(dup);
            }

            if (remap.Count == 0)
            {
                if (skippedPathLoaded.Count > 0 && skipped.Count == 0)
                    return FixResult.Fail(L.Tr(
                        "No copy could be safely merged: the other copies live under Resources/ or StreamingAssets/, are assigned to an AssetBundle, or are Addressables entries — they're loaded by path/name/address, which a GUID redirect can't fix, so deleting them would break runtime loading. Dedup these manually.",
                        "没有可安全合并的副本：其余副本位于 Resources/ 或 StreamingAssets/、已分配到 AssetBundle、或是 Addressables 条目——它们按路径/名称/address 加载，GUID 重定向无法修复，删除会破坏运行时加载。这类请手动去重。"));
                return FixResult.Fail(L.Tr(
                    "No copy could be safely merged: the copies differ from the kept asset in their imported sub-objects (different import settings), so redirecting references would break them. Align their import settings first, or dedup manually.",
                    "没有可安全合并的副本：副本与保留资源的导入子对象不一致（导入设置不同），重定向引用会破坏它们。请先统一导入设置，或手动去重。"));
            }

            // 2) Rewrite only the files the index says reference a to-be-deleted copy (no second full-project sweep).
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dupGuid in remap.Keys)
                foreach (var f in index.FilesReferencing(dupGuid))
                    files.Add(f);

            int fileHits = 0, refHits = 0;
            foreach (var file in files)
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }                    // unreadable/locked/deleted since scan: skip

                string rewritten = RedirectGuids(text, remap, out int n);
                if (n == 0) continue;

                try { File.WriteAllText(file, rewritten); }
                catch (Exception e)
                {
                    return FixResult.Fail(L.Tr($"Could not write {file}: {e.Message} (no files were deleted).",
                                               $"无法写入 {file}：{e.Message}（未删除任何文件）。"));
                }
                fileHits++;
                refHits += n;
            }

            // 3) Delete the now-orphaned copies (references already point at the kept asset).
            int deleted = 0;
            var deleteErrors = new List<string>();
            foreach (var dup in toDelete)
            {
                if (AssetDatabase.DeleteAsset(dup)) deleted++;
                else deleteErrors.Add(dup);
            }

            if (refreshAfter) AssetDatabase.Refresh();

            var sb = new StringBuilder();
            sb.Append(L.Tr($"Merged {deleted} copy/copies into {canonical}: redirected {refHits} reference(s) across {fileHits} file(s).",
                           $"已将 {deleted} 份副本合并到 {canonical}：在 {fileHits} 个文件中重定向 {refHits} 处引用。"));
            if (skipped.Count > 0)
                sb.Append(L.Tr($" Skipped {skipped.Count} copy/copies (diverged or different sub-objects).",
                               $" 跳过 {skipped.Count} 份（已改动或子对象不同）。"));
            if (skippedPathLoaded.Count > 0)
                sb.Append(L.Tr($" Kept {skippedPathLoaded.Count} copy/copies that load by path/name/address (Resources/StreamingAssets/AssetBundle/Addressables — not safe to auto-remove).",
                               $" 保留 {skippedPathLoaded.Count} 份按路径/名称/address 加载的副本（Resources/StreamingAssets/AssetBundle/Addressables，不可自动删除）。"));
            if (deleteErrors.Count > 0)
                sb.Append(L.Tr($" {deleteErrors.Count} could not be deleted.", $" {deleteErrors.Count} 份无法删除。"));
            return FixResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Batch-merge many duplicate groups with a **single** project scan (instead of one scan per group — 500 groups
        /// would otherwise trigger 500 full scans). Builds one shared index covering every copy in every group, then
        /// merges each group (keeping its most-referenced copy) reusing that index, and reimports/refreshes **once** at
        /// the end. Used by the rule-level "merge all" button.
        /// </summary>
        public static FixResult MergeAll(IReadOnlyList<IReadOnlyList<string>> groups)
        {
            if (groups == null || groups.Count == 0)
                return FixResult.Fail(L.Tr("Nothing to merge.", "无可合并项。"));

            if (EditorSettings.serializationMode != SerializationMode.ForceText)
                return FixResult.Fail(L.Tr(
                    "Asset Serialization must be set to \"Force Text\" (Project Settings > Editor) for reference redirection. Binary/mixed serialization can't be safely edited.",
                    "引用重定向要求 Asset Serialization 设为「Force Text」（Project Settings > Editor）。二进制/混合序列化无法安全编辑。"));

            // One scan covering every copy in every group; reused by each per-group merge.
            var allPaths = new List<string>();
            foreach (var g in groups)
                if (g != null)
                    allPaths.AddRange(g);
            var index = BuildReferenceIndex(allPaths);

            bool progress = !Application.isBatchMode;
            // Batch the deletes/reimports: defer asset processing until all groups are rewritten, then one Refresh.
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    if (progress)
                        EditorUtility.DisplayProgressBar(
                            "PerfLint",
                            L.Tr($"Merging group {i + 1}/{groups.Count}…", $"合并第 {i + 1}/{groups.Count} 组…"),
                            groups.Count == 0 ? 1f : (float)i / groups.Count);

                    Merge(groups[i], null, index, refreshAfter: false);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                if (progress) EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            // Report by the ACTUAL on-disk outcome (after deletes are applied), not by per-call success: a group is
            // truly done only if ≤1 copy survives. A group that still has ≥2 surviving copies — multiple path-loaded
            // copies that can't be deleted, or one a merge couldn't touch — will be reported again by a re-scan, so
            // count it honestly as "still needs manual handling" (otherwise the summary undercounts what's left).
            int done = 0, remaining = 0;
            foreach (var g in groups)
            {
                if (g == null) continue;
                int survivors = 0;
                foreach (var p in g)
                {
                    try { if (File.Exists(ScannerUtil.ToPhysicalFullPath(p))) survivors++; }
                    catch { /* ignore */ }
                }
                if (survivors <= 1) done++; else remaining++;
            }

            var sb = new StringBuilder();
            sb.Append(L.Tr($"Deduplicated {done} of {done + remaining} group(s).", $"已去重 {done + remaining} 组中的 {done} 组。"));
            if (remaining > 0)
                sb.Append(L.Tr(
                    $"\n\n{remaining} group(s) still have duplicates that can't be auto-merged: they contain multiple copies loaded by path/name/address (Resources / StreamingAssets / AssetBundle / Addressables), or copies with different import settings. Dedup these manually.",
                    $"\n\n{remaining} 组仍有无法自动合并的重复：含多份按路径/名称/address 加载的副本（Resources / StreamingAssets / AssetBundle / Addressables），或导入设置不同的副本，请手动处理。"));
            return FixResult.Ok(sb.ToString());
        }

        /// <summary>The default "keep" choice: the most-referenced copy (fewest references to rewrite), ties → ordinal order.</summary>
        public static string ChooseDefaultKeep(IReadOnlyList<string> group)
        {
            if (group == null || group.Count == 0) return null;
            return ChooseDefaultKeep(group, BuildReferenceIndex(group, showProgress: false));
        }

        private static string ChooseDefaultKeep(IReadOnlyList<string> group, DuplicateReferenceIndex index)
        {
            if (group == null || group.Count == 0) return null;
            string best = null;
            int bestCount = -1;
            // Iterate the group in its given (ordinal-sorted) order; strict '>' keeps the first on ties → lowest ordinal.
            foreach (var p in group)
            {
                int c = index.ReferenceCount(p);
                if (c > bestCount) { bestCount = c; best = p; }
            }
            return best ?? group[0];
        }

        /// <summary>Per-copy project-wide reference counts (thin wrapper over <see cref="BuildReferenceIndex"/>).</summary>
        public static IReadOnlyDictionary<string, int> CountReferences(IReadOnlyList<string> group)
            => BuildReferenceIndex(group, showProgress: false).CountByPath;

        /// <summary>
        /// Scans Assets/ + ProjectSettings/ **once** (parallelized) and records, per group copy: how many references
        /// point at it and which files contain them. A copy's own .meta (its GUID definition, not a reference) is
        /// excluded, so a truly unreferenced copy counts 0.
        /// </summary>
        public static DuplicateReferenceIndex BuildReferenceIndex(IReadOnlyList<string> group, bool showProgress = true)
        {
            var guidByPath = new Dictionary<string, string>(StringComparer.Ordinal);
            var countByGuid = new Dictionary<string, int>(StringComparer.Ordinal);
            var filesByGuid = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ownMeta = new Dictionary<string, string>(StringComparer.Ordinal); // guid -> its own .meta (normalized)

            if (group != null)
            {
                foreach (var p in group)
                {
                    string g = AssetDatabase.AssetPathToGUID(p);
                    if (string.IsNullOrEmpty(g) || guidByPath.ContainsValue(g)) { continue; }
                    guidByPath[p] = g;
                    countByGuid[g] = 0;
                    filesByGuid[g] = new List<string>();
                    try { ownMeta[g] = NormalizePath(ScannerUtil.ToPhysicalFullPath(p) + ".meta"); } catch { /* leave unset */ }
                }
            }

            if (guidByPath.Count > 0)
            {
                var targetGuids = new HashSet<string>(guidByPath.Values, StringComparer.Ordinal);
                var files = EnumerateReferenceFiles().ToList();
                var lockObj = new object();
                bool progress = showProgress && !Application.isBatchMode;
                // Smaller chunks → the progress bar advances more smoothly (one tick per chunk); a big chunk makes the
                // bar jump in coarse steps that look stalled on large projects. (Doesn't change total time — the editor
                // main thread is busy throughout; Unity's "(busy for Ns)" suffix is just its long-task indicator.)
                const int chunk = 128;
                try
                {
                    for (int i = 0; i < files.Count; i += chunk)
                    {
                        if (progress)
                            // Short title: Unity appends "(busy for Ns)" to it and truncates the small fixed-size
                            // progress window with an ellipsis when it gets long. Keep the detail in the info line.
                            EditorUtility.DisplayProgressBar(
                                "PerfLint",
                                L.Tr("Scanning references…", "扫描引用…"),
                                files.Count == 0 ? 1f : (float)i / files.Count);

                        int end = Math.Min(files.Count, i + chunk);
                        Parallel.For(i, end, j =>
                        {
                            string file = files[j];
                            string text;
                            try { text = File.ReadAllText(file); } catch { return; }
                            if (text.IndexOf("guid", StringComparison.OrdinalIgnoreCase) < 0) return; // cheap reject

                            string fileNorm = NormalizePath(file);
                            // Single pass: extract every GUID token, keep the ones we're tracking. Accumulate locally
                            // first to take the lock once per file instead of once per hit.
                            Dictionary<string, int> local = null;
                            foreach (Match m in GuidToken.Matches(text))
                            {
                                string tok = m.Value;
                                if (!targetGuids.Contains(tok)) continue;
                                if (ownMeta.TryGetValue(tok, out string meta) && meta == fileNorm) continue; // self-definition
                                if (local == null) local = new Dictionary<string, int>(StringComparer.Ordinal);
                                local.TryGetValue(tok, out int cc);
                                local[tok] = cc + 1;
                            }
                            if (local != null)
                                lock (lockObj)
                                    foreach (var kv in local)
                                    {
                                        countByGuid[kv.Key] += kv.Value;
                                        filesByGuid[kv.Key].Add(file);
                                    }
                        });
                    }
                }
                finally
                {
                    if (progress) EditorUtility.ClearProgressBar();
                }
            }

            var countByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            if (group != null)
                foreach (var p in group)
                    countByPath[p] = guidByPath.TryGetValue(p, out string g) && countByGuid.TryGetValue(g, out int c) ? c : 0;

            return new DuplicateReferenceIndex(countByPath, filesByGuid, guidByPath);
        }

        /// <summary>All reference-bearing files under Assets/ and ProjectSettings/ (binary/source extensions excluded).</summary>
        private static IEnumerable<string> EnumerateReferenceFiles()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var roots = new[]
            {
                Application.dataPath,                          // <project>/Assets
                Path.Combine(projectRoot, "ProjectSettings"),  // scene list, graphics/quality settings, …
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var file in files)
                {
                    if (BinaryExtensions.Contains(Path.GetExtension(file))) continue;
                    yield return file;
                }
            }
        }

        /// <summary>
        /// Pure helper (unit-testable): replace each duplicate GUID with its canonical GUID in <paramref name="text"/>,
        /// returning the rewritten text and the number of replacements. GUIDs are 32-hex unique tokens, so a plain
        /// substring replace is collision-safe.
        /// </summary>
        internal static string RedirectGuids(string text, IReadOnlyDictionary<string, string> remap, out int replacements)
        {
            replacements = 0;
            if (string.IsNullOrEmpty(text) || remap == null || remap.Count == 0) return text;

            foreach (var kv in remap)
            {
                string from = kv.Key, to = kv.Value;
                if (string.IsNullOrEmpty(from) || from == to) continue;

                int idx = text.IndexOf(from, StringComparison.Ordinal);
                if (idx < 0) continue;

                var sb = new StringBuilder(text.Length);
                int prev = 0;
                while (idx >= 0)
                {
                    sb.Append(text, prev, idx - prev).Append(to);
                    replacements++;
                    prev = idx + from.Length;
                    idx = text.IndexOf(from, prev, StringComparison.Ordinal);
                }
                sb.Append(text, prev, text.Length - prev);
                text = sb.ToString();
            }
            return text;
        }

        /// <summary>
        /// True when an asset is loaded by string path / name / address rather than by GUID reference, so deleting a
        /// duplicate of it would silently break loading that a GUID redirect can't repair: anything under a
        /// <c>Resources/</c> or <c>StreamingAssets/</c> folder (<c>Resources.Load</c> / streaming reads), explicitly
        /// assigned to an AssetBundle (<c>bundle.LoadAsset("name")</c>), or an Addressables entry (loaded by address,
        /// via <see cref="AddressableEntryHook"/> when the package is installed). The chooser also uses this to
        /// annotate such rows.
        /// </summary>
        public static bool IsLoadedByStringPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (assetPath.IndexOf("/StreamingAssets/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try
            {
                var imp = AssetImporter.GetAtPath(assetPath);
                if (imp != null && !string.IsNullOrEmpty(imp.assetBundleName)) return true;
            }
            catch { /* importer unavailable: fall through */ }
            try
            {
                if (AddressableEntryHook != null && AddressableEntryHook(assetPath)) return true;
            }
            catch { /* hook failure: treat as not-addressable */ }
            return false;
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant(); }
            catch { return path; }
        }

        /// <summary>Local fileIDs of every object an asset exposes (main + sub-assets). Empty for unloadable assets (e.g. scenes).</summary>
        private static HashSet<long> LocalFileIds(string assetPath)
        {
            var ids = new HashSet<long>();
            try
            {
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (obj == null) continue;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string _, out long localId))
                        ids.Add(localId);
                }
            }
            catch { /* unloadable: leave empty — subset check below treats it as "no sub-objects to break" */ }
            return ids;
        }

        private static bool IsSubset(HashSet<long> sub, HashSet<long> super)
        {
            foreach (var id in sub)
                if (!super.Contains(id)) return false;
            return true;
        }

        /// <summary>Byte-for-byte file comparison, resolving Unity asset paths to physical paths. Returns false on any error.</summary>
        private static bool FilesEqual(string assetPathA, string assetPathB)
        {
            try
            {
                string a = ScannerUtil.ToPhysicalFullPath(assetPathA);
                string b = ScannerUtil.ToPhysicalFullPath(assetPathB);
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists) return false;
                if (fa.Length != fb.Length) return false;

                using (var sa = fa.OpenRead())
                using (var sb = fb.OpenRead())
                {
                    var ba = new byte[64 * 1024];
                    var bb = new byte[64 * 1024];
                    int ra;
                    while ((ra = sa.Read(ba, 0, ba.Length)) > 0)
                    {
                        int off = 0;
                        while (off < ra)
                        {
                            int rb = sb.Read(bb, off, ra - off);
                            if (rb <= 0) return false;
                            off += rb;
                        }
                        for (int i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
                    }
                    return sb.Read(bb, 0, 1) == 0; // both must end together
                }
            }
            catch { return false; }
        }
    }
}
