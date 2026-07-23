using System.Collections.Generic;
using System.Linq;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Performance domain: the Mipmap Streaming advisor.
    ///   PERF.TEXSTR001 — A meaningful pool (estimated ≥64MB) of streaming-eligible scene textures (mipmapped, non-UI,
    ///     ≥512px) is NOT participating in Mipmap Streaming. Streaming loads only the mip levels the camera actually
    ///     needs — trading a little CPU for a lot of texture memory (real-world course case: 280MB → 176MB with default
    ///     settings). One project-level Info finding with the quantified pool + a one-click enable (Pro action).
    ///   PERF.TEXSTR002 — Textures have "Stream Mip Maps" set in their importer, but Texture Streaming is OFF in the
    ///     active Quality Settings — one half of the switch pair is dead. Info + one-click enable of the global switch.
    ///
    /// History note: a blanket "streaming not enabled" rule was previously REJECTED (ledger §3-60-③, high false-positive
    /// risk — streaming off is a legitimate default). This rule is the approved revision (ledger §3-62-①): it fires only
    /// when the eligible pool is big enough that the memory left on the table is real, reports ONE quantified finding
    /// instead of per-texture spam, and frames it as an opportunity ("up to ~X MB"), never a defect.
    ///
    /// Sizing uses importer metadata only (dimensions × format bits-per-pixel × 1.33 mip factor) — deliberately NOT
    /// ScannerUtil.StorageMemoryBytes, which loads the texture (loading every texture crashes large projects; see
    /// TextureImportScanner's crash note). The estimate is labeled approximate.
    ///
    /// After enabling, tuning/verification (Memory Budget etc.) lives in the Runtime Profiler window's Texture
    /// Streaming section — SRP has no built-in mip debug view, which is exactly why the panel exists.
    /// </summary>
    // ISceneScoped: the finding itself is project-wide (eligibility pool), but its actionable number — the
    // perceivable ceiling — reflects the OPEN scene(s) since the 2026-07-17 scene-scoping of memory optimization,
    // so the "open your heaviest scene and re-scan" notice applies to this scanner too.
    public sealed class TextureStreamingScanner : IScanner, ISceneScoped
    {
        public string Name => "Texture Streaming";
        public Domain Domain => Domain.Performance;

        /// <summary>Minimum eligible-pool estimate before TEXSTR001 speaks. Internal-settable for tests.</summary>
        internal static long ThresholdBytes = 64L * 1024 * 1024;

        /// <summary>Eligible textures must be at least this big on the long edge — small textures have little mip memory to reclaim.</summary>
        private const int MinDim = 512;

        /// <summary>Eligible-pool scan result (separated from Scan so tests can assert candidate membership directly).</summary>
        internal sealed class Pool
        {
            public List<string> Candidates = new List<string>(); // eligible but not flagged Stream Mip Maps
            public long CandidateBytes;
            public int FlaggedCount;                             // textures already flagged Stream Mip Maps
            /// <summary>Per-candidate byte estimate — feeds the per-scene pools (scene deps ∩ candidates).</summary>
            public Dictionary<string, long> CandidateBytesByPath = new Dictionary<string, long>(System.StringComparer.Ordinal);
        }

        internal Pool CollectPool(ScanContext context)
        {
            string platform = !string.IsNullOrEmpty(context.TargetPlatform)
                ? context.TargetPlatform
                : ScannerUtil.ActivePlatformName();

            var pool = new Pool();
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(Name, (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (ScannerUtil.IsPerfLintOwnAsset(path)) continue;
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
                if (!importer.mipmapEnabled) continue;
                // UI/sprite/cursor textures render at fixed screen distance — streaming buys nothing there;
                // lightmaps are engine-managed. Keep to the texture types a 3D camera actually samples by distance.
                if (importer.textureType != TextureImporterType.Default
                    && importer.textureType != TextureImporterType.NormalMap) continue;

                if (importer.streamingMipmaps)
                {
                    pool.FlaggedCount++;
                    continue;
                }

                TextureImportScanner.GetImportedSize(importer, platform, out int w, out int h);
                if (Mathf.Max(w, h) < MinDim) continue;

                pool.Candidates.Add(path);
                long bytes = EstimateBytes(w, h, TextureImportScanner.IsEffectivelyUncompressed(importer, platform));
                pool.CandidateBytes += bytes;
                pool.CandidateBytesByPath[path] = bytes;
            }
            return pool;
        }

        public IEnumerable<Finding> Scan(ScanContext context)
        {
            bool streamingActive = QualitySettings.streamingMipmapsActive;
            var pool = CollectPool(context);
            var candidates = pool.Candidates;
            long candidateBytes = pool.CandidateBytes;
            int flaggedCount = pool.FlaggedCount;

            // TEXSTR002 — flags set but the global switch is off: the per-texture work is currently doing nothing.
            if (!streamingActive && flaggedCount > 0)
            {
                yield return new Finding(
                    ruleId: "PERF.TEXSTR002",
                    domain: Domain.Performance,
                    severity: Severity.Info,
                    title: L.Tr($"{flaggedCount} texture(s) request Stream Mip Maps but Texture Streaming is off", $"{flaggedCount} 个纹理设了 Stream Mip Maps 但 Texture Streaming 未开"),
                    detail: L.Tr($"{flaggedCount} texture importer(s) have 'Stream Mip Maps' enabled, but 'Texture Streaming' is OFF in the active Quality Settings level — " +
                                 "streaming needs both halves, so the per-texture flags currently do nothing (they cost nothing either). " +
                                 "Enable the Quality Settings switch to activate them, or clear the flags if streaming was abandoned deliberately.",
                                 $"{flaggedCount} 个纹理的导入设置开了「Stream Mip Maps」，但当前 Quality Settings 级别的「Texture Streaming」是关闭的——" +
                                 "串流需要两头都开，逐纹理的标记目前不起任何作用（也没有代价）。" +
                                 "开启 Quality Settings 开关即可激活；若是有意放弃串流，也可清掉这些标记。"),
                    targetPath: null,
                    action: new FindingAction(
                        label: L.Tr("Enable Texture Streaming (Quality Settings)", "开启 Texture Streaming（Quality Settings）"),
                        confirmMessage: EnableQualityConfirm(),
                        run: () =>
                        {
                            EnableStreamingAllQualityLevels();
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr("Texture Streaming enabled for all quality levels.", "已为所有质量级别开启 Texture Streaming。"));
                        }));
            }

            // TEXSTR001 — a big eligible pool is not streaming. Gated on the quantified estimate (see class note).
            if (candidates.Count > 0 && candidateBytes >= ThresholdBytes)
            {
                string poolHuman = ScannerUtil.Human(candidateBytes);
                bool needsQuality = !streamingActive;
                var capturedCandidates = candidates;
                // The savings ceiling is what the user can PERCEIVE in a build of what they're looking at
                // (product rule 2026-07-17: memory optimization is scene-scoped — "build it and feel it").
                // Only a scene's own loads are resident at any moment, so the ceiling = the OPEN scene(s)'
                // eligible pool − budget; the heaviest build scene is offered as guidance ("open it and
                // re-scan"), the same mental model as the scene-scope notice. A zero here also keeps the
                // finding OUT of the scene-scoped optimize plan (nothing perceivable to offer). Only when
                // there is no scene information at all does the figure degrade to the project-wide bound.
                long budgetBytes = (long)(QualitySettings.streamingMipmapsMemoryBudget * 1024f * 1024f);
                string budgetMb = $"{QualitySettings.streamingMipmapsMemoryBudget:0}";
                var openDeps = OpenSceneDependencyUnion();
                var buildScenePools = CollectBuildScenePools(pool.CandidateBytesByPath, context);
                long reclaimCeiling;
                string ceilingLocEn, ceilingLocCn;
                if (openDeps.Count == 0 && buildScenePools.Count == 0)
                {
                    reclaimCeiling = CeilingAfterBudget(candidateBytes, budgetBytes);
                    ceilingLocEn = $"project-wide bound at the current {budgetMb} MB budget (no saved open scene or build scenes to attribute it to)";
                    ceilingLocCn = $"按全项目口径与当前 {budgetMb} MB 预算估（无已保存的打开场景或 build 场景可归因）";
                }
                else
                {
                    long openScenePool = ScenePoolBytes(openDeps, pool.CandidateBytesByPath);
                    reclaimCeiling = CeilingAfterBudget(openScenePool, budgetBytes);
                    ceilingLocEn = $"the open scene(s)' eligible pool is ~{ScannerUtil.Human(openScenePool)} vs the {budgetMb} MB budget";
                    ceilingLocCn = $"打开场景的可串流池约 {ScannerUtil.Human(openScenePool)}，预算 {budgetMb} MB";
                    if (TryBestSceneCeiling(buildScenePools, budgetBytes, out string bestScene, out long bestPool, out long bestCeiling)
                        && bestCeiling > reclaimCeiling)
                    {
                        string sceneName = System.IO.Path.GetFileNameWithoutExtension(bestScene);
                        ceilingLocEn += $"; your heaviest build scene '{sceneName}' (pool ~{ScannerUtil.Human(bestPool)}) could unlock up to ~{ScannerUtil.Human(bestCeiling)} — open it and re-scan";
                        ceilingLocCn += $"；最重的 build 场景「{sceneName}」（池约 {ScannerUtil.Human(bestPool)}）可解锁至约 {ScannerUtil.Human(bestCeiling)}——打开它重扫即可";
                    }
                }

                string stateLine = needsQuality
                    ? L.Tr("Texture Streaming is OFF in Quality Settings and these textures lack the Stream Mip Maps flag — enabling means both.",
                           "Quality Settings 的 Texture Streaming 未开，这些纹理也未设 Stream Mip Maps 标记——开启需两头一起。")
                    : L.Tr("Texture Streaming is already ON in Quality Settings — these textures just lack the per-importer Stream Mip Maps flag.",
                           "Quality Settings 的 Texture Streaming 已开——只差这些纹理的 Stream Mip Maps 导入标记。");

                yield return new Finding(
                    ruleId: "PERF.TEXSTR001",
                    domain: Domain.Performance,
                    severity: Severity.Info,
                    title: L.Tr($"Mipmap Streaming could serve ~{poolHuman} of textures on demand", $"Mipmap Streaming 可让约 {poolHuman} 的纹理按需加载"),
                    groupTitle: L.Tr("Mipmap Streaming opportunity", "Mipmap Streaming 优化机会"),
                    detail: L.Tr($"{candidates.Count} mipmapped scene textures (est. ~{poolHuman} total, importer-metadata estimate) are eligible for Mipmap Streaming " +
                                 "but not participating. With streaming, Unity loads only the mip levels the current camera needs instead of every texture at full size — " +
                                 "a little CPU for a lot of texture memory (a real measured case went 280MB → 176MB with default settings). " +
                                 $"{stateLine}\n" +
                                 "Notes: savings depend on camera distance/scene layout, so treat the figure as the eligible pool, not a promise. " +
                                 $"The streaming Memory Budget caps the effect per moment, and only one scene's textures are resident at a time — the realistic savings ceiling is ~{ScannerUtil.Human(reclaimCeiling)}: {ceilingLocEn}. " +
                                 "Scenes whose pool stays under the budget see no change; lowering the budget raises the ceiling; additive multi-scene loading can exceed a single scene's pool (the estimate is conservative there). " +
                                 "Set per-camera behaviour via Streaming Controller if needed. After enabling, verify visuals and tune Memory Budget / " +
                                 "Max Level Reduction in the PerfLint Runtime Profiler's Texture Streaming section (SRP has no built-in mip debug view).",
                                 $"{candidates.Count} 个带 Mipmap 的场景纹理（合计约 {poolHuman}，按导入元数据估算）符合 Mipmap Streaming 条件但未参与。" +
                                 "开启串流后 Unity 只加载当前相机需要的 Mip 级别、不再整张全尺寸加载——用少量 CPU 换大量纹理内存（实测案例默认参数 280MB → 176MB）。" +
                                 $"{stateLine}\n" +
                                 "注意：实际节省取决于相机距离与场景布局，此数字是「可参与的池子」而非承诺值。" +
                                 $"串流 Memory Budget 决定单一时刻的效果上限，且同一时刻只有一个场景的纹理驻留——现实可省上限约 {ScannerUtil.Human(reclaimCeiling)}：{ceilingLocCn}。" +
                                 "场景池低于预算的场景看不到变化；调低预算可抬高上限；additive 多场景叠加加载可能超过单场景池（此估算对其偏保守）。" +
                                 "如需按相机差异化可加 Streaming Controller。" +
                                 "开启后请检查画质，并在 PerfLint Runtime Profiler 的 Texture Streaming 区调 Memory Budget / Max Level Reduction 验证（SRP 没有内置的 Mip 调试视图）。"),
                    targetPath: null,
                    group: candidates.Count > 1 ? candidates : null,
                    action: new FindingAction(
                        label: needsQuality
                            ? L.Tr("Enable Mipmap Streaming (settings + textures)", "开启 Mipmap Streaming（设置 + 纹理）")
                            : L.Tr($"Enable Stream Mip Maps on {candidates.Count} textures", $"为 {candidates.Count} 个纹理开启 Stream Mip Maps"),
                        confirmMessage: (needsQuality ? EnableQualityConfirm() + "\n" : "") +
                            L.Tr($"Enable 'Stream Mip Maps' on {capturedCandidates.Count} texture importer(s) and reimport them — this can take a while on large projects.\n" +
                                 "Importer changes are per-file and revertible by re-editing; the Quality Settings change modifies QualitySettings.asset and cannot be " +
                                 "reverted with Edit > Undo. Commit to version control first.",
                                 $"为 {capturedCandidates.Count} 个纹理导入器开启「Stream Mip Maps」并重新导入——大项目可能需要一段时间。\n" +
                                 "导入设置逐文件可再改回；Quality Settings 修改写入 QualitySettings.asset，无法用 Edit > Undo 撤销。建议先提交版本控制。"),
                        run: () =>
                        {
                            if (needsQuality) EnableStreamingAllQualityLevels();
                            int changed = 0;
                            try
                            {
                                AssetDatabase.StartAssetEditing();
                                foreach (var p in capturedCandidates)
                                {
                                    if (AssetImporter.GetAtPath(p) is not TextureImporter ti || ti.streamingMipmaps) continue;
                                    ti.streamingMipmaps = true;
                                    EditorUtility.SetDirty(ti);
                                    ti.SaveAndReimport();
                                    changed++;
                                }
                            }
                            finally
                            {
                                AssetDatabase.StopAssetEditing();
                            }
                            AssetDatabase.SaveAssets();
                            return FixResult.Ok(L.Tr($"Stream Mip Maps enabled on {changed} texture(s)" + (needsQuality ? "; Texture Streaming enabled for all quality levels." : "."),
                                                      $"已为 {changed} 个纹理开启 Stream Mip Maps" + (needsQuality ? "；并为所有质量级别开启 Texture Streaming。" : "。")));
                        }),
                    // Budget-aware ceiling, NOT the raw pool (see reclaimCeiling above), and NOT a promise either —
                    // still camera/scene-dependent, so the ceiling flag keeps it OUT of the verified "optimized ~X
                    // for you" tally. Zero when the pool fits the current budget (the finding still fires: the
                    // eligibility story holds, there's just nothing to promise at this budget).
                    estimatedMemorySavingsBytes: reclaimCeiling,
                    savingsAreCeiling: true);
            }
        }

        private static string EnableQualityConfirm()
            => L.Tr("Enable 'Texture Streaming' in Quality Settings for ALL quality levels.\n" +
                    "This modifies project settings (QualitySettings.asset) and cannot be reverted with Edit > Undo; commit to version control first.",
                    "为 Quality Settings 的所有质量级别开启「Texture Streaming」。\n" +
                    "此操作修改项目设置（QualitySettings.asset），无法用 Edit > Undo 撤销；建议先提交版本控制。");

        /// <summary>
        /// streamingMipmapsActive only reads/writes the ACTIVE quality level, so walk every level and restore the
        /// original selection afterwards — otherwise the setting silently stays off on the levels devices actually use.
        /// </summary>
        internal static void EnableStreamingAllQualityLevels()
        {
            int current = QualitySettings.GetQualityLevel();
            try
            {
                int levels = QualitySettings.names.Length;
                for (int i = 0; i < levels; i++)
                {
                    QualitySettings.SetQualityLevel(i, applyExpensiveChanges: false);
                    QualitySettings.streamingMipmapsActive = true;
                }
            }
            finally
            {
                QualitySettings.SetQualityLevel(current, applyExpensiveChanges: false);
            }
        }

        /// <summary>
        /// Savings ceiling of the streaming pool at a given Memory Budget: streaming only evicts mips once demand
        /// exceeds the budget, so the honest potential is pool − budget, never negative (a pool that fits the budget
        /// yields zero — verified by a real device A/B where a 97MB-textures scene under the 512MB default budget
        /// measured exactly no change). Lowering the budget raises the ceiling; the finding text says so.
        /// </summary>
        internal static long CeilingAfterBudget(long poolBytes, long budgetBytes)
            => poolBytes > budgetBytes ? poolBytes - budgetBytes : 0;

        /// <summary>One scene's eligible streaming pool: the candidates that scene's dependency set actually pulls in.</summary>
        internal static long ScenePoolBytes(IEnumerable<string> sceneDependencies, IReadOnlyDictionary<string, long> candidateBytesByPath)
        {
            long sum = 0;
            if (sceneDependencies == null || candidateBytesByPath == null) return 0;
            foreach (var d in sceneDependencies)
                if (!string.IsNullOrEmpty(d) && candidateBytesByPath.TryGetValue(d, out long b)) sum += b;
            return sum;
        }

        /// <summary>
        /// Picks the scene whose budget-capped ceiling is largest — for scene-based games that scene bounds what any
        /// single runtime moment can save. All-under-budget still succeeds (biggest pool, ceiling 0: an honest zero,
        /// distinct from "no scene information at all" which returns false and falls back to the project-wide bound).
        /// </summary>
        internal static bool TryBestSceneCeiling(IReadOnlyList<KeyValuePair<string, long>> scenePools, long budgetBytes,
            out string scenePath, out long poolBytes, out long ceilingBytes)
        {
            scenePath = null; poolBytes = 0; ceilingBytes = 0;
            if (scenePools == null || scenePools.Count == 0) return false;
            foreach (var sp in scenePools)
            {
                long c = CeilingAfterBudget(sp.Value, budgetBytes);
                if (scenePath == null || c > ceilingBytes || (c == ceilingBytes && sp.Value > poolBytes))
                {
                    scenePath = sp.Key;
                    poolBytes = sp.Value;
                    ceilingBytes = c;
                }
            }
            return true;
        }

        /// <summary>
        /// Union of the LOADED scene(s)' dependency paths — the assets resident in "what the user is looking at".
        /// Union (not per-scene) because additively loaded scenes are resident together; deduping via the set also
        /// prevents double-counting shared dependencies. Unsaved/untitled scenes have no path and contribute nothing.
        /// </summary>
        private static HashSet<string> OpenSceneDependencyUnion()
        {
            var set = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || string.IsNullOrEmpty(sc.path)) continue;
                foreach (var d in AssetDatabase.GetDependencies(sc.path, true)) set.Add(d);
            }
            return set;
        }

        /// <summary>
        /// Per-scene eligible pools for the enabled Build Settings scenes (path-level GetDependencies — no scene
        /// opening). Feeds the "your heaviest scene could unlock ~X" guidance. Editor glue kept thin — the sum and
        /// the pick are the pure, unit-tested parts above.
        /// </summary>
        private List<KeyValuePair<string, long>> CollectBuildScenePools(IReadOnlyDictionary<string, long> candidateBytesByPath, ScanContext context)
        {
            var scenePaths = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled && !string.IsNullOrEmpty(s.path)) scenePaths.Add(s.path);

            var result = new List<KeyValuePair<string, long>>();
            foreach (var sp in scenePaths.Distinct())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                result.Add(new KeyValuePair<string, long>(sp,
                    ScenePoolBytes(AssetDatabase.GetDependencies(sp, true), candidateBytesByPath)));
            }
            return result;
        }

        /// <summary>
        /// Rough imported-size estimate from metadata only (no texture load): pixels × bits-per-pixel ÷ 8 × 1.33 (mip
        /// chain; pass mips:false for textures without one). Compressed formats vary 4–8bpp by family/platform — 6bpp
        /// is the deliberate middle; uncompressed is 32bpp. Good enough to gate a 64MB threshold and label a pool
        /// "~X MB". Shared bpp convention for every texture-memory savings estimate (TEX001/002/004/005 reuse it).
        /// </summary>
        internal static long EstimateBytes(int w, int h, bool uncompressed, bool mips = true)
        {
            double bpp = uncompressed ? 32.0 : 6.0;
            return (long)((double)w * h * bpp / 8.0 * (mips ? 1.33 : 1.0));
        }
    }
}
