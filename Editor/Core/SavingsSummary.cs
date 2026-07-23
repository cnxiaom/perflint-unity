using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// Aggregates per-finding savings estimates (<see cref="Finding.EstimatedMemorySavingsBytes"/> /
    /// <see cref="Finding.EstimatedBuildSavingsBytes"/>) into the totals behind the panel's
    /// "estimated optimization effect" line.
    ///
    /// Honesty contract: every input is already an estimate/ceiling (bpp conventions, streaming pool, source-file
    /// bytes before archive compression), so the totals MUST be presented as "up to ~X (est.)" — never a promise.
    /// Known residual overlap on the memory side: the TEXSTR001 streaming pool and per-texture TEX rules can both
    /// count the same texture (different mechanisms that only partially stack); acceptable inside a ceiling figure,
    /// not worth a cross-rule reconciliation pass.
    /// </summary>
    public static class SavingsSummary
    {
        public readonly struct Totals
        {
            public readonly long MemoryBytes;
            public readonly long BuildBytes;
            /// <summary>The ceiling-semantics portion of MemoryBytes (e.g. the Mipmap Streaming pool). The verified
            /// "optimized ~X for you" tally must use <see cref="FirmMemoryBytes"/>, never the ceiling part.</summary>
            public readonly long MemoryCeilingBytes;

            public long FirmMemoryBytes => MemoryBytes - MemoryCeilingBytes;

            public Totals(long memoryBytes, long buildBytes, long memoryCeilingBytes = 0)
            {
                MemoryBytes = memoryBytes;
                BuildBytes = buildBytes;
                MemoryCeilingBytes = memoryCeilingBytes;
            }

            public bool HasAny => MemoryBytes > 0 || BuildBytes > 0;
        }

        public static Totals Compute(IReadOnlyList<Finding> findings)
        {
            if (findings == null) return new Totals(0, 0);

            long mem = 0, memCeiling = 0;
            // Build savings are deduplicated by target path: the same asset can be flagged by more than one
            // duplication rule (byte-identical copies that are ALSO packed into multiple bundles) and summing
            // would double-charge it — keep the largest single claim per path. Pathless findings can't collide,
            // so they sum directly.
            Dictionary<string, long> buildByPath = null;
            long build = 0;
            foreach (var f in findings)
            {
                if (f == null) continue;
                mem += f.EstimatedMemorySavingsBytes;
                if (f.SavingsAreCeiling) memCeiling += f.EstimatedMemorySavingsBytes;

                long b = f.EstimatedBuildSavingsBytes;
                if (b <= 0) continue;
                if (string.IsNullOrEmpty(f.TargetPath))
                {
                    build += b;
                }
                else
                {
                    buildByPath ??= new Dictionary<string, long>();
                    if (!buildByPath.TryGetValue(f.TargetPath, out long prev) || b > prev)
                        buildByPath[f.TargetPath] = b;
                }
            }
            if (buildByPath != null)
                foreach (var kv in buildByPath)
                    build += kv.Value;

            return new Totals(mem, build, memCeiling);
        }

        /// <summary>
        /// The FIRM memory savings relevant to the currently open scene(s): findings whose target asset is in the
        /// scenes' dependency set, plus pathless firm findings that are scene-derived by construction (SBATCH001's
        /// combined-mesh bill is computed FROM the loaded scenes). Ceiling findings are excluded — this figure exists
        /// precisely so a same-scene Memory Profiler A/B has a number it can actually validate; a camera-dependent
        /// pool would wreck that. Dependency membership is injected so the logic stays unit-testable.
        /// </summary>
        public static long ComputeSceneScopedMemory(IReadOnlyList<Finding> findings, ISet<string> sceneDependencyPaths)
        {
            if (findings == null || sceneDependencyPaths == null) return 0;
            long total = 0;
            foreach (var f in findings)
            {
                if (f == null || f.SavingsAreCeiling) continue;
                long s = f.EstimatedMemorySavingsBytes;
                if (s <= 0) continue;
                if (string.IsNullOrEmpty(f.TargetPath) || sceneDependencyPaths.Contains(f.TargetPath))
                    total += s;
            }
            return total;
        }
    }
}
