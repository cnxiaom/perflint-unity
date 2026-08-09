#if PERFLINT_ADDRESSABLES
using System;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor.AddressableAssets.Settings;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Temporarily sets <c>AddressableAssetSettings.IgnoreUnsupportedFilesInBuild</c> for the duration of an official
    /// Analyze rule run, restoring the user's value afterwards.
    ///
    /// **Why**: every analyze-backed rule (AADUP001, AARES001) goes through
    /// <c>BuildScriptPackedMode.PrepGroupBundlePacking → GenerateBuildInputDefinitions →
    /// ThrowExceptionIfInvalidFiletypeOrAddress</c>, which THROWS on the first group entry whose asset has no
    /// recognizable main type (<c>AssetDatabase.GetMainAssetTypeAtPath</c> returns null → Addressables treats it as
    /// <c>DefaultAsset</c>). One such asset anywhere in the project aborts the whole analysis, so the rule produced
    /// zero findings and simply vanished from the panel — with nothing but a console warning to explain it.
    /// Observed for real: a single unimportable .png in one group silently removed both AADUP001 and AARES001 from a
    /// project that had been reporting them for months.
    /// With the flag on, Addressables logs a warning for that one entry and keeps going, so the other N thousand
    /// entries still get analyzed. Diagnosing the offending entry itself is <see cref="AddressableEntryScanner"/>'s
    /// job (ASSET.AAENTRY001) — this guard only stops one bad asset from taking the rest of the report down with it.
    ///
    /// The setter is a plain field assignment (no <c>SetDirty</c> / modification event), so the settings asset is not
    /// dirtied and Unity will not serialize the temporary value. Restore happens in <see cref="Dispose"/>, i.e. also
    /// on exception. When the value is already true, nothing is written at all.
    ///
    /// Requires Addressables ≥ 1.17.2 (when <c>IgnoreUnsupportedFilesInBuild</c> became public API); on 1.16.x the
    /// guard compiles to a no-op and the old abort-on-first-bad-asset behaviour stands — the rules then surface
    /// ASSET.AABLOCK001 instead of disappearing.
    /// </summary>
    internal readonly struct AddressableAnalyzeGuard : IDisposable
    {
#if PERFLINT_AA_IGNORE_UNSUPPORTED
        private readonly AddressableAssetSettings _settings;
        private readonly bool _restoreToFalse;

        public AddressableAnalyzeGuard(AddressableAssetSettings settings)
        {
            _settings = settings;
            _restoreToFalse = settings != null && !settings.IgnoreUnsupportedFilesInBuild;
            if (_restoreToFalse) settings.IgnoreUnsupportedFilesInBuild = true;
        }

        public void Dispose()
        {
            if (_restoreToFalse && _settings != null) _settings.IgnoreUnsupportedFilesInBuild = false;
        }
#else
        public AddressableAnalyzeGuard(AddressableAssetSettings settings) { }
        public void Dispose() { }
#endif
    }

    /// <summary>
    /// Turns "the official Analyze rule threw, so this rule has nothing to say" into a visible finding.
    ///
    /// A rule that produces zero findings is indistinguishable from a clean project in the panel — the row simply is
    /// not there. That is exactly how an aborted analysis stayed hidden: the only trace was one console warning, and
    /// the panel looked like the duplicates had been fixed. A silent blind spot reads as good news, so it must not be
    /// silent.
    /// </summary>
    internal static class AddressableAnalyzeFailure
    {
        public static Finding Describe(string ruleId, string ruleLabel, string message)
        {
            return new Finding(
                ruleId: "ASSET.AABLOCK001",
                domain: Domain.Assets,
                severity: Severity.Warning,
                title: L.Tr($"{ruleLabel} ({ruleId}) could not run this scan", $"{ruleLabel}（{ruleId}）本次扫描没能跑起来"),
                groupTitle: L.Tr("Addressables analysis could not run — results are incomplete", "Addressables 分析未能完成——结果不完整"),
                detail: L.Tr($"The official Addressables analysis backing {ruleId} threw, so that rule reported nothing this scan. " +
                             "An empty rule looks exactly like a clean project, so treat this as a blind spot rather than a pass.",
                             $"{ruleId} 依赖的官方 Addressables 分析抛了异常，所以这条规则本次什么都没报。" +
                             "规则为空和「项目干净」在面板上长得一模一样，所以这是一处盲区，不是通过。") +
                        L.Tr($"\nReported error: {message}", $"\n报错内容：{message}") +
                        L.Tr("\nThe usual cause is a group entry whose asset the editor cannot import, or an address containing square brackets — the same conditions that make a packed Addressables build fail. Fix those, then rescan.",
                             "\n最常见的原因是某个 group 条目的资产编辑器导入不了，或者 address 里含方括号——正是让 Addressables 打包失败的那两种情况。修掉后重新扫描。"),
                targetPath: null,
                ignoreExempt: true);
        }
    }
}
#endif
