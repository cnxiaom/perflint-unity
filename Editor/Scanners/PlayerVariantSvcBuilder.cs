using System;
using System.Collections.Generic;
using PerfLint.Capture;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerfLint.Scanners
{
    /// <summary>
    /// Turns device-captured variant records into ShaderVariantCollection entries.
    ///
    /// The impedance mismatch this solves: players log the pass NAME ("ForwardLit", "FORWARD", "ShadowCaster"…), but a
    /// ShaderVariantCollection entry needs a <see cref="PassType"/>. The mapping isn't 1:1, so resolution works in two
    /// steps per record: guess from the pass name (built-in LightModes map by name; anything else is almost certainly
    /// an SRP pass), then lean on the fact that the ShaderVariant CONSTRUCTOR VALIDATES (shader, passType, keywords)
    /// and throws when the shader has no such variant — brute-forcing the remaining PassTypes is therefore
    /// deterministic and safe: the first constructor that accepts is a variant the shader really has. Records no
    /// PassType accepts are counted as unresolved, never guessed into the collection. A resolved (shader, pass name)
    /// pair is cached and tried first for subsequent records of the same pass.
    ///
    /// Everything is additive and validated, so a malformed record can't poison the collection — and the collection's
    /// consumers stay safe by construction: warm-up simply skips variants the current platform doesn't use.
    /// </summary>
    internal static class PlayerVariantSvcBuilder
    {
        internal sealed class Result
        {
            public int TotalRecords;
            public int Added;
            public int AlreadyPresent;
            /// <summary>Shader was found, but no PassType accepted the (pass, keywords) combination.</summary>
            public int UnresolvedVariants;
            /// <summary>Shader.Find failed — bundle-only shaders, renamed, or not in this project.</summary>
            public readonly List<string> MissingShaders = new List<string>();
        }

        // Built-in pipeline LightMode names → PassType. Everything not listed here is tried as an SRP pass first.
        private static readonly Dictionary<string, PassType> NameGuess = new Dictionary<string, PassType>(StringComparer.OrdinalIgnoreCase)
        {
            { "forwardbase", PassType.ForwardBase },
            { "forward", PassType.ForwardBase },
            { "forwardadd", PassType.ForwardAdd },
            { "forward_delta", PassType.ForwardAdd },
            { "shadowcaster", PassType.ShadowCaster },
            { "deferred", PassType.Deferred },
            { "meta", PassType.Meta },
            { "motionvectors", PassType.MotionVectors },
            { "vertex", PassType.Vertex },
            { "vertexlm", PassType.VertexLM },
            { "prepassbase", PassType.LightPrePassBase },
            { "prepassfinal", PassType.LightPrePassFinal },
        };

        public static Result Build(IEnumerable<VariantRecord> records, ShaderVariantCollection into)
        {
            var result = new Result();
            if (records == null || into == null) return result;

            var missing = new HashSet<string>();
            var shaderCache = new Dictionary<string, Shader>();
            var passCache = new Dictionary<string, PassType>(); // "shaderName\npassName" → resolved PassType

            foreach (var r in records)
            {
                if (r == null || string.IsNullOrEmpty(r.shader)) continue;
                result.TotalRecords++;

                if (!shaderCache.TryGetValue(r.shader, out var shader))
                    shaderCache[r.shader] = shader = Shader.Find(r.shader);
                if (shader == null)
                {
                    if (missing.Add(r.shader)) result.MissingShaders.Add(r.shader);
                    continue;
                }

                var keywords = r.KeywordArray();
                string cacheKey = r.shader + "\n" + r.pass;
                passCache.TryGetValue(cacheKey, out var cached);
                bool hasCached = passCache.ContainsKey(cacheKey);

                bool resolved = false;
                foreach (var passType in Candidates(r.pass, hasCached, cached))
                {
                    try
                    {
                        var variant = new ShaderVariantCollection.ShaderVariant(shader, passType, keywords);
                        if (into.Add(variant)) result.Added++;
                        else result.AlreadyPresent++;
                        passCache[cacheKey] = passType;
                        resolved = true;
                        break;
                    }
                    catch
                    {
                        // ctor rejected the combination for this PassType — try the next candidate
                    }
                }
                if (!resolved) result.UnresolvedVariants++;
            }
            return result;
        }

        /// <summary>Candidate PassTypes in the order worth trying; each value yielded once.</summary>
        internal static IEnumerable<PassType> Candidates(string passName, bool hasCached, PassType cached)
        {
            var tried = new HashSet<PassType>();

            if (hasCached && tried.Add(cached)) yield return cached;

            if (!string.IsNullOrEmpty(passName) && NameGuess.TryGetValue(passName, out var guess) && tried.Add(guess))
                yield return guess;

            // Unrecognized names are almost always SRP passes (URP/HDRP LightModes); unnamed single-pass shaders are Normal.
            if (tried.Add(PassType.ScriptableRenderPipeline)) yield return PassType.ScriptableRenderPipeline;
            if (tried.Add(PassType.ScriptableRenderPipelineDefaultUnlit)) yield return PassType.ScriptableRenderPipelineDefaultUnlit;
            if (tried.Add(PassType.Normal)) yield return PassType.Normal;

            foreach (PassType p in Enum.GetValues(typeof(PassType)))
                if (tried.Add(p))
                    yield return p;
        }
    }
}
