using System;

namespace PerfLint.Core
{
    /// <summary>
    /// The "material upgrade" bridge between the main module and the optional URP module. MAT001 findings
    /// are produced in the main asmdef's MaterialScanner, but upgrading materials from Built-in → URP
    /// requires calling the official upgrader (<c>MaterialUpgrader</c> / <c>StandardUpgrader</c>) from
    /// the URP package (com.unity.render-pipelines.universal) — the main module cannot reference optional packages.
    ///
    /// The solution follows the same pattern as <see cref="FindingAction"/>: the upgrade logic is compiled
    /// in a sub-asmdef that holds the URP dependency (PerfLint.Editor.Urp, gated by <c>PERFLINT_URP</c>),
    /// and registers a factory delegate here via <see cref="InitializeOnLoad"/>; MaterialScanner retrieves
    /// an Action on demand when producing a finding.
    /// If the URP package is absent, the entire sub-asmdef is not compiled, the delegate remains null → no
    /// Action is attached (falls back to suggestion-only).
    /// </summary>
    public static class MaterialUpgradeBridge
    {
        /// <summary>
        /// Factory: given (material asset path, current shader name), returns a <see cref="FindingAction"/>
        /// that upgrades the material to URP; returns null when the shader is outside the reliable coverage
        /// of the official upgrader (keeps it manual, no button attached).
        /// Injected by the URP sub-module at domain load time; null when the URP package is not installed.
        /// </summary>
        public static Func<string, string, FindingAction> CreateUpgradeAction;
    }
}
