using UnityEditor;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Per-project settings for the build-time shader-variant stripper (B layer). Persisted under ProjectSettings/ so it
    /// survives domain reloads and is readable from the build callback. OFF by default — the stripper does nothing until
    /// the user picks a captured SVC and explicitly enables it.
    /// </summary>
    [FilePath("ProjectSettings/PerfLintShaderStrip.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class ShaderStripSettings : ScriptableSingleton<ShaderStripSettings>
    {
        /// <summary>Whether build-time stripping is active.</summary>
        public bool enabled;

        /// <summary>Project-relative path to the .shadervariants asset to keep (the recorded set).</summary>
        public string svcPath = "";

        /// <summary>Conservative (false): only prune shaders present in the SVC. Aggressive (true): prune every shader not fully covered — bigger savings, higher risk.</summary>
        public bool aggressive;

        /// <summary>Diagnostic: log every stripped variant (shader / pass / keywords) so it can be cross-referenced against Strict-Matching's "missing variant" errors to find what broke a build.</summary>
        public bool logStripped;

        public void SaveNow() => Save(true);
    }
}
