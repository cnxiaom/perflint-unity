using System;

namespace PerfLint.Capture
{
    /// <summary>
    /// Parses the shader-compilation lines a development player logs when Graphics Settings → "Log Shader Compilation"
    /// (<c>GraphicsSettings.logWhenShaderIsCompiled</c>) is on. Ground truth from a real 2022.3 Windows/D3D11 player
    /// (2026-07-07 probe — see dev-changelog):
    ///
    ///   Uploaded shader variant to the GPU driver: Standard (instance 0x118), pass: ShadowCaster, keywords SHADOWS_DEPTH, time: 0.2 ms
    ///   Uploaded shader variant to the GPU driver: Hidden/BlitCopy (instance 0x2E), pass: &lt;Unnamed Pass 0&gt;, keywords &lt;no keywords&gt;, time: 1 ms
    ///
    /// and the older/editor-side wording:
    ///
    ///   Compiled shader: Universal Render Pipeline/Lit, pass: ForwardLit, stage: vertex, keywords _MAIN_LIGHT_SHADOWS _SHADOWS_SOFT
    ///
    /// Format-drift hardening: the literal anchors (", pass: ", ", stage: ", ", keywords") have been stable across
    /// versions, but the keyword separator gained/lost a trailing colon, the stage field comes and goes, shader names
    /// carry an " (instance 0x…)" suffix in the player wording, and a ", time: X ms" tail follows the keywords there —
    /// all tolerated. Fields are located right-to-left off the anchors so a comma — or even a literal ", pass: " —
    /// inside a shader name cannot shift them. This runs on every log line the player prints, so the miss path must be
    /// allocation-free (prefix check first) and it must never throw.
    /// </summary>
    internal static class ShaderCompileLogParser
    {
        private const string PrefixCompiled = "Compiled shader: ";
        private const string PrefixUploaded = "Uploaded shader variant to the GPU driver: ";
        private const string PassAnchor = ", pass: ";
        private const string StageAnchor = ", stage: ";
        private const string KeywordAnchor = ", keywords";
        private const string TimeAnchor = ", time:";
        private const string InstanceAnchor = " (instance ";
        private const string NoKeywords = "<no keywords>";

        /// <summary>
        /// True when the line is a shader-compilation message. <paramref name="keywords"/> comes back in log order
        /// (callers normalize via <see cref="VariantRecord.Create"/>), empty (never null) for keyword-less variants.
        /// </summary>
        public static bool TryParse(string line, out string shader, out string pass, out string[] keywords)
        {
            shader = null;
            pass = null;
            keywords = null;
            if (string.IsNullOrEmpty(line)) return false;

            int bodyStart;
            if (line.StartsWith(PrefixCompiled, StringComparison.Ordinal)) bodyStart = PrefixCompiled.Length;
            else if (line.StartsWith(PrefixUploaded, StringComparison.Ordinal)) bodyStart = PrefixUploaded.Length;
            else return false;

            // Right-to-left: the LAST occurrence of each anchor is the real field separator, whatever the names contain.
            int kwAt = line.LastIndexOf(KeywordAnchor, StringComparison.Ordinal);
            if (kwAt < bodyStart) return false;
            int stageAt = line.LastIndexOf(StageAnchor, kwAt, StringComparison.Ordinal);
            int passEnd = stageAt >= bodyStart ? stageAt : kwAt;
            int passAt = line.LastIndexOf(PassAnchor, passEnd, StringComparison.Ordinal);
            if (passAt < bodyStart) return false;

            shader = line.Substring(bodyStart, passAt - bodyStart).Trim();
            // Player wording appends " (instance 0x118)" to the shader name — strip it back to the findable name.
            int inst = shader.LastIndexOf(InstanceAnchor, StringComparison.Ordinal);
            if (inst > 0 && shader.EndsWith(")", StringComparison.Ordinal))
                shader = shader.Substring(0, inst).TrimEnd();

            pass = line.Substring(passAt + PassAnchor.Length, passEnd - passAt - PassAnchor.Length).Trim();

            string kws = line.Substring(kwAt + KeywordAnchor.Length);
            // Player wording appends ", time: 0.2 ms" after the keywords — cut it. Keywords can't contain a comma,
            // so the first time-anchor inside the keyword section is always the real tail.
            int timeAt = kws.IndexOf(TimeAnchor, StringComparison.Ordinal);
            if (timeAt >= 0) kws = kws.Substring(0, timeAt);
            kws = kws.TrimStart(':').Trim();

            keywords = kws.Length == 0 || kws.StartsWith(NoKeywords, StringComparison.Ordinal)
                ? Array.Empty<string>()
                : kws.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); // null = any whitespace

            return shader.Length > 0;
        }
    }
}
