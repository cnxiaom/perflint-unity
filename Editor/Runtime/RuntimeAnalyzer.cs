using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;
using PerfLint.Scanners;

namespace PerfLint.Runtime
{
    /// <summary>
    /// Converts a single runtime sampling result into deterministic RUN.* findings (zero tokens, following the rule-engine philosophy).
    /// Thresholds are intentionally conservative and wording is measured — runtime data is heavily dependent on target platform/scene;
    /// it is better to under-report than to false-positive.
    ///
    /// Core differentiator: "runtime confirms the problem → pinpoints to a specific script/method → guides to static line-level analysis + AI Fix".
    /// Runtime answers "where is it slow"; static Roslyn + AI Fix answers "which line to change".
    /// </summary>
    public static class RuntimeAnalyzer
    {
        public static List<Finding> Analyze(RuntimeProfileResult r)
        {
            var findings = new List<Finding>();
            if (r == null) return findings;

            FrameRate(r, findings);
            Stutter(r, findings);
            GcPerFrame(r, findings);
            MemoryTrend(r, findings);
            DrawCallsAndSetPass(r, findings);
            GpuBound(r, findings);
            TriangleDensity(r, findings);
            BatchingEfficiency(r, findings);
            MaterialInstancing(r, findings);
            Hotspots(r, findings);

            return findings;
        }

        private static void FrameRate(RuntimeProfileResult r, List<Finding> findings)
        {
            var ft = r.FrameTimeNs;
            if (ft == null || !ft.HasData) return;

            double avgMs = ft.Avg / 1_000_000.0;
            double fps = r.AverageFps;

            if (avgMs > 33.3) // < 30 FPS
            {
                findings.Add(new Finding(
                    ruleId: "RUN.FPS001",
                    domain: Domain.Runtime,
                    severity: Severity.Critical,
                    title: L.Tr($"Low runtime frame rate: avg {fps:0} FPS (main thread {avgMs:0.0} ms/frame)", $"运行时帧率偏低：平均 {fps:0} FPS（主线程 {avgMs:0.0} ms/帧）"),
                    detail: L.Tr($"Average main-thread frame time during sampling was {avgMs:0.0} ms (~{fps:0} FPS), below 30 FPS. ", $"采样期间主线程平均帧时间 {avgMs:0.0} ms（约 {fps:0} FPS），低于 30 FPS。") +
                            L.Tr("The main thread is the bottleneck. Expand the \"CPU Hotspots\" below to find the most expensive methods, then optimize line by line with the script GC analysis in the main panel. ", "主线程是瓶颈所在域。展开下方「CPU 热点」定位最耗时的方法，并结合主面板的脚本 GC 分析逐行优化。") +
                            L.Tr("(Your frame-rate target depends on your platform; mobile typically aims for 30/60 FPS.)", "（帧率目标取决于你的目标平台，移动端通常以 30/60 FPS 为线。）")));
            }
            else if (avgMs > 22.2) // < 45 FPS
            {
                findings.Add(new Finding(
                    ruleId: "RUN.FPS001",
                    domain: Domain.Runtime,
                    severity: Severity.Warning,
                    title: L.Tr($"Moderate runtime frame rate: avg {fps:0} FPS (main thread {avgMs:0.0} ms/frame)", $"运行时帧率中等：平均 {fps:0} FPS（主线程 {avgMs:0.0} ms/帧）"),
                    detail: L.Tr($"Average main-thread frame time during sampling was {avgMs:0.0} ms (~{fps:0} FPS). If you target 60 FPS and need more headroom, ", $"采样期间主线程平均帧时间 {avgMs:0.0} ms（约 {fps:0} FPS）。若目标 60 FPS 仍有余量需优化，") +
                            L.Tr("expand the \"CPU Hotspots\" below to find the main cost centers.", "可展开下方「CPU 热点」定位主要耗时点。")));
            }
        }

        private static void Stutter(RuntimeProfileResult r, List<Finding> findings)
        {
            var ft = r.FrameTimeNs;
            if (ft == null || ft.SampleCount < 30) return;

            double avgMs = ft.Avg / 1_000_000.0;
            double p95Ms = ft.P95 / 1_000_000.0;
            double maxMs = ft.Max / 1_000_000.0;

            // Stutter signal: p95 is notably above the average (long-tail spike), and p95 itself is slow enough. Distinct from the "sustained low frame rate" problem.
            if (p95Ms > 33.3 && p95Ms >= avgMs * 1.8)
            {
                findings.Add(new Finding(
                    ruleId: "RUN.FPS002",
                    domain: Domain.Runtime,
                    severity: Severity.Warning,
                    title: L.Tr($"Runtime stutter spikes: p95 {p95Ms:0.0} ms (avg {avgMs:0.0} ms, peak {maxMs:0.0} ms)", $"运行时卡顿尖刺：p95 {p95Ms:0.0} ms（均值 {avgMs:0.0} ms，峰值 {maxMs:0.0} ms）"),
                    detail: L.Tr($"The 95th-percentile frame time {p95Ms:0.0} ms is well above the average {avgMs:0.0} ms, indicating recurring stutter (hitches). ", $"95% 分位帧时间 {p95Ms:0.0} ms 明显高于平均 {avgMs:0.0} ms，说明存在周期性卡顿（hitch）。") +
                            L.Tr("Common causes: per-frame/bursty GC collections, synchronous asset loading, instantiation spikes. First check below for a \"per-frame GC allocation\" warning.", "常见成因：每帧/突发 GC 回收、资源同步加载、实例化峰值。先看下方是否有「每帧 GC 分配」告警。")));
            }

            // Catastrophic single-frame spike: max far exceeds p95 (an isolated frame, not a long tail) and the absolute value has reached the level of a player-perceptible freeze.
            // p95 deliberately ignores single-point outliers, so a one-off "689 ms freeze" like this would slip through FPS002 — this check covers it separately.
            // Triple criterion: absolute value ≥100 ms (perceptible freeze) + ≥3×p95 (genuinely an outlier, not just generally slow) + ≥4× the average.
            if (maxMs >= 100.0 && maxMs >= p95Ms * 3.0 && maxMs >= avgMs * 4.0)
            {
                Severity sev = maxMs >= 500.0 ? Severity.Critical : Severity.Warning;
                string deepNote = r.WasDeepProfile
                    ? L.Tr("(Note: Deep Profile inflates frame times across the board, so absolute milliseconds run high; but the spike's deviation relative to p95/average is still real. Re-test on device to confirm the magnitude.)", "（注意：Deep Profile 会整体放大帧时间，绝对毫秒数偏高；但它相对 p95/均值的离群程度仍真实，建议真机复测确认量级。）")
                    : "";

                // Attribution: two complementary clues (see RuntimeSampler.SnapshotWorstFrame).
                //  - Top self-time entries: where time ultimately lands in leaf functions (often engine/third-party library leaves).
                //  - The script entry point on the heaviest call path: "which of your code" triggered it — self-time always lands in leaves and can't point to user scripts;
                //    you must drill down the main trunk by Total (including children) for it to surface (equivalent to sorting by Total in the Profiler's Hierarchy view).
                string attribution = "";
                string culpritScript = null;  // The user script (not a third-party package) that triggered the spike, used to attach Locate
                string culpritMethod = null;  // The culprit method name, so Locate can jump to the declaration line when opening the script
                bool offerLineAnalysis = false; // Whether to offer "line-level analysis / AI Fix" — only meaningful when the heavy work is genuinely inside a user script
                var wf = r.WorstFrame;
                if (wf != null && wf.HasData)
                {
                    string selfLeaders = "";
                    if (wf.TopMarkers.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var m in wf.TopMarkers)
                            parts.Add(L.Tr($"\"{m.Marker}\" {m.SelfMs:0.0} ms", $"「{m.Marker}」{m.SelfMs:0.0} ms"));
                        selfLeaders = string.Join(" · ", parts);
                    }

                    var full = wf.UserCallPath; // Methods mapped to scripts on the heaviest call path (outer→inner, includes user scripts and third-party package scripts)
                    // The deepest user script (non-Packages/) method = the entry point that triggered the spike.
                    int culpritIdx = -1;
                    for (int i = 0; i < full.Count; i++)
                        if (!IsPackageScript(full[i].ScriptPath)) culpritIdx = i;

                    if (culpritIdx >= 0)
                    {
                        var culprit = full[culpritIdx];
                        culpritScript = culprit.ScriptPath;
                        culpritMethod = MethodNameOf(culprit.Marker);

                        // User-script call chain (outer→inner, with consecutive duplicates removed), showing at most the innermost 3 levels.
                        var chain = new List<string>();
                        for (int i = 0; i <= culpritIdx; i++)
                        {
                            if (IsPackageScript(full[i].ScriptPath)) continue;
                            if (chain.Count > 0 && chain[chain.Count - 1] == full[i].Marker) continue;
                            chain.Add(full[i].Marker);
                        }
                        if (chain.Count > 3) chain.RemoveRange(0, chain.Count - 3);
                        string chainText = string.Join(" → ", chain);

                        // Whether the heavy work lands in a third-party package downstream of the culprit (e.g. A*'s BlockUntilCalculated) —
                        // if so, editing library source line by line is not the right model (same logic as [0.14.1] HOT001): withdraw line-level analysis and give usage-layer advice instead.
                        string downstreamPkg = null;
                        for (int i = culpritIdx + 1; i < full.Count; i++)
                            if (IsPackageScript(full[i].ScriptPath)) { downstreamPkg = PackageNameOf(full[i].ScriptPath); break; }

                        string head = L.Tr($"\n**This spike was triggered by your script: {culprit.Marker}** (~{culprit.TotalMs:0} ms including children, attributed by Total — matching Unity Profiler's Hierarchy ordering).", $"\n**这次尖刺是你的脚本触发的：{culprit.Marker}**（含子项约 {culprit.TotalMs:0} ms，按 Total 归因，与 Unity Profiler 的 Hierarchy 排序一致）。") +
                                      (chain.Count > 1 ? L.Tr($"\nCall chain (outer→inner): {chainText}.", $"\n调用链（外→内）：{chainText}。") : "");

                        if (downstreamPkg != null)
                        {
                            // The heavy work is inside a third-party package: the script is only the caller. No line-level analysis (it would jump into read-only library source, or scan your file and find nothing).
                            offerLineAnalysis = false;
                            attribution = head +
                                L.Tr($"\nBut the **actual time is spent inside the third-party package `{downstreamPkg}`** (top self-time: {selfLeaders}) — your script is only the caller; the heavy work is in the library. ", $"\n但**实际耗时在第三方包 `{downstreamPkg}` 内部**（self-time 大头：{selfLeaders}）——你的脚本只是发起调用的一方，重活在库里。") +
                                L.Tr("Editing library source line by line isn't the right model (package re-import overwrites it, and it breaks official support), so no line-level analysis / AI Fix is offered here. ", "逐行改库源码不是正确模型（包重导入会覆盖、且断官方支持），故这里不提供逐行分析/AI Fix。") +
                                L.Tr("The fix belongs at the **usage layer**: spread this one-off batch call across multiple frames/coroutines, reduce scale or precision (a coarser graph / fewer nodes), or cache and reuse the result. ", "优化在**用法层**：把这次一次性批量调用分摊到多帧/协程、降规模或精度（更粗的图/更少节点）、缓存结果复用。") +
                                L.Tr("Click Locate to open your call site, and use Explain to get AI suggestions tailored to it.", "点 Locate 打开你的调用点，用 Explain 让 AI 针对它给改法。");
                        }
                        else
                        {
                            // The heavy work is in the user script itself (or the engine/BCL it calls): line-level analysis is useful (find hot loops / per-frame allocations).
                            offerLineAnalysis = true;
                            attribution = head +
                                (string.IsNullOrEmpty(selfLeaders) ? "" :
                                    L.Tr($"\nTop self-time inside it: {selfLeaders}.", $"\n其内部 self-time 大头：{selfLeaders}。")) +
                                L.Tr(" Click \"Line-level analysis\" to drill into the specific slow/allocating lines in this method; if it's a one-off batch computation, the lever is reducing the work it triggers (spread across frames/coroutines, reduce scale, cache and reuse).", "点「逐行分析」可落到这个方法里具体慢/分配的行；若是一次性批量计算，着力点是减少它触发的工作量（分摊到多帧/协程、降规模、缓存复用）。");
                        }
                    }
                    else if (!string.IsNullOrEmpty(selfLeaders))
                    {
                        // No user script on the path (pure engine spike, or Deep Profile not enabled so no method-level markers) → fall back to self-time attribution.
                        attribution = L.Tr("\nThis frame was mostly spent in: ", "\n该帧主要耗在：") + selfLeaders +
                                      L.Tr(". These are the top self-time entries picked from the slowest frame (engine/loading noise already filtered out). ", "。这是从最慢那一帧挑出的 self-time 大户（已滤除引擎/加载噪音）。") +
                                      L.Tr("No specific user script could be located — to pinpoint the method, enable Deep Profile and re-sample.", "未能定位到具体用户脚本——若想精确到方法，开启 Deep Profile 后重采样。");
                    }
                }

                string capturedCulprit = culpritScript;
                string capturedMethod = culpritMethod;
                findings.Add(new Finding(
                    ruleId: "RUN.FPS003",
                    domain: Domain.Runtime,
                    severity: sev,
                    title: L.Tr($"Runtime single-frame stutter spike: slowest frame {maxMs:0} ms ({maxMs / System.Math.Max(avgMs, 0.01):0}x the {avgMs:0.0} ms average)", $"运行时单帧卡顿尖刺：最慢一帧 {maxMs:0} ms（均值 {avgMs:0.0} ms 的 {maxMs / System.Math.Max(avgMs, 0.01):0} 倍）"),
                    detail: L.Tr($"An isolated extra-long frame occurred during sampling — about {maxMs:0} ms for a single frame, far exceeding the 95th percentile {p95Ms:0.0} ms and the average {avgMs:0.0} ms. ", $"采样期间出现一次孤立的超长帧——单帧约 {maxMs:0} ms，远超 95 分位 {p95Ms:0.0} ms 与均值 {avgMs:0.0} ms。") +
                            L.Tr("Players will clearly feel a hitch/freeze. This kind of single-point spike is deliberately ignored by the p95 stutter check (FPS002), so it is flagged separately. ", "玩家会明显感到一次卡顿/冻结。这类单点尖刺被 p95 卡顿检测（FPS002）刻意忽略，故单独标出。") + deepNote + attribution + "\n" +
                            L.Tr("Common causes: a one-off batch computation (e.g. batch pathfinding/baking during level generation), synchronous asset loading, an Instantiate storm, or a forced GC.Collect. ", "常见成因：一次性批量计算（如关卡生成的批量寻路/烘焙）、同步资源加载、Instantiate 风暴、强制 GC.Collect。") +
                            L.Tr("If it happens during level generation/loading, consider spreading the work across frames (coroutines/chunking) or masking it with a loading screen. ", "若发生在关卡生成/加载阶段，考虑把工作分摊到多帧（协程/分块）或用加载屏遮蔽。") +
                            (string.IsNullOrEmpty(capturedCulprit)
                                ? L.Tr("Pinpointing: in the Unity Profiler, jump to the slowest frame and expand the CPU call stack to see the full call chain.", "精确定位：在 Unity Profiler 里跳到最慢那一帧、展开 CPU 调用栈即可看到完整调用链。")
                                : L.Tr("Click Locate to open the script pinpointed above directly.", "点 Locate 直接打开上面定位到的脚本。")),
                    targetPath: capturedCulprit,
                    ping: string.IsNullOrEmpty(capturedCulprit) ? (System.Action)null : () => ScannerUtil.OpenScript(capturedCulprit, capturedMethod),
                    codeFile: offerLineAnalysis ? capturedCulprit : null));
            }
        }

        private static void GcPerFrame(RuntimeProfileResult r, List<Finding> findings)
        {
            var gc = r.GcPerFrameBytes;
            if (gc == null || !gc.HasData) return;

            double avgBytes = gc.Avg;
            double maxBytes = gc.Max;

            // Sustained per-frame allocation is one of the primary causes of stutter. ≥1 KB/frame warrants attention; ≥8 KB/frame is escalated to high priority.
            if (avgBytes >= 1024)
            {
                Severity sev = avgBytes >= 8 * 1024 ? Severity.Critical : Severity.Warning;
                findings.Add(new Finding(
                    ruleId: "RUN.GC001",
                    domain: Domain.Runtime,
                    severity: sev,
                    title: L.Tr($"Sustained per-frame GC allocation: avg {Human(avgBytes)}/frame (peak {Human(maxBytes)})", $"运行时持续每帧 GC 分配：平均 {Human(avgBytes)}/帧（峰值 {Human(maxBytes)}）"),
                    detail: L.Tr($"During sampling, an average of {Human(avgBytes)} of managed-heap allocation was produced per frame (peak {Human(maxBytes)}). ", $"采样期间平均每帧产生 {Human(avgBytes)} 托管堆分配（峰值 {Human(maxBytes)}）。") +
                            L.Tr("Sustained per-frame allocation periodically triggers GC and causes stutter. This is runtime-**confirmed** GC pressure — ", "持续的每帧分配会周期性触发 GC、造成卡顿。这是运行时**证实**的 GC 压力——") +
                            L.Tr("next, run a scan in the main panel: the \"Script GC / per-frame allocation\" analysis pinpoints allocation sites line by line (GetComponent / string concatenation / LINQ, etc.), ", "下一步在主面板运行扫描，「脚本 GC / 每帧分配」分析会逐行定位分配点（GetComponent/字符串拼接/LINQ 等），") +
                            L.Tr("and AI Fix can patch them in one click. Entries marked as scripts in the \"CPU Hotspots\" below are the first things to investigate.", "并可用 AI Fix 一键修复。下方「CPU 热点」中标为脚本的条目是优先排查对象。")));
            }
        }

        private static void MemoryTrend(RuntimeProfileResult r, List<Finding> findings)
        {
            var mem = r.TotalMemoryBytes;
            if (mem == null || mem.SampleCount < 60) return; // Too few samples to draw a trend conclusion

            var gc  = r.GcUsedBytes;
            var gfx = r.GfxUsedBytes;
            // Use TrendDelta (second-half average − first-half average) rather than Last−First: more resilient to single-frame spikes / endpoint noise, and more reliable for direction.
            double gcGrowth  = (gc  != null && gc.HasData)  ? gc.TrendDelta  : 0;
            double gfxGrowth = (gfx != null && gfx.HasData) ? gfx.TrendDelta : 0;
            bool hasBreakdown = (gc != null && gc.HasData) || (gfx != null && gfx.HasData);

            // ── RUN.MEM002: Managed heap keeps growing (points more clearly to a C#-side leak than Total growth does) ──
            // The managed heap should fall back to a baseline after GC; a monotonically rising baseline is a strong signal that objects are held by strong references and cannot be reclaimed — purer than Total (which includes asset loading).
            // Use **absolute delta** as the primary criterion, not a ratio threshold: in the Editor the managed heap baseline is inflated to hundreds of MB by the editor itself, so a ratio criterion gets diluted and fails.
            // TrendDelta is second-half minus first-half average; under linear growth it is roughly half the full delta, so the threshold is set to 25 MB (≈ 50 MB net growth over the full window).
            if (gc != null && gc.SampleCount >= 60 && gcGrowth > 25L * 1024 * 1024 && r.DurationSeconds >= 5)
            {
                findings.Add(new Finding(
                    ruleId: "RUN.MEM002",
                    domain: Domain.Runtime,
                    severity: Severity.Warning,
                    title: L.Tr($"Managed heap keeps growing: +{Human(gcGrowth)} ({Human(gc.First)} → {Human(gc.Last)})", $"托管堆持续增长：+{Human(gcGrowth)}（{Human(gc.First)} → {Human(gc.Last)}）"),
                    detail: L.Tr($"The managed heap (GC Used Memory) grew monotonically by {Human(gcGrowth)} within the sampling window. ", $"托管堆（GC Used Memory）在采样窗口内单调增长 {Human(gcGrowth)}。") +
                            L.Tr("The managed heap should fall back to a baseline after GC; a steadily rising baseline points more clearly to a **C#-side object leak** (held by strong references and unreclaimable) ", "托管堆在 GC 后本应回落到基线，基线被持续抬高更明确地指向 **C# 侧对象泄漏**（被强引用无法回收），") +
                            L.Tr("rather than ordinary asset loading.\n\n", "而非普通的资源加载。\n\n") +
                            L.Tr("Frequent causes (by prevalence):\n", "高频成因（按常见度）：\n") +
                            L.Tr("1. Event/delegate subscriptions never unsubscribed with `-=` — the most common; the publisher holds the subscriber long-term and the whole reference chain stays uncollectable;\n", "1. 事件/委托订阅后未 `-=` 注销——最常见，发布者长期持有订阅者，整条引用链都回收不掉；\n") +
                            L.Tr("2. static collections / singleton caches that only grow (unbounded List/Dictionary growth);\n", "2. static 集合 / 单例缓存只增不删（List/Dictionary 无界增长）；\n") +
                            L.Tr("3. a coroutine/closure captured an object and holds it until the coroutine ends;\n", "3. 协程/闭包捕获了对象，协程未结束就一直持有；\n") +
                            L.Tr("4. DontDestroyOnLoad objects recreated on scene change without destroying the old instance.\n\n", "4. DontDestroyOnLoad 对象换场景时重复创建、旧实例未销毁。\n\n") +
                            L.Tr("Pinpointing: take two snapshots with the Unity Memory Profiler (with some activity between them) and diff them; see which object type's instance count is rising, then trace its reference chain back to the holder. ", "定位：用 Unity Memory Profiler 抓两个快照（间隔一段操作）做 diff，按对象类型看哪类实例数在涨，再回溯其引用链找到持有者。") +
                            L.Tr("Use Explain to get AI investigation suggestions for the type you suspect.", "可用 Explain 让 AI 针对你怀疑的类型给排查建议。")));
            }

            // ── RUN.MEM001: Total memory growth (includes asset loading; conservatively Info-level and requires verification) ──
            // Also uses TrendDelta, threshold 50 MB (≈ 100 MB full delta); ratio threshold dropped (fails under Editor's high baseline).
            double growth = mem.TrendDelta;
            if (growth > 50L * 1024 * 1024 && r.DurationSeconds >= 5)
            {
                string breakdown = "";
                if (hasBreakdown)
                {
                    // Remainder = Total delta minus the known managed-heap and graphics portions (mostly native containers/audio/physics, or Editor + Deep Profile's own overhead).
                    double otherGrowth = growth - gcGrowth - gfxGrowth;

                    breakdown = L.Tr("\n\nGrowth source breakdown:", "\n\n增长来源拆分：");
                    if (gc  != null && gc.HasData)  breakdown += L.Tr($" managed heap {Signed(gcGrowth)}", $" 托管堆 {Signed(gcGrowth)}");
                    if (gfx != null && gfx.HasData) breakdown += L.Tr($"{(gc != null && gc.HasData ? " ·" : "")} graphics resources {Signed(gfxGrowth)}", $"{(gc != null && gc.HasData ? " ·" : "")} 图形资源 {Signed(gfxGrowth)}");
                    breakdown += L.Tr($" · other/native {Signed(otherGrowth)}", $" · 其他/native {Signed(otherGrowth)}");

                    // Dominant direction: determined by each part's share of the Total delta (not gc vs. gfx relative to each other — otherwise a small managed-heap share could be mislabelled "primary").
                    if (gcGrowth > growth * 0.5 && gcGrowth > 20L * 1024 * 1024)
                        breakdown += L.Tr(". The growth is mainly on the **managed-heap side**: see RUN.MEM002 (C# object leak direction).", "。主要涨在**托管堆侧**：见 RUN.MEM002（C# 对象泄漏方向）。");
                    else if (gfxGrowth > growth * 0.5 && gfxGrowth > 30L * 1024 * 1024)
                        breakdown += L.Tr(". The growth is mainly on the **graphics-resources side**: check for RenderTextures new'd at runtime without Release(), and repeatedly loaded textures/materials that are never unloaded (call Resources.UnloadUnusedAssets when appropriate).", "。主要涨在**图形资源侧**：排查运行时 new 的 RenderTexture 未 Release()、重复加载未卸载的纹理/材质（适时 Resources.UnloadUnusedAssets）。");
                    else if (otherGrowth > growth * 0.5)
                        breakdown += L.Tr(". Most of the growth falls **outside the managed heap/graphics**, mostly native allocations (containers/audio/physics) — it may also be Profiler buffer overhead from Editor + Deep Profile itself. **Turn off Deep Profile or re-test on device** before deciding whether it's a real leak.", "。大部分增长**未落入托管堆/图形**，多为 native 分配（容器/音频/物理）——也可能是 Editor + Deep Profile 自身的 Profiler 缓冲开销，**建议关掉 Deep Profile 或在真机复测**再判断是否真泄漏。");
                }

                findings.Add(new Finding(
                    ruleId: "RUN.MEM001",
                    domain: Domain.Runtime,
                    severity: Severity.Info,
                    title: L.Tr($"Runtime memory keeps growing: +{Human(growth)} ({Human(mem.First)} → {Human(mem.Last)})", $"运行时内存持续增长：+{Human(growth)}（{Human(mem.First)} → {Human(mem.Last)}）"),
                    detail: L.Tr($"Total Used Memory grew by {Human(growth)} within the sampling window. **This may be a memory leak, or it may be normal asset loading / cache warm-up** — it needs verification: ", $"采样窗口内 Total Used Memory 增长了 {Human(growth)}。**可能是内存泄漏，也可能是正常的资源加载/缓存预热**，需复核：") +
                            L.Tr("re-sample during steady-state play (without loading new scenes/assets); if memory still grows monotonically, take and compare snapshots with the Unity Memory Profiler to pinpoint it.", "请在稳定运行（无加载新场景/资源）的状态下重新采样一段，若内存仍单调增长，再用 Unity Memory Profiler 抓快照对比定位。") +
                            breakdown));
            }
        }

        private static void DrawCallsAndSetPass(RuntimeProfileResult r, List<Finding> findings)
        {
            var setPass = r.SetPassCalls;
            if (setPass != null && setPass.HasData && setPass.Avg >= 300)
            {
                Severity sev = setPass.Avg >= 800 ? Severity.Warning : Severity.Info;
                findings.Add(new Finding(
                    ruleId: "RUN.SETPASS001",
                    domain: Domain.Runtime,
                    severity: sev,
                    title: L.Tr($"High runtime SetPass calls: avg {setPass.Avg:0}/frame (peak {setPass.Max:0})", $"运行时 SetPass 调用偏高：平均 {setPass.Avg:0}/帧（峰值 {setPass.Max:0}）"),
                    detail: L.Tr($"An average of {setPass.Avg:0} SetPass calls per frame. SetPass affects CPU render cost more than Draw Calls do. ", $"平均每帧 {setPass.Avg:0} 次 SetPass。SetPass 比 Draw Call 更影响 CPU 渲染开销。") +
                            L.Tr("Things to check: too many material/shader variants breaking batching, SRP Batcher compatibility (see material diagnostics MAT001/MAT002 in the main panel), ", "可排查：材质/Shader 变体过多导致批处理断裂、SRP Batcher 兼容性（见主面板材质诊断 MAT001/MAT002）、") +
                            L.Tr("and interleaving of transparent objects / different render queues. The exact threshold depends on your platform.", "半透明/不同渲染队列穿插。具体阈值取决于目标平台。")));
            }

            var draw = r.DrawCalls;
            if (draw != null && draw.HasData && draw.Avg >= 2000)
            {
                findings.Add(new Finding(
                    ruleId: "RUN.DRAW001",
                    domain: Domain.Runtime,
                    severity: Severity.Info,
                    title: L.Tr($"High runtime Draw Calls: avg {draw.Avg:0}/frame (peak {draw.Max:0})", $"运行时 Draw Call 偏高：平均 {draw.Avg:0}/帧（峰值 {draw.Max:0}）"),
                    detail: L.Tr($"An average of {draw.Avg:0} Draw Calls per frame. Consider static/dynamic batching, GPU Instancing, sprite atlasing (Sprite Atlas), ", $"平均每帧 {draw.Avg:0} 次 Draw Call。可考虑静态/动态批处理、GPU Instancing、合图（Sprite Atlas）、") +
                            L.Tr("or reducing the number of visible objects (culling/LOD). Whether this is too high depends on your platform and scene scale.", "或减少可见物体数量（剔除/LOD）。是否过高取决于目标平台与场景规模。")));
            }
        }

        private static void GpuBound(RuntimeProfileResult r, List<Finding> findings)
        {
            var gpu = r.GpuFrameTimeNs;
            var cpu = r.FrameTimeNs;

            if (gpu == null || !gpu.HasData)
            {
                // GPU counters unavailable. Two fundamentally different cases: WebGL is a platform-inherent impossibility (don't waste the user's time);
                // on other platforms it is usually that the Profiler's GPU module isn't enabled, or the target platform/graphics API doesn't support it — guide the user on how to investigate.
                bool isWebGL = UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.WebGL;
                string detail = isWebGL
                    ? L.Tr("**The WebGL platform cannot provide GPU frame time** — browsers/WebGL don't expose GPU timing (timer queries). This is an inherent platform limitation, ", "**WebGL 平台无法提供 GPU 帧时间**——浏览器/WebGL 不暴露 GPU 计时（timer query），这是平台固有限制，") +
                      L.Tr("not a sampling error, and it can't be solved by enabling the Profiler's GPU module. To gauge GPU load, look at **indirect indicators** instead: this panel's ", "不是采样出错，也无法通过开 Profiler 的 GPU 模块解决。判断 GPU 负载请改看**间接指标**：本面板的 ") +
                      L.Tr("Draw Calls / Batches / SetPass / triangles, plus scene overdraw and texture/RT VRAM.\n", "Draw Call / Batch / SetPass / 三角面，以及场景 overdraw、纹理/RT 显存。\n") +
                      L.Tr("Also note: in editor Play Mode, even with the build target set to WebGL, rendering uses the **editor's own GPU**, not the real WebGL runtime — ", "另注意：编辑器 Play Mode 即便目标平台设为 WebGL，渲染走的也是**编辑器自身的显卡**，并非真实 WebGL 运行环境——") +
                      L.Tr("to measure real WebGL frame time, use browser-side tools like Chrome DevTools / Spector.js.", "要测真实 WebGL 的帧时间，需在浏览器侧用 Chrome DevTools / Spector.js 等工具。")
                    : L.Tr("GPU frame time could not be collected on the current platform or configuration. Note: **before Unity 2022, GPU frame time is often unobtainable through public APIs in the editor** ", "当前平台或配置未能采集到 GPU 帧时间。说明：**Unity 2022 之前的编辑器下，GPU 帧时间往往无法通过公开 API 取得**") +
                      L.Tr("(FrameTimingManager often returns 0 in the editor, and frame data has no GPU column) — this is a Unity-version/editor limitation, not a PerfLint error. ", "（FrameTimingManager 在编辑器常返回 0、帧数据也无 GPU 列）——这是 Unity 版本/编辑器限制，非 PerfLint 出错。") +
                      L.Tr("How to investigate: (1) after upgrading to a 2022+ editor, PerfLint can read the Profiler \"GPU ms\" column directly; (2) enable the \"GPU\" module in the Unity Profiler window ", "排查：① 升级到 2022+ 编辑器后 PerfLint 可直接读 Profiler「GPU ms」列；② 在 Unity Profiler 窗口开「GPU」模块") +
                      L.Tr("to view GPU cost directly; (3) a device/Standalone build with Player ▸ Frame Timing Stats checked is more likely to produce data than the editor. ", "直接查看 GPU 耗时；③ 真机/Standalone 构建 + 勾 Player ▸ Frame Timing Stats 比编辑器更可能出数。") +
                      L.Tr("Until then, use this panel's Draw Calls / Batches / SetPass / triangles as an indirect gauge of GPU load.", "在此之前，用本面板的 Draw Call / Batch / SetPass / 三角面作为 GPU 负载的间接判断。");

                findings.Add(new Finding(
                    ruleId: "RUN.GPU001",
                    domain: Domain.Runtime,
                    severity: Severity.Info,
                    title: isWebGL
                        ? L.Tr("GPU frame time unavailable (inherent WebGL platform limitation, not an error)", "GPU 帧时间不可用（WebGL 平台固有限制，非错误）")
                        : L.Tr("GPU frame time unavailable — can't tell whether the GPU is the bottleneck", "GPU 帧时间不可用——无法判断 GPU 是否为瓶颈"),
                    detail: detail));
                return;
            }

            double gpuMs  = gpu.Avg / 1_000_000.0;
            double cpuMs  = cpu != null && cpu.HasData ? cpu.Avg / 1_000_000.0 : 0;
            double gpuMax = gpu.Max / 1_000_000.0;

            if (cpuMs > 0 && gpuMs > cpuMs * 1.15)
            {
                // GPU time is notably above the CPU main-thread frame time → GPU is the bottleneck.
                Severity sev = gpuMs > cpuMs * 1.5 ? Severity.Critical : Severity.Warning;
                findings.Add(new Finding(
                    ruleId: "RUN.GPU001",
                    domain: Domain.Runtime,
                    severity: sev,
                    title: L.Tr($"GPU-bound: GPU {gpuMs:0.0} ms vs CPU {cpuMs:0.0} ms/frame (peak GPU {gpuMax:0.0} ms)", $"GPU 瓶颈：GPU {gpuMs:0.0} ms vs CPU {cpuMs:0.0} ms/帧（峰值 GPU {gpuMax:0.0} ms）"),
                    detail: L.Tr($"GPU frame time ({gpuMs:0.0} ms) exceeds the main-thread CPU frame time ({cpuMs:0.0} ms), meaning the GPU is the current frame-rate cap. ", $"GPU 帧时间（{gpuMs:0.0} ms）超过主线程 CPU 帧时间（{cpuMs:0.0} ms），说明 GPU 是当前帧率上限。") +
                            L.Tr("Optimization directions (ordered by payoff):\n", "优化方向（按效益排序）：\n") +
                            L.Tr("1. Reduce triangle count or enable LOD (see RUN.GPU002);\n", "1. 减少三角面数或开启 LOD（见 RUN.GPU002）；\n") +
                            L.Tr("2. Reduce overdraw: fewer stacked transparent objects, enable Early-Z / Depth Prepass;\n", "2. 降低 Overdraw：减少半透明物体层叠、开启 Early-Z/Depth Prepass；\n") +
                            L.Tr("3. Simplify shader instructions: avoid heavy computation in the fragment shader; move what you can to the vertex stage;\n", "3. 精简 Shader 指令：避免在 Fragment Shader 里做复杂计算，能在顶点做的不放片元；\n") +
                            L.Tr("4. Lower the resolution or enable Dynamic Resolution;\n", "4. 降低分辨率或开启动态分辨率（Dynamic Resolution）；\n") +
                            L.Tr("5. Check real-time shadows: an oversized Shadow Distance or too many cascades is a hidden GPU killer.", "5. 检查实时阴影：Shadow Distance 过大、Cascade 过多是 GPU 隐形杀手。")));
            }
            else if (cpuMs > 0 && gpuMs < cpuMs * 0.6)
            {
                // GPU is clearly idle; CPU is the bottleneck. Rendering itself is not the problem.
                findings.Add(new Finding(
                    ruleId: "RUN.GPU001",
                    domain: Domain.Runtime,
                    severity: Severity.Info,
                    title: L.Tr($"GPU idle ({gpuMs:0.0} ms), CPU is the current bottleneck", $"GPU 空闲（{gpuMs:0.0} ms），CPU 是当前瓶颈"),
                    detail: L.Tr($"GPU frame time is only {gpuMs:0.0} ms, far below CPU {cpuMs:0.0} ms — render complexity is not the current bottleneck. ", $"GPU 帧时间仅 {gpuMs:0.0} ms，远低于 CPU {cpuMs:0.0} ms，渲染复杂度不是当前瓶颈。") +
                            L.Tr("Prioritize the CPU hotspots above (script logic / GC). If you later increase scene complexity the GPU may become the bottleneck again; refer back to this rule's advice then.", "优先排查上方 CPU 热点（脚本逻辑/GC）。若后续增加场景复杂度 GPU 可能再成瓶颈，届时再参考本规则建议。")));
            }
            // GPU ≈ CPU (0.6–1.15×): both sides are balanced; no finding is generated to avoid noise.
        }

        private static void TriangleDensity(RuntimeProfileResult r, List<Finding> findings)
        {
            var tri = r.Triangles;
            if (tri == null || !tri.HasData) return;

            double avgTri = tri.Avg;
            if (avgTri >= 300_000)
            {
                Severity sev = avgTri >= 600_000 ? Severity.Critical : Severity.Warning;
                findings.Add(new Finding(
                    ruleId: "RUN.GPU002",
                    domain: Domain.Runtime,
                    severity: sev,
                    title: L.Tr($"High runtime triangle count: avg {avgTri / 1000.0:0.0}K tris/frame (peak {tri.Max / 1000.0:0.0}K)", $"运行时三角面数偏高：平均 {avgTri / 1000.0:0.0}K 面/帧（峰值 {tri.Max / 1000.0:0.0}K）"),
                    detail: L.Tr($"During sampling, an average of {avgTri / 1000.0:0.0}K triangles were rendered per frame — a direct indicator of GPU geometry-processing cost. ", $"采样期间平均每帧渲染 {avgTri / 1000.0:0.0}K 个三角面，这是 GPU 几何处理开销的直接指标。") +
                            L.Tr("Optimization directions:\n", "优化方向：\n") +
                            L.Tr("1. Set up LOD Groups for complex models (3-4 levels recommended; distant models can drop below 10% of the triangle count);\n", "1. 为复杂模型设置 LOD Group（推荐 3~4 级，远距离模型面数可降至 10% 以下）；\n") +
                            L.Tr("2. Check visibility culling: is Occlusion Culling baked, and is the camera's Far Clip too large;\n", "2. 检查可见性剔除：Occlusion Culling 是否已烘焙，相机 Far Clip 是否过大；\n") +
                            L.Tr("3. For high-poly characters/foliage instanced in large numbers, consider GPU Instancing + simplified LODs.\n", "3. 高面数角色/植被若大量实例化，考虑 GPU Instancing + 简化 LOD。\n") +
                            L.Tr("Reference baselines: <50K for mobile, <200K for mid-range PC scenes, up to 500K+ on high-end PC/console.", "参考基准：移动端建议 <50K，PC 中档场景 <200K，高端 PC/Console 可至 500K+。")));
            }
        }

        private static void BatchingEfficiency(RuntimeProfileResult r, List<Finding> findings)
        {
            var draw   = r.DrawCalls;
            var batch  = r.Batches;
            if (draw == null || !draw.HasData || batch == null || !batch.HasData) return;

            // Only evaluate when the Draw Call count is large enough for batching to provide a benefit; the ratio is meaningless when it is too small.
            if (draw.Avg < 50) return;

            double ratio = batch.Avg / draw.Avg; // The closer to 1, the worse the batching (every DC is an independent Batch)
            if (ratio < 0.9) return;

            Severity sev = ratio >= 0.97 ? Severity.Warning : Severity.Info;

            // Use actual scene measurements to turn "why isn't it batching" from a generic checklist into a concrete diagnosis.
            var sb = r.SceneBatching;
            string sceneInsight = "";
            if (sb != null && sb.HasData)
            {
                sceneInsight =
                    L.Tr($"\n\nMeasured in this scene: {sb.RendererCount} mesh Renderers actually use {sb.UniqueMaterialCount} distinct material objects", $"\n\n本次场景实测：{sb.RendererCount} 个网格 Renderer 实际用了 {sb.UniqueMaterialCount} 个不同材质对象");
                if (sb.MaterialReuseRatio < 1.5)
                    sceneInsight += L.Tr(" — nearly one material per object; the lack of material reuse is the direct reason batching can't merge them.", "——几乎一物一材质，材质不复用是批处理无法合并的直接原因。");
                else
                    sceneInsight += L.Tr($" (material reuse ratio {sb.MaterialReuseRatio:0.0}:1).", $"（材质复用率 {sb.MaterialReuseRatio:0.0}:1）。");
                if (sb.InstancedMaterialRendererCount > 0)
                    sceneInsight += L.Tr($" Also detected {sb.InstancedMaterialRendererCount} objects instancing their materials at runtime — this is the primary root cause; see RUN.GPU004.", $" 并检测到 {sb.InstancedMaterialRendererCount} 个物体在运行时实例化了材质——这是首要根因，详见 RUN.GPU004。");
            }

            findings.Add(new Finding(
                ruleId: "RUN.GPU003",
                domain: Domain.Runtime,
                severity: sev,
                title: L.Tr($"Low batching efficiency: {batch.Avg:0} separate batches out of {draw.Avg:0} Draw Calls (ratio {ratio:P0})", $"批处理效率低：{draw.Avg:0} Draw Call 中 {batch.Avg:0} 个独立 Batch（比率 {ratio:P0}）"),
                detail: L.Tr($"Batches / Draw Calls ≈ {ratio:P0}, meaning batching is barely working — the vast majority of Draw Calls are not merged. ", $"Batches / Draw Calls ≈ {ratio:P0}，说明批处理几乎未生效——绝大多数 Draw Call 都未被合并。") +
                        sceneInsight + "\n\n" +
                        L.Tr("Checklist (ordered by prevalence):\n", "排查清单（按常见程度排序）：\n") +
                        L.Tr("1. Runtime material instancing: accessing renderer.material (instead of sharedMaterial) in a script clones the material, and each clone is a separate batch; switch to MaterialPropertyBlock or sharedMaterial (see RUN.GPU004);\n", "1. 运行时材质实例化：脚本里访问 renderer.material（而非 sharedMaterial）会克隆材质，每个克隆都是独立 batch；改用 MaterialPropertyBlock 或 sharedMaterial（见 RUN.GPU004）；\n") +
                        L.Tr("2. SRP projects: check whether materials are SRP Batcher-compatible (the shader must wrap properties in a CBUFFER); see \"MAT001 / MAT002\" in the main panel;\n", "2. SRP 项目：检查材质是否兼容 SRP Batcher（要求 Shader 用 CBUFFER 封装属性）；见主面板「MAT001 / MAT002」；\n") +
                        L.Tr("3. Static batching: confirm that non-moving objects in the scene have Batching Static checked;\n", "3. 静态批处理：确认场景中不会移动的物体已勾选 Batching Static；\n") +
                        L.Tr("4. Dynamic batching: meshes with <300 vertices can use it (Project Settings → Player → Dynamic Batching);\n", "4. 动态批处理：顶点数 <300 的 Mesh 可启用（Project Settings → Player → Dynamic Batching）；\n") +
                        L.Tr("5. GPU Instancing: for many objects sharing the same Mesh and Material (e.g. grass/bullets), enable \"Enable GPU Instancing\".\n", "5. GPU Instancing：大量同 Mesh 同 Material 的物体（如草地/子弹）启用 Enable GPU Instancing。\n") +
                        L.Tr("Note: under URP/HDRP the SRP Batcher reduces SetPass cost, so the Batch count not dropping is normal; what matters is whether SetPass is already well below the Draw Call count.", "注意：URP/HDRP 下 SRP Batcher 减少的是 SetPass 开销，Batch 数不降也正常；关键看 SetPass 是否已大幅低于 Draw Call 数。")));
        }

        /// <summary>
        /// Runtime material-instancing detection — **impossible via static scanning, unique to Play Mode**. Accessing renderer.material (not sharedMaterial)
        /// clones the material, the material name gains a "(Instance)" suffix, and every clone is a separate batch — a common true culprit behind "100% separate batches".
        /// Pinpoints the root cause directly to the specific GameObject (selectable with one click).
        /// </summary>
        private static void MaterialInstancing(RuntimeProfileResult r, List<Finding> findings)
        {
            var sb = r.SceneBatching;
            if (sb == null || !sb.HasData) return;
            if (sb.InstancedMaterialRendererCount < 5) return; // Threshold: a small number of instances may be intentional; avoid noise

            int n = sb.InstancedMaterialRendererCount;
            Severity sev = n >= 20 ? Severity.Warning : Severity.Info;

            findings.Add(new Finding(
                ruleId: "RUN.GPU004",
                domain: Domain.Runtime,
                severity: sev,
                title: L.Tr($"Runtime material instancing: {n} objects cloned their materials, breaking batching", $"运行时材质实例化：{n} 个物体克隆了材质，打断批处理"),
                detail: L.Tr($"In the scene, {n} objects have material names ending in \"(Instance)\", meaning they were cloned at runtime — ", $"场景中有 {n} 个物体的材质名以「(Instance)」结尾，说明运行时被克隆过——") +
                        L.Tr("almost always triggered by a script reading `renderer.material` (or `.materials`). The moment you access `.material`, Unity copies a separate material for that object, ", "几乎都是脚本里读了 `renderer.material`（或 `.materials`）触发的。Unity 一旦访问 `.material` 就会为该物体复制一份独立材质，") +
                        L.Tr("so every object becomes its own batch and batching fails completely. This is a problem that only surfaces at runtime and is invisible to static scanning.\n\n", "于是每个物体都成了独立 batch，批处理彻底失效。这是运行时才暴露、静态扫描看不到的问题。\n\n") +
                        L.Tr("Fix (pick one):\n", "修复方式（二选一）：\n") +
                        L.Tr("1. **Read-only access → use sharedMaterial**: if you only read material properties, change `renderer.material` to `renderer.sharedMaterial` (note: modifying sharedMaterial affects all objects sharing that material);\n", "1. **只读属性 → 用 sharedMaterial**：若只是读取材质属性，把 `renderer.material` 改为 `renderer.sharedMaterial`（注意：改 sharedMaterial 会影响所有共用该材质的物体）；\n") +
                        L.Tr("2. **To change a single object's appearance → use MaterialPropertyBlock**:\n", "2. **要改单个物体外观 → 用 MaterialPropertyBlock**：\n") +
                        "   ```csharp\n" +
                        "   var mpb = new MaterialPropertyBlock();\n" +
                        "   renderer.GetPropertyBlock(mpb);\n" +
                        "   mpb.SetColor(\"_BaseColor\", color);\n" +
                        "   renderer.SetPropertyBlock(mpb);\n" +
                        "   ```\n" +
                        L.Tr("   MaterialPropertyBlock changes per-object properties without cloning the material, preserving batching (and it's compatible with GPU Instancing).\n\n", "   MaterialPropertyBlock 改单物体属性而不克隆材质，保留批处理（且兼容 GPU Instancing）。\n\n") +
                        L.Tr("Click \"Locate\" to select the detected instanced objects, then go back to the script and search for the `.material` calls that operate on them. Use Explain to get an AI fix tailored to your situation.", "点「Locate」选中检测到的实例化物体，回到脚本搜索操作它们的 `.material` 调用。用 Explain 可让 AI 给出针对你具体情况的改法。"),
                ping: () => sb.SelectInstancedExamples()));
        }

        private static void Hotspots(RuntimeProfileResult r, List<Finding> findings)
        {
            if (!r.HotspotsAvailable)
            {
                findings.Add(new Finding(
                    ruleId: "RUN.HOT000",
                    domain: Domain.Runtime,
                    severity: Severity.Info,
                    title: L.Tr("CPU hotspot collection unavailable (degraded to counter-only diagnostics)", "CPU 热点采集不可用（已降级为仅计数器诊断）"),
                    detail: L.Tr("The current Unity version/platform couldn't replay frame data to merge hotspots. Counter-layer diagnostics (frame rate / GC / memory / rendering) are unaffected. ", "当前 Unity 版本/平台未能回放帧数据做热点归并。计数器层诊断（帧率/GC/内存/渲染）不受影响。") +
                            L.Tr("For method-level hotspot localization, open the Unity Profiler window (Window > Analysis > Profiler) to inspect alongside.", "如需方法级热点定位，可打开 Unity Profiler 窗口（Window > Analysis > Profiler）配合查看。")));
                return;
            }

            bool anyScriptMapped = false;
            foreach (var h in r.Hotspots)
            {
                // Only emit a finding for "significant enough" hotspots, to avoid noise: script hotspots >3% or self-time >0.5ms/frame; non-script >8%.
                bool significant = h.IsScript
                    ? (h.SharePercent >= 3 || h.SelfMsPerFrame >= 0.5)
                    : h.SharePercent >= 8;
                if (!significant) continue;
                if (h.IsScript) anyScriptMapped = true;

                Severity sev = h.SharePercent >= 15 ? Severity.Warning : Severity.Info;

                // Peak markedly above average (IsSpiky) → an occasional stutter spike rather than a sustained hotspot. Targeted sampling already folds spike frames into the merge,
                // so here we surface "average vs. peak" explicitly to distinguish the two problem types (sustained slowness / occasional stutter).
                string msText = h.IsSpiky
                    ? L.Tr($"avg {h.SelfMsPerFrame:0.00} ms · peak {h.PeakMsPerFrame:0.0} ms/frame", $"平均 {h.SelfMsPerFrame:0.00} ms · 峰值 {h.PeakMsPerFrame:0.0} ms/帧")
                    : L.Tr($"{h.SelfMsPerFrame:0.00} ms/frame", $"{h.SelfMsPerFrame:0.00} ms/帧");
                string spikeDetail = h.IsSpiky
                    ? L.Tr($"\n\n⚠️ **Intermittent stutter spike**: the single-frame peak {h.PeakMsPerFrame:0.0} ms is far above the average {h.SelfMsPerFrame:0.00} ms — this isn't a sustained per-frame cost but a periodic/occasional burst on certain frames (e.g. scheduled recomputation, bursty loading, instantiation peaks). Optimization should focus on 'which frames, and why triggered', not treat it as a fixed per-frame cost.", $"\n\n⚠️ **偶发卡顿尖刺**：单帧峰值 {h.PeakMsPerFrame:0.0} ms 远高于平均 {h.SelfMsPerFrame:0.00} ms——它不是每帧持续耗时，而是周期性/偶发地在某些帧爆发（如定时重算、突发加载、实例化峰值）。优化应聚焦『哪些帧、为何触发』，而非当作每帧固定开销。")
                    : "";

                if (h.IsScript && IsPackageScript(h.ScriptPath))
                {
                    // Hotspot is in a third-party package (under Packages/): usually read-only, library source shouldn't be edited, so don't guide toward line-level analysis / AI Fix
                    // (no codeFile → the runtime panel won't show the "line-level analysis" button; AI Fix, which needs codeFile/line numbers, is disabled along with it).
                    // Give "usage-layer" optimization advice instead; keep Locate for viewing source and pinpointing your own call site.
                    string capturedPath = h.ScriptPath;
                    string capturedMethod = MethodNameOf(h.Marker);
                    string pkg = PackageNameOf(capturedPath);
                    string pkgLabel = pkg != null ? L.Tr($"the third-party package `{pkg}`", $"第三方包 `{pkg}`") : L.Tr("a third-party package", "第三方包");
                    findings.Add(new Finding(
                        ruleId: "RUN.HOT001",
                        domain: Domain.Runtime,
                        severity: sev,
                        title: L.Tr($"CPU hotspot (third-party package): {h.Marker} — {msText} ({h.SharePercent:0.0}%)", $"CPU 热点（第三方包）：{h.Marker} — {msText}（{h.SharePercent:0.0}%）"),
                        detail: L.Tr($"Main-thread self-time hotspot \"{h.Marker}\" averages {h.SelfMsPerFrame:0.00} ms per frame, about {h.SharePercent:0.0}% of frame time. ", $"主线程 self-time 热点「{h.Marker}」平均每帧 {h.SelfMsPerFrame:0.00} ms，占帧时间约 {h.SharePercent:0.0}%。") +
                                L.Tr($"This hotspot is inside {pkgLabel}, third-party code — you generally shouldn't edit library source directly (package re-import overwrites it, and it breaks official support), so no line-level analysis / AI Fix is offered here. ", $"该热点在{pkgLabel}内，属第三方代码——通常不应直接改库源码（包重导入会覆盖、且断官方支持），因此这里不提供逐行分析/AI Fix。") +
                                L.Tr("The fix belongs at the **usage layer**: reduce call frequency (e.g. spread pathfinding/recomputation across frames, add throttling), reduce precision or scale (a coarser graph, fewer nodes/iterations), ", "优化应落在**用法层**：降低调用频率（如把寻路/重算分摊到多帧、加节流）、降低精度或规模（更粗的图、更少节点/迭代）、") +
                                L.Tr("cache and reuse results, or evaluate a lighter alternative. Click Locate to open the source and help you find where you initiate the call.", "缓存结果复用、或评估更轻的替代实现。点 Locate 打开源码可帮你定位到自己发起调用的位置。") + spikeDetail,
                        targetPath: capturedPath,
                        ping: () => ScannerUtil.OpenScript(capturedPath, capturedMethod)));
                }
                else if (h.IsScript)
                {
                    string capturedPath = h.ScriptPath;
                    string capturedMethod = MethodNameOf(h.Marker);
                    findings.Add(new Finding(
                        ruleId: "RUN.HOT001",
                        domain: Domain.Runtime,
                        severity: sev,
                        title: L.Tr($"CPU hotspot (script): {h.Marker} — {msText} ({h.SharePercent:0.0}%)", $"CPU 热点（脚本）：{h.Marker} — {msText}（{h.SharePercent:0.0}%）"),
                        detail: L.Tr($"Main-thread self-time hotspot \"{h.Marker}\" averages {h.SelfMsPerFrame:0.00} ms per frame, about {h.SharePercent:0.0}% of frame time. ", $"主线程 self-time 热点「{h.Marker}」平均每帧 {h.SelfMsPerFrame:0.00} ms，占帧时间约 {h.SharePercent:0.0}%。") +
                                L.Tr("Runtime has confirmed this script is a CPU hotspot. Click Locate to open the script, then optimize line by line with the \"Script GC / per-frame allocation\" analysis in the main panel; ", "运行时已证实此脚本是 CPU 热点。点 Locate 打开脚本，结合主面板「脚本 GC / 每帧分配」分析逐行优化；") +
                                L.Tr("if the hotspot stems from per-frame allocation, AI Fix can patch it.", "若热点源于每帧分配，可用 AI Fix 修复。") + spikeDetail,
                        targetPath: capturedPath,
                        ping: () => ScannerUtil.OpenScript(capturedPath, capturedMethod),
                        codeFile: capturedPath));
                }
                else
                {
                    findings.Add(new Finding(
                        ruleId: "RUN.HOT002",
                        domain: Domain.Runtime,
                        severity: sev,
                        title: L.Tr($"CPU hotspot: {h.Marker} — {msText} ({h.SharePercent:0.0}%)", $"CPU 热点：{h.Marker} — {msText}（{h.SharePercent:0.0}%）"),
                        detail: L.Tr($"Main-thread self-time hotspot \"{h.Marker}\" averages {h.SelfMsPerFrame:0.00} ms per frame, about {h.SharePercent:0.0}% of frame time. ", $"主线程 self-time 热点「{h.Marker}」平均每帧 {h.SelfMsPerFrame:0.00} ms，占帧时间约 {h.SharePercent:0.0}%。") +
                                L.Tr("This marker couldn't be mapped to a project script (it may be an engine subsystem or third-party code); to dig deeper, expand the call stack for this marker in the Unity Profiler.", "该 marker 未能映射到工程脚本（可能是引擎子系统或第三方代码），如需深挖可在 Unity Profiler 中按此 marker 展开调用栈。") + spikeDetail));
                }
            }

            // If not a single script hotspot was mapped, give different advice depending on whether Deep Profile is enabled.
            if (r.Hotspots.Count > 0 && !anyScriptMapped)
            {
                if (r.WasDeepProfile)
                {
                    // Deep Profile is on but all hotspots are still in system/engine code (high self-time in GC.Alloc, String.Concat, etc.),
                    // meaning user scripts all have low self-time and the hotspot root cause lies in the system calls they invoke.
                    findings.Add(new Finding(
                        ruleId: "RUN.HOT003",
                        domain: Domain.Runtime,
                        severity: Severity.Info,
                        title: L.Tr("Deep Profile is on, but all hotspots are in system/engine code (not user scripts)", "Deep Profile 已开启，但热点均在系统/引擎代码（非用户脚本）"),
                        detail: L.Tr("During sampling, the highest self-time markers (e.g. GC.Alloc, String.Concat, Physics.Step) couldn't be mapped to project scripts. ", "采样期间 self-time 最高的 marker（如 GC.Alloc、String.Concat、Physics.Step 等）无法映射到工程脚本。") +
                                L.Tr("This means CPU time is mostly spent inside engine or BCL calls, with user scripts merely initiating them.\n", "这说明 CPU 时间主要消耗在引擎或 BCL 调用内部，用户脚本只是发起调用方。\n") +
                                L.Tr("Recommendation: in the Unity Profiler \"CPU Usage\" view, switch to \"Hierarchy\" and sort by \"Total (incl. children)\" ", "建议：在 Unity Profiler「CPU Usage」视图切换到「Hierarchy」，按「Total（含子）」排序，") +
                                L.Tr("to find the highest-cost **user script methods** — they are the entry points calling these system hotspots, and the place to focus optimization. ", "找到耗时最高的**用户脚本方法**——它们就是调用这些系统热点的入口，也是优化的着力点。") +
                                L.Tr("If the top hotspot is GC.Alloc, combine the \"runtime per-frame GC allocation\" finding below with the static panel's \"Script GC\" analysis to locate the allocation source.", "若顶部热点是 GC.Alloc，结合下方「运行时每帧 GC 分配」finding，用静态面板的「脚本 GC」分析定位分配源。")));
                }
                else
                {
                    // Deep Profile is off, so markers are coarse-grained aggregates (BehaviourUpdate, etc.) that can't be narrowed to a specific script.
                    findings.Add(new Finding(
                        ruleId: "RUN.HOT003",
                        domain: Domain.Runtime,
                        severity: Severity.Info,
                        title: L.Tr("Want hotspots pinpointed to specific script methods? Enable Deep Profile and re-sample", "想把热点定位到具体脚本方法？开启 Deep Profile 再采样"),
                        detail: L.Tr("This run's hotspots are coarse-grained markers (e.g. BehaviourUpdate aggregates the Update of all scripts), so they can't be narrowed to a specific script. ", "本次热点为粗粒度 marker（如 BehaviourUpdate 聚合了所有脚本的 Update），未能精确到某个脚本。") +
                                L.Tr("Enable \"Deep Profile\" in the Unity Profiler window, then re-enter Play Mode and sample; markers will refine to ClassName.Method(), ", "在 Unity Profiler 窗口开启「Deep Profile」后重新进入 Play Mode 采样，marker 会细化为 ClassName.Method()，") +
                                L.Tr("and this panel can then map hotspots to scripts and offer Locate / line-level analysis. Note that Deep Profile has high overhead — use it for localization only, not for measuring real frame rate.", "本面板即可把热点映射到脚本并提供 Locate / 逐行分析。注意 Deep Profile 开销很大，仅用于定位、勿用于测真实帧率。")));
                }
            }
        }

        private static string Human(double bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes:0} B";
        }

        /// <summary>A memory amount with a sign (used for growth breakdowns; the negative sign uses the minus character to avoid confusion with a hyphen).</summary>
        private static string Signed(double bytes) =>
            (bytes >= 0 ? "+" : "−") + Human(System.Math.Abs(bytes));

        /// <summary>Extracts the method name from a marker display name: "LevelGenerator.AllVehiclesHavePaths()" → "AllVehiclesHavePaths". Returns null if none.</summary>
        private static string MethodNameOf(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return null;
            int paren = marker.IndexOf('(');
            string head = paren >= 0 ? marker.Substring(0, paren) : marker;
            int dot = head.LastIndexOf('.');
            string m = dot >= 0 ? head.Substring(dot + 1) : head;
            return string.IsNullOrEmpty(m) ? null : m;
        }

        /// <summary>Whether the script is in a third-party package (under Packages/ rather than Assets/). Unity asset paths always use forward slashes and a capitalized Packages prefix.</summary>
        private static bool IsPackageScript(string path) =>
            !string.IsNullOrEmpty(path) && path.Replace('\\', '/').StartsWith("Packages/");

        /// <summary>Extracts the package name from a package script path: Packages/com.arongranberg.astar/.../X.cs → com.arongranberg.astar. Returns null if none.</summary>
        private static string PackageNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace('\\', '/');
            const string prefix = "Packages/";
            if (!p.StartsWith(prefix)) return null;
            int start = prefix.Length;
            int slash = p.IndexOf('/', start);
            return slash > start ? p.Substring(start, slash - start) : null;
        }
    }
}
