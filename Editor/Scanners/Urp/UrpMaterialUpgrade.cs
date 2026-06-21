#if PERFLINT_URP
using System;
using PerfLint.Core;
using PerfLint.L10n;
using UnityEditor;
using UnityEditor.Rendering;            // MaterialUpgrader
using UnityEditor.Rendering.Universal;  // StandardUpgrader
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>
    /// MAT001 executable fix (URP-only): upgrade Built-in materials to URP/Lit using Unity's **official upgrader**,
    /// reusing URP's own Built-in → URP property mapping (_MainTex→_BaseMap, metallic/specular workflow detection, etc.) instead of guessing a 1:1 mapping ourselves.
    ///
    /// Compiled only when the URP package is installed (<c>PERFLINT_URP</c>, injected via versionDefines); registers the
    /// factory delegate with the main module via <see cref="MaterialUpgradeBridge"/>, so the asmdef dependency direction stays intact.
    ///
    /// **Coverage (deliberately narrowed, tracked in progress-ledger)**: uses only URP's **public** upgrader classes, with an exact per-shader mapping:
    ///   - "Standard" / "Standard (Specular setup)"  → <see cref="StandardUpgrader"/>
    ///   - "Autodesk Interactive"                     → <see cref="AutodeskInteractiveUpgrader"/>
    ///   - "Nature/Terrain/Standard"                  → <see cref="TerrainUpgrader"/>
    /// Everything else (especially **Legacy Shaders/\***, Mobile/UI/Sprites families) returns null and stays manual—URP's upgrader for them,
    /// <c>StandardSimpleLightingUpgrader</c>, is **internal** (and its constructor further requires an internal UpgradeParams), so the child asmdef cannot construct it;
    /// covering them would require reflecting into URP's internal GetUpgraders, which is fragile and untestable—deferred as a separate decision (see ledger).
    ///
    /// This modifies the .mat asset (swaps shader + remaps properties). The confirmation dialog truthfully advises "commit to version control first".
    /// </summary>
    [InitializeOnLoad]
    internal static class UrpMaterialUpgrade
    {
        static UrpMaterialUpgrade()
        {
            MaterialUpgradeBridge.CreateUpgradeAction = CreateAction;
        }

        /// <summary>
        /// Given a Built-in shader name, returns the corresponding URP **public** official upgrader; null if there is no match (e.g. Legacy Shaders/*).
        /// This is the **single source of truth** for both the "upgradable" check and the actual upgrade—CreateAction relies on its non-null result to decide whether to attach a button, and Upgrade uses it to execute.
        /// </summary>
        private static MaterialUpgrader SelectUpgrader(string shaderName)
        {
            switch (shaderName)
            {
                case "Standard":
                case "Standard (Specular setup)":
                    return new StandardUpgrader(shaderName);
                case "Autodesk Interactive":
                    return new AutodeskInteractiveUpgrader(shaderName);
                case "Nature/Terrain/Standard":
                    return new TerrainUpgrader(shaderName);
                default:
                    return null; // Legacy Shaders/*, Mobile/UI/Sprites, etc.: URP upgrader is internal, stays manual
            }
        }

        /// <summary>Factory: returns an Action when a matching public upgrader exists, otherwise null (the finding gets no button and stays manual).</summary>
        private static FindingAction CreateAction(string assetPath, string shaderName)
        {
            if (SelectUpgrader(shaderName) == null) return null;

            return new FindingAction(
                label: L.Tr("Upgrade to URP", "升级到 URP"),
                confirmMessage:
                    L.Tr($"Run Unity's official Built-in → URP material upgrader on:\n{assetPath}\n\n", $"用 Unity 官方 Built-in → URP 材质升级器升级：\n{assetPath}\n\n") +
                    L.Tr($"Switches the shader from \"{shaderName}\" to its URP equivalent and remaps textures/properties (e.g. _MainTex → _BaseMap) using URP's built-in mapping.\n", $"把 shader 从「{shaderName}」换为对应的 URP shader，并用 URP 内置映射重映射贴图/参数（如 _MainTex → _BaseMap）。\n") +
                    L.Tr("This modifies the material asset. Custom or non-standard properties may not transfer 1:1—review the result. Commit to version control first.", "此操作修改材质资产。自定义/非标准属性可能无法 1:1 迁移，请复核结果。建议先提交版本控制。"),
                run: () => Upgrade(assetPath));
        }

        private static FixResult Upgrade(string assetPath)
        {
            try
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (mat == null) return FixResult.Fail(L.Tr($"Material not found: {assetPath}", $"找不到材质：{assetPath}"));

                // The upgrader is selected by the "current shader name" (which drives the property mapping). Re-select at run time to avoid a mismatch if the material was changed after the scan.
                string current = mat.shader != null ? mat.shader.name : null;
                var upgrader = SelectUpgrader(current);
                if (upgrader == null)
                    return FixResult.Fail(L.Tr($"Shader changed and is no longer upgradable: {current}", $"shader 已变更、不再可升级：{current}"));

                // LogErrorOnNonExistingProperty off: Built-in materials often carry properties the upgrader doesn't declare; enabling it would spew a pile of harmless errors.
                MaterialUpgrader.Upgrade(mat, upgrader, MaterialUpgrader.UpgradeFlags.None);

                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssetIfDirty(mat); // The single-item execution path doesn't call SaveAssets externally, so save here (the batch path will save again, which is harmless)
                return FixResult.Ok(L.Tr($"Upgraded to URP: {assetPath}", $"已升级到 URP：{assetPath}"));
            }
            catch (Exception e)
            {
                return FixResult.Fail(L.Tr($"Upgrade failed: {e.Message}", $"升级失败：{e.Message}"));
            }
        }
    }
}
#endif
