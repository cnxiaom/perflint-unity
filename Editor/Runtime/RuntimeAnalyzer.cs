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
            GraphicsMemory(r, findings);
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

            // Use the MEDIAN (sustained) frame time, not the mean: a one-off level-gen/loading freeze inflates the mean (e.g. to "33 FPS") while the game
            // actually runs at 100 FPS steady-state. FPS001 is about *sustained* low frame rate; the catastrophic freezes are reported separately by FPS003.
            double medMs = ft.Median / 1_000_000.0;
            if (medMs <= 0) return;
            double fps = 1000.0 / medMs;

            if (medMs > 33.3) // < 30 FPS sustained
            {
                findings.Add(new Finding(
                    ruleId: "RUN.FPS001",
                    domain: Domain.Runtime,
                    severity: Severity.Critical,
                    title: L.Tr($"Low sustained frame rate: median {fps:0} FPS (main thread {medMs:0.0} ms/frame)", $"运行时持续帧率偏低：中位 {fps:0} FPS（主线程 {medMs:0.0} ms/帧）"),
                    detail: L.Tr($"The median main-thread frame time during sampling was {medMs:0.0} ms (~{fps:0} FPS), below 30 FPS — a sustained slowdown, not a one-off spike. ", $"采样期间主线程中位帧时间 {medMs:0.0} ms（约 {fps:0} FPS），低于 30 FPS——是持续性偏慢，而非一次性尖刺。") +
                            L.Tr("The main thread is the bottleneck. Expand the \"CPU Hotspots\" below to find the most expensive methods, then optimize line by line with the script GC analysis in the main panel. ", "主线程是瓶颈所在域。展开下方「CPU 热点」定位最耗时的方法，并结合主面板的脚本 GC 分析逐行优化。") +
                            L.Tr("(Your frame-rate target depends on your platform; mobile typically aims for 30/60 FPS.)", "（帧率目标取决于你的目标平台，移动端通常以 30/60 FPS 为线。）")));
            }
            else if (medMs > 22.2) // < 45 FPS sustained
            {
                findings.Add(new Finding(
                    ruleId: "RUN.FPS001",
                    domain: Domain.Runtime,
                    severity: Severity.Warning,
                    title: L.Tr($"Moderate sustained frame rate: median {fps:0} FPS (main thread {medMs:0.0} ms/frame)", $"运行时持续帧率中等：中位 {fps:0} FPS（主线程 {medMs:0.0} ms/帧）"),
                    detail: L.Tr($"The median main-thread frame time during sampling was {medMs:0.0} ms (~{fps:0} FPS). If you target 60 FPS and need more headroom, ", $"采样期间主线程中位帧时间 {medMs:0.0} ms（约 {fps:0} FPS）。若目标 60 FPS 仍有余量需优化，") +
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

            // A catastrophic single-frame freeze is present (FPS003 reports it in detail, per culprit, below). When so, suppress the coarser FPS002 — its
            // p95 just restates the same freeze cluster and adds noise. (FPS001 already uses the median, so a freeze can't drag it into a false "slow game".)
            bool catastrophic = maxMs >= 100.0 && maxMs >= p95Ms * 3.0 && maxMs >= avgMs * 4.0;

            // Stutter signal: p95 notably above average (long-tail), p95 itself slow enough. Distinct from sustained low frame rate; suppressed when FPS003 covers it.
            if (p95Ms > 33.3 && p95Ms >= avgMs * 1.8 && !catastrophic)
            {
                findings.Add(new Finding(
                    ruleId: "RUN.FPS002",
                    domain: Domain.Runtime,
                    severity: Severity.Warning,
                    title: L.Tr($"Runtime stutter spikes: p95 {p95Ms:0.0} ms (avg {avgMs:0.0} ms, peak {maxMs:0.0} ms)", $"运行时卡顿尖刺：p95 {p95Ms:0.0} ms（均值 {avgMs:0.0} ms，峰值 {maxMs:0.0} ms）"),
                    detail: L.Tr($"The 95th-percentile frame time {p95Ms:0.0} ms is well above the average {avgMs:0.0} ms, indicating recurring stutter (hitches). ", $"95% 分位帧时间 {p95Ms:0.0} ms 明显高于平均 {avgMs:0.0} ms，说明存在周期性卡顿（hitch）。") +
                            L.Tr("Common causes: per-frame/bursty GC collections, synchronous asset loading, instantiation spikes. First check below for a \"per-frame GC allocation\" warning.", "常见成因：每帧/突发 GC 回收、资源同步加载、实例化峰值。先看下方是否有「每帧 GC 分配」告警。")));
            }

            // Single-frame spike(s): max far exceeds p95 (outliers, not a long tail) and reaches a player-perceptible freeze. p95 ignores single-point
            // outliers, so these slip through FPS002 — covered here per culprit. The triple criterion (`catastrophic`, above) gates whether to report at all.
            if (!catastrophic) return;

            string deepNote = r.WasDeepProfile
                ? L.Tr("(Note: Deep Profile inflates frame times across the board, so absolute milliseconds run high; but the spike's deviation relative to p95/average is still real. Re-test on device to confirm the magnitude.)", "（注意：Deep Profile 会整体放大帧时间，绝对毫秒数偏高；但它相对 p95/均值的离群程度仍真实，建议真机复测确认量级。）")
                : "";

            // A level-gen freeze is a CLUSTER of heavy frames across several phases (PlaceObstaclesAsync / AllVehiclesHavePaths / …), not one frame.
            // RuntimeSampler aggregates spike frames by culprit (script+method); emit ONE finding per culprit, ranked by cost, each with its own Locate.
            var frames = r.WorstFrames;
            if (frames == null || frames.Count == 0)
            {
                findings.Add(BuildSpikeFinding(null, maxMs, avgMs, p95Ms, deepNote, "", couldntCapture: false));
                return;
            }

            // Keep only frames whose CULPRIT is itself a genuine freeze (by its own inclusive Total, not the EditorLoop-inflated frame total) — this filters
            // red-herrings like a 50ms log call that merely rode on a high-overhead frame, and keeps ranking honest so a real 363ms phase isn't displaced.
            double floor = System.Math.Max(100.0, avgMs * 3.0);
            // Keep only frames that (a) are themselves a genuine freeze (by culprit Total, not the EditorLoop-inflated frame total) AND (b) we can attribute to a
            // script. An unattributable spike (an EditorLoop/Deep-Profile artifact frame with no user/package method on its path) is pure noise once we have real
            // culprits — drop it. Only when NOTHING attributes do we emit a single honest "couldn't capture" pointer.
            var attributed = new List<WorstFrameInfo>();
            foreach (var wf in frames)
            {
                if (wf == null || !wf.HasData || SpikeDisplayMs(wf) < floor) continue;
                if (wf.UserCallPath != null && wf.UserCallPath.Count > 0) attributed.Add(wf);
            }

            if (attributed.Count == 0)
            {
                // A spike happened (gate passed) but no frame could be attributed to a script → be honest rather than misattribute.
                findings.Add(BuildSpikeFinding(null, maxMs, avgMs, p95Ms, deepNote, "", couldntCapture: true));
                return;
            }

            const int MaxSpikeFindings = 4;
            for (int i = 0; i < attributed.Count && i < MaxSpikeFindings; i++)
            {
                // On the first (worst) finding, if there are more culprits than we show, name the overflow so nothing is silently dropped.
                string extra = "";
                if (i == 0 && attributed.Count > MaxSpikeFindings)
                {
                    var more = new List<string>();
                    for (int j = MaxSpikeFindings; j < attributed.Count && more.Count < 6; j++)
                        more.Add(SpikeCulpritShort(attributed[j]) ?? L.Tr($"{SpikeDisplayMs(attributed[j]):0} ms frame", $"{SpikeDisplayMs(attributed[j]):0} ms 帧"));
                    extra = L.Tr($"\n\nThis window had {attributed.Count} distinct spiking culprits; showing the top {MaxSpikeFindings} by cost. Others: {string.Join(", ", more)}.", $"\n\n本窗口共 {attributed.Count} 个不同尖刺来源，仅列耗时前 {MaxSpikeFindings} 个。其余：{string.Join("、", more)}。");
                }
                findings.Add(BuildSpikeFinding(attributed[i], SpikeDisplayMs(attributed[i]), avgMs, p95Ms, deepNote, extra, couldntCapture: false));
            }
        }

        /// <summary>The culprit's game-relevant magnitude (ms): deepest USER method's inclusive Total; else deepest PACKAGE method's Total; else the frame self-total. Mirrors RuntimeSampler.CulpritMagnitude.</summary>
        private static double SpikeDisplayMs(WorstFrameInfo wf)
        {
            if (wf == null || !wf.HasData) return 0;
            var full = wf.UserCallPath;
            if (full != null)
            {
                for (int i = full.Count - 1; i >= 0; i--)
                    if (!IsPackageScript(full[i].ScriptPath)) return full[i].TotalMs > 0 ? full[i].TotalMs : wf.TotalSelfMs;
                if (full.Count > 0) return full[full.Count - 1].TotalMs > 0 ? full[full.Count - 1].TotalMs : wf.TotalSelfMs;
            }
            return wf.TotalSelfMs;
        }

        /// <summary>The innermost mapped method on a spike frame's heaviest path — deepest user script, else deepest package method, else null. For the "others" overflow list.</summary>
        private static string SpikeCulpritShort(WorstFrameInfo wf)
        {
            var full = wf?.UserCallPath;
            if (full == null) return null;
            for (int i = full.Count - 1; i >= 0; i--)
                if (!IsPackageScript(full[i].ScriptPath)) return full[i].Marker;
            return full.Count > 0 ? full[full.Count - 1].Marker : null;
        }

        /// <summary>
        /// Build one RUN.FPS003 finding for a single spike frame (one culprit). spikeMs is that frame's magnitude. When wf is null this is a generic spike
        /// pointer: couldntCapture=false → "jump to the slowest frame in the Profiler"; couldntCapture=true → "the spike frame's call stack couldn't be captured".
        /// </summary>
        // displayMs = the culprit's game-relevant magnitude (SpikeDisplayMs), already computed by the caller (matches Unity Profiler, excludes EditorLoop/Deep-Profile
        // overhead that inflates the raw frame total); for the wf==null generic path it's the window max.
        private static Finding BuildSpikeFinding(WorstFrameInfo wf, double displayMs, double avgMs, double p95Ms, string deepNote, string extraNote, bool couldntCapture)
        {
            // Attribution (see RuntimeSampler.SnapshotWorstFrame): the heaviest-Total call path names the triggering script; self-time leaders name where the time lands.
            string attribution = "";
            string culpritScript = null;    // user/package script for Locate
            string culpritMethod = null;    // method name so Locate jumps to the declaration
            string culpritShort = null;     // short label for the title suffix
            bool offerLineAnalysis = false; // line-level analysis / AI Fix only when the heavy work is genuinely inside a user script
            bool attributed = false;

            if (wf != null && wf.HasData)
            {
                string selfLeaders = ""; double leadersSum = 0;
                if (wf.TopMarkers.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var m in wf.TopMarkers)
                    {
                        parts.Add(L.Tr($"\"{m.Marker}\" {m.SelfMs:0.0} ms", $"「{m.Marker}」{m.SelfMs:0.0} ms"));
                        leadersSum += m.SelfMs;
                    }
                    selfLeaders = string.Join(" · ", parts);
                }

                var full = wf.UserCallPath; // methods on the heaviest call path (outer→inner, user + package)
                int culpritIdx = -1;
                for (int i = 0; i < full.Count; i++)
                    if (!IsPackageScript(full[i].ScriptPath)) culpritIdx = i; // deepest user script = the entry point that triggered the spike

                if (culpritIdx >= 0)
                {
                    attributed = true;
                    var culprit = full[culpritIdx];
                    culpritScript = culprit.ScriptPath;
                    culpritMethod = MethodNameOf(culprit.Marker);
                    culpritShort = culprit.Marker;

                    // User-script call chain (outer→inner, consecutive dupes removed, innermost 3 levels).
                    var chain = new List<string>();
                    for (int i = 0; i <= culpritIdx; i++)
                    {
                        if (IsPackageScript(full[i].ScriptPath)) continue;
                        if (chain.Count > 0 && chain[chain.Count - 1] == full[i].Marker) continue;
                        chain.Add(full[i].Marker);
                    }
                    if (chain.Count > 3) chain.RemoveRange(0, chain.Count - 3);
                    string chainText = string.Join(" → ", chain);

                    // Heavy work in a third-party package downstream of the culprit (e.g. A*)? Then it's a caller — usage-layer advice, no line-level analysis.
                    string downstreamPkg = null;
                    for (int i = culpritIdx + 1; i < full.Count; i++)
                        if (IsPackageScript(full[i].ScriptPath)) { downstreamPkg = PackageNameOf(full[i].ScriptPath); break; }

                    string gcPhrase = culprit.GcBytes >= 1024 ? L.Tr($", allocating ~{Human(culprit.GcBytes)}", $"、分配约 {Human(culprit.GcBytes)}") : "";
                    string head = L.Tr($"\n**This spike was triggered by your script: {culprit.Marker}** (~{culprit.TotalMs:0} ms including children{gcPhrase}, attributed by Total — matching Unity Profiler's Hierarchy ordering).", $"\n**这次尖刺是你的脚本触发的：{culprit.Marker}**（含子项约 {culprit.TotalMs:0} ms{gcPhrase}，按 Total 归因，与 Unity Profiler 的 Hierarchy 排序一致）。") +
                                  (chain.Count > 1 ? L.Tr($"\nCall chain (outer→inner): {chainText}.", $"\n调用链（外→内）：{chainText}。") : "");

                    if (downstreamPkg != null)
                    {
                        offerLineAnalysis = false;
                        attribution = head +
                            L.Tr($"\nBut the **actual time is spent inside the third-party package `{downstreamPkg}`** (top self-time: {selfLeaders}) — your script is only the caller; the heavy work is in the library. ", $"\n但**实际耗时在第三方包 `{downstreamPkg}` 内部**（self-time 大头：{selfLeaders}）——你的脚本只是发起调用的一方，重活在库里。") +
                            L.Tr("Editing library source line by line isn't the right model (package re-import overwrites it, and it breaks official support), so no line-level analysis / AI Fix is offered here. ", "逐行改库源码不是正确模型（包重导入会覆盖、且断官方支持），故这里不提供逐行分析/AI Fix。") +
                            L.Tr("The fix belongs at the **usage layer**: spread this one-off batch call across multiple frames/coroutines, reduce scale or precision (a coarser graph / fewer nodes), or cache and reuse the result. ", "优化在**用法层**：把这次一次性批量调用分摊到多帧/协程、降规模或精度（更粗的图/更少节点）、缓存结果复用。") +
                            L.Tr("Click Locate to open your call site, and use Explain to get AI suggestions tailored to it.", "点 Locate 打开你的调用点，用 Explain 让 AI 针对它给改法。");
                    }
                    else
                    {
                        offerLineAnalysis = true;
                        attribution = head +
                            (string.IsNullOrEmpty(selfLeaders) ? "" :
                                L.Tr($"\nTop self-time inside it: {selfLeaders}.", $"\n其内部 self-time 大头：{selfLeaders}。")) +
                            L.Tr(" Click \"Line-level analysis\" to drill into the specific slow/allocating lines in this method; if it's a one-off batch computation, the lever is reducing the work it triggers (spread across frames/coroutines, reduce scale, cache and reuse).", "点「逐行分析」可落到这个方法里具体慢/分配的行；若是一次性批量计算，着力点是减少它触发的工作量（分摊到多帧/协程、降规模、缓存复用）。");
                    }
                }
                // No user script on the path, but third-party package method(s) are (e.g. A* pathfinding). Name the package, usage-layer advice, Locate to library source.
                else if (full.Count > 0)
                {
                    attributed = true;
                    var pkgFrame = full[full.Count - 1]; // deepest mapped method (all are package here)
                    culpritScript = pkgFrame.ScriptPath;
                    culpritMethod = MethodNameOf(pkgFrame.Marker);
                    culpritShort = pkgFrame.Marker;
                    offerLineAnalysis = false;
                    string pkg = PackageNameOf(pkgFrame.ScriptPath);
                    string pkgLabel = pkg != null ? L.Tr($"the third-party package `{pkg}`", $"第三方包 `{pkg}`") : L.Tr("a third-party package", "第三方包");
                    string pkgGcPhrase = pkgFrame.GcBytes >= 1024 ? L.Tr($", allocating ~{Human(pkgFrame.GcBytes)}", $"、分配约 {Human(pkgFrame.GcBytes)}") : "";
                    attribution =
                        L.Tr($"\n**This spike is inside {pkgLabel}: {pkgFrame.Marker}** (~{pkgFrame.TotalMs:0} ms including children{pkgGcPhrase}, attributed by Total). ", $"\n**这次尖刺发生在{pkgLabel}内部：{pkgFrame.Marker}**（含子项约 {pkgFrame.TotalMs:0} ms{pkgGcPhrase}，按 Total 归因）。") +
                        L.Tr("No user script was on the main-thread call path — your code triggers this work indirectly (a common case: a synchronous pathfinding/graph rebuild or NavMesh/mesh bake kicked off during level generation). ", "主线程调用路径上没有你的脚本——是你的代码间接触发了这段工作（常见：关卡生成时同步发起的寻路/图重建、NavMesh/网格烘焙）。") +
                        L.Tr("Editing library source isn't the right model, so no line-level analysis / AI Fix is offered here. The fix is at the **usage layer**: don't rebuild/scan synchronously per object — batch it, spread it across frames, do it once, or run it async/off the main thread. ", "改库源码不是正确模型，故这里不提供逐行分析/AI Fix。优化在**用法层**：别每个物体都同步重建/重扫——批量处理、分摊到多帧、只做一次，或改成异步/放到非主线程。") +
                        L.Tr("Click Locate to open the library method, then trace back to where you invoke it; use Explain for AI suggestions tailored to it.", "点 Locate 打开库方法，再回溯到你发起调用的位置；用 Explain 让 AI 针对它给改法。");
                }
                // Self-time leaders are trustworthy only when they explain a real chunk of the frame; if time is spread across engine/GC internals (filtered), fall through to honest.
                else if (!string.IsNullOrEmpty(selfLeaders) && leadersSum >= wf.TotalSelfMs * 0.2)
                {
                    attributed = true;
                    attribution = L.Tr("\nThis frame was mostly spent in: ", "\n该帧主要耗在：") + selfLeaders +
                                  L.Tr(". These are the top self-time entries picked from the slowest frame (engine/loading noise already filtered out). ", "。这是从最慢那一帧挑出的 self-time 大户（已滤除引擎/加载噪音）。") +
                                  L.Tr("No specific user script could be located — to pinpoint the method, enable Deep Profile and re-sample.", "未能定位到具体用户脚本——若想精确到方法，开启 Deep Profile 后重采样。");
                }

                if (!attributed)
                    couldntCapture = true; // captured a frame but its time is spread across engine/GC internals with no dominant user-code marker
            }

            if (!attributed && couldntCapture)
                attribution = L.Tr("\nThe slowest frame's call stack couldn't be captured — the spike likely occurred outside the sampling window (keep sampling running while it happens), or its time is spread across engine/GC internals with no single user-code hotspot. ", "\n最慢那一帧的调用栈未能捕获——尖刺很可能发生在采样窗口之外（在它发生时保持采样即可捕获），或耗时分散在引擎/GC 内部、没有单一用户代码热点。");

            Severity sev = displayMs >= 500.0 ? Severity.Critical : Severity.Warning;
            string capturedCulprit = culpritScript;
            string capturedMethod = culpritMethod;
            string titleSuffix = culpritShort != null ? $" — {culpritShort}" : "";
            bool hasCulprit = !string.IsNullOrEmpty(capturedCulprit);
            // Concise detail: intro + the specific attribution + one action line. The generic "common causes / spread across frames" boilerplate is kept ONLY
            // for the unattributed case (where it's the only guidance we have) — repeating it on every attributed finding is what made the panel read as noise.
            string intro = L.Tr($"An extra-long frame occurred during sampling — about {displayMs:0} ms for a single frame, far exceeding the 95th percentile {p95Ms:0.0} ms and the average {avgMs:0.0} ms. Players will clearly feel a hitch/freeze. ", $"采样期间出现一次超长帧——单帧约 {displayMs:0} ms，远超 95 分位 {p95Ms:0.0} ms 与均值 {avgMs:0.0} ms。玩家会明显感到一次卡顿/冻结。");
            // Attributed findings already end with their own Locate/Explain guidance (in `attribution`) — don't append a second, redundant Locate line.
            string tail = hasCulprit
                ? ""
                : L.Tr("Common causes: a one-off batch computation (batch pathfinding/baking during level generation), synchronous asset loading, an Instantiate storm, or a forced GC.Collect. In the Unity Profiler, jump to the slowest frame and expand the CPU call stack to see the full chain.", "常见成因：一次性批量计算（关卡生成的批量寻路/烘焙）、同步资源加载、Instantiate 风暴、强制 GC.Collect。在 Unity Profiler 里跳到最慢那一帧、展开 CPU 调用栈即可看到完整调用链。");
            return new Finding(
                ruleId: "RUN.FPS003",
                domain: Domain.Runtime,
                severity: sev,
                title: L.Tr($"Runtime stutter spike: ~{displayMs:0} ms ({displayMs / System.Math.Max(avgMs, 0.01):0}x the {avgMs:0.0} ms average){titleSuffix}", $"运行时卡顿尖刺：约 {displayMs:0} ms（均值 {avgMs:0.0} ms 的 {displayMs / System.Math.Max(avgMs, 0.01):0} 倍）{titleSuffix}"),
                detail: intro + deepNote + attribution + (string.IsNullOrEmpty(tail) ? "" : "\n" + tail) + extraNote,
                targetPath: capturedCulprit,
                ping: string.IsNullOrEmpty(capturedCulprit) ? (System.Action)null : () => ScannerUtil.OpenScript(capturedCulprit, capturedMethod),
                codeFile: offerLineAnalysis ? capturedCulprit : null);
        }

        private static void GcPerFrame(RuntimeProfileResult r, List<Finding> findings)
        {
            var gc = r.GcPerFrameBytes;
            if (gc == null || !gc.HasData) return;

            // Use the MEDIAN (sustained) per-frame allocation, not the mean: a level-gen/loading burst allocates MBs on a few frames and inflates the mean,
            // but that transient GC is already attributed per-culprit by RUN.FPS003 (which now shows the allocating method + its bytes). GC001 is about
            // *sustained* per-frame allocation (GetComponent / string concat / LINQ in Update), which the static "Script GC" analysis pinpoints line by line.
            double medBytes = gc.Median;
            double maxBytes = gc.Max;

            // ≥1 KB/frame sustained warrants attention; ≥8 KB/frame is escalated to high priority.
            if (medBytes >= 1024)
            {
                Severity sev = medBytes >= 8 * 1024 ? Severity.Critical : Severity.Warning;
                string intro = L.Tr($"During sampling, a median of {Human(medBytes)} of managed-heap allocation was produced per frame (peak {Human(maxBytes)}) — a sustained per-frame allocation (not a one-off level-gen burst; those are attributed per-culprit by the runtime stutter-spike findings above). Sustained per-frame allocation periodically triggers GC and causes stutter. ", $"采样期间每帧中位产生 {Human(medBytes)} 托管堆分配（峰值 {Human(maxBytes)}）——这是持续性的每帧分配（而非一次性关卡生成爆发，后者已由上方运行时尖刺 finding 逐 culprit 归因）。持续的每帧分配会周期性触发 GC、造成卡顿。");

                var site = r.TopGcSite;
                if (site != null && !string.IsNullOrEmpty(site.ScriptPath))
                {
                    // Runtime attribution: point Locate at the actual per-frame allocator we measured this session, not the static "Script GC" panel.
                    string method = MethodNameOf(site.Method);
                    string sitePath = site.ScriptPath;
                    findings.Add(new Finding(
                        ruleId: "RUN.GC001",
                        domain: Domain.Runtime,
                        severity: sev,
                        title: L.Tr($"Runtime GC allocation: median {Human(medBytes)}/frame — heaviest allocator {site.Method}", $"运行时 GC 分配：中位 {Human(medBytes)}/帧——分配最大的是 {site.Method}"),
                        detail: intro +
                                L.Tr($"**Runtime attribution: the heaviest per-frame allocator measured was `{site.Method}` (up to ~{Human(site.BytesPerFrame)} in a single frame).** Click Locate to open it, then cut its allocations at the source (cache buffers/arrays instead of allocating each call, avoid per-frame GetComponent / LINQ / string concatenation / boxing; if it's a level-gen batch, allocate once and reuse). ", $"**运行时归因：本次单帧分配最大的是 `{site.Method}`（单帧最高约 {Human(site.BytesPerFrame)}）。**点 Locate 打开它，从源头减少分配（缓存 buffer/数组而非每次新建、避免每帧 GetComponent/LINQ/字符串拼接/装箱；若是关卡生成的批量分配，改成分配一次复用）。") +
                                L.Tr("(No line-level Script-GC scan is offered here — the runtime allocation is inside this method's logic/subtree, not a static allocation pattern; Locate + manual review is the reliable path.)", "（这里不提供逐行「脚本 GC」扫描——运行时分配来自该方法的逻辑/子树，而非静态可识别的分配模式；Locate 打开人工审阅更可靠。）"),
                        targetPath: sitePath,
                        ping: () => ScannerUtil.OpenScript(sitePath, method)));
                }
                else if (!r.WasDeepProfile)
                {
                    // Couldn't attribute because Deep Profile was OFF → the Hierarchy has only coarse markers (BehaviourUpdate, etc.) that don't map to a
                    // method. Don't send the user to an (often empty) static panel — tell them the one action that unlocks function-level GC attribution.
                    findings.Add(new Finding(
                        ruleId: "RUN.GC001",
                        domain: Domain.Runtime,
                        severity: sev,
                        title: L.Tr($"Runtime GC allocation: median {Human(medBytes)}/frame (peak {Human(maxBytes)}) — enable Deep Profile to pinpoint the source", $"运行时 GC 分配：中位 {Human(medBytes)}/帧（峰值 {Human(maxBytes)}）——开 Deep Profile 才能定位来源"),
                        detail: intro +
                                L.Tr("**Deep Profile is OFF**, so these allocations can't be attributed to a specific method — the Hierarchy only has coarse markers. Turn on the \"Deep Profile\" toggle at the top of this panel and re-sample; this finding will then pinpoint the allocating function and Locate straight to it. ", "**未开启 Deep Profile**,无法把这些分配归因到具体方法——Hierarchy 只有粗粒度 marker。点本面板顶部的「Deep Profile」开关后重新采样,本条即可定位到分配函数并直接 Locate 过去。") +
                                L.Tr("(Deep Profile has high overhead — use it for localization, not for measuring real frame rate.)", "（Deep Profile 开销大,仅用于定位,别用来测真实帧率。）")));
                        // No Locate/Ping: without Deep Profile there is no function to open, and the static panel would likely be empty.
                }
                else
                {
                    // Deep Profile was ON but no dominant runtime allocator resolved → the allocation is genuinely spread thin, or lands inside a third-party
                    // package / engine call that isn't a user script. Point to the static Script GC analysis (which shows a helpful empty-state when it can't see it).
                    findings.Add(new Finding(
                        ruleId: "RUN.GC001",
                        domain: Domain.Runtime,
                        severity: sev,
                        title: L.Tr($"Runtime GC allocation: median {Human(medBytes)}/frame (peak {Human(maxBytes)}) — spread across many sites", $"运行时 GC 分配：中位 {Human(medBytes)}/帧（峰值 {Human(maxBytes)}）——分散在多处"),
                        detail: intro +
                                L.Tr("No single runtime method dominates the allocation (it's spread across many sites, or lands inside a third-party package / engine call). Check the main PerfLint panel's \"Script GC / per-frame allocation\" analysis for line-level patterns (GetComponent / string concatenation / LINQ, etc.); ", "没有单一运行时方法主导分配（分散在多处,或落在第三方包/引擎调用里）。到主 PerfLint 面板的「脚本 GC / 每帧分配」分析里看逐行分配模式（GetComponent/字符串拼接/LINQ 等）;") +
                                L.Tr("if it finds nothing, record a GC.Alloc sample in the Unity Profiler (CPU module → \"GC Alloc\" column) to catch boxing / package / engine allocations the static scan can't see.", "若查不到,用 Unity Profiler 录一段 GC.Alloc（CPU 模块的「GC Alloc」列）来捕获静态扫描看不到的装箱/包/引擎分配。"),
                        ping: () => PerfLint.UI.PerfLintWindow.OpenWindow().FocusOnScriptGcRules()));
                }
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

            // ── RUN.MEM003: Name WHICH object/asset categories grew (leak-suspect: created and never destroyed). Attributes the memory growth without a heap
            // snapshot — fires alongside MEM001 (real total growth) and lists the categories whose count/memory climbed over the window. ──
            if (growth > 50L * 1024 * 1024 && r.DurationSeconds >= 5 && r.CategoryCounters != null)
            {
                var grown = new List<string>();
                bool materialGrew = false;

                void CheckCount(string key, string label, double floor)
                {
                    if (r.CategoryCounters.TryGetValue(key, out var s) && s != null && s.HasData && s.TrendDelta >= floor && s.Last > s.First)
                        grown.Add($"{label} +{s.TrendDelta:0} ({s.First:0} → {s.Last:0})");
                }
                void CheckMem(string key, string label, double floorBytes)
                {
                    if (r.CategoryCounters.TryGetValue(key, out var s) && s != null && s.HasData && s.TrendDelta >= floorBytes && s.Last > s.First)
                        grown.Add($"{label} +{Human(s.TrendDelta)} ({Human(s.First)} → {Human(s.Last)})");
                }

                CheckCount("GameObject Count", L.Tr("GameObjects", "GameObject"), 20);
                CheckCount("Object Count",     L.Tr("UnityEngine.Objects", "UnityEngine.Object 总数"), 50);
                CheckCount("Texture Count",    L.Tr("Textures", "纹理"), 10);
                CheckMem  ("Texture Memory",   L.Tr("Texture memory", "纹理显存"), 8L * 1024 * 1024);
                CheckCount("Mesh Count",       L.Tr("Meshes", "网格"), 10);
                CheckMem  ("Mesh Memory",      L.Tr("Mesh memory", "网格内存"), 8L * 1024 * 1024);
                if (r.CategoryCounters.TryGetValue("Material Count", out var matS) && matS != null && matS.HasData && matS.TrendDelta >= 10 && matS.Last > matS.First)
                {
                    materialGrew = true;
                    grown.Add($"{L.Tr("Materials", "材质")} +{matS.TrendDelta:0} ({matS.First:0} → {matS.Last:0})");
                }

                if (grown.Count > 0)
                {
                    // Gated cross-ref: only mention GPU004 when runtime material instancing actually crossed its emission threshold (never a dangling reference).
                    var sb = r.SceneBatching;
                    string matNote = (materialGrew && sb != null && sb.HasData && sb.InstancedMaterialRendererCount >= MaterialInstancingMinCount)
                        ? L.Tr(" (materials are also being cloned at runtime — see RUN.GPU004)", "（材质还在运行时被克隆——见 RUN.GPU004）")
                        : "";
                    findings.Add(new Finding(
                        ruleId: "RUN.MEM003",
                        domain: Domain.Runtime,
                        severity: Severity.Info,
                        title: L.Tr("Object/asset instances growing during sampling", "运行时对象/资源实例持续增长"),
                        detail: L.Tr("These object/asset categories climbed over the sampling window:\n  • ", "以下对象/资源类别在采样窗口内增长：\n  • ") + string.Join("\n  • ", grown) + matNote + "\n\n" +
                                L.Tr("This may be normal (loading a level creates objects) OR a leak — objects/assets created and never destroyed. ", "这可能正常（加载关卡会创建对象），也可能是泄漏——创建后从未销毁。") +
                                L.Tr("Verify: sample again during steady-state play (no new scenes/assets loading); if these counts keep rising and don't fall back to a baseline, the objects aren't being destroyed. ", "复核：在稳定运行（不加载新场景/资源）时再采样一段；若这些数量持续上升、不回落到基线，说明对象没有被销毁。") +
                                L.Tr("Common causes: spawned objects / VFX not pooled or Destroy()'d, materials/textures instantiated at runtime, DontDestroyOnLoad duplicates. Use the Unity Memory Profiler to confirm the exact type.", "常见成因：生成的物体/特效未池化或未 Destroy()、运行时实例化的材质/纹理、DontDestroyOnLoad 重复。用 Unity Memory Profiler 确认具体类型。")));
                }
            }
        }

        // ── RUN.MEM004: name the biggest textures / render targets / meshes in memory (VRAM localization). Fires when a single asset is genuinely large. ──
        private static void GraphicsMemory(RuntimeProfileResult r, List<Finding> findings)
        {
            var sb = r.SceneBatching;
            if (sb == null || !sb.HasData) return;

            // RUN.MEM005: many IDENTICAL runtime RenderTextures alive at once → likely created repeatedly without Release() (a VRAM leak static scanning can't see).
            var rt = sb.SuspectRtLeak;
            if (rt != null && rt.Count >= 8)
            {
                findings.Add(new Finding(
                    ruleId: "RUN.MEM005",
                    domain: Domain.Runtime,
                    severity: Severity.Warning,
                    title: L.Tr($"{rt.Count} identical {rt.Width}×{rt.Height} RenderTextures alive ({Human(rt.TotalBytes)}) — likely not Released()", $"{rt.Count} 个相同的 {rt.Width}×{rt.Height} RenderTexture 同时存活（{Human(rt.TotalBytes)}）——疑似没 Release()"),
                    detail: L.Tr($"{rt.Count} RenderTextures of {rt.Width}×{rt.Height} ({rt.Format}), created at runtime (not project assets), are alive at once and consume {Human(rt.TotalBytes)}. ", $"{rt.Count} 个 {rt.Width}×{rt.Height}（{rt.Format}）的 RenderTexture 在运行时创建（非工程资产）、同时存活，占用 {Human(rt.TotalBytes)}。") +
                            L.Tr("You rarely need this many identical render targets — this usually means a script does `new RenderTexture(...)` repeatedly (per frame / per spawn / per camera) and never calls `Release()` / `Destroy()` on the old one. ", "很少需要这么多相同的渲染目标——通常是脚本反复 `new RenderTexture(...)`（每帧/每次生成/每个相机）而没有对旧的调用 `Release()` / `Destroy()`。") +
                            L.Tr("Fix: cache and reuse a single RenderTexture, or use RenderTexture.GetTemporary / ReleaseTemporary for short-lived ones. (If you already pool them via GetTemporary, this may be the pool and is expected.) ", "修复：缓存复用同一个 RenderTexture，或短生命周期的用 RenderTexture.GetTemporary / ReleaseTemporary。（若你已用 GetTemporary 池化，这可能就是池、属正常。）") +
                            L.Tr("Click Locate to select them.", "点 Locate 选中它们。"),
                    ping: sb.HasRtLeakExamples ? (System.Action)sb.SelectRtLeakExamples : null));
            }

            var assets = sb.TopMemoryAssets;
            if (assets == null || assets.Count == 0) return;
            if (assets[0].Bytes < 16L * 1024 * 1024) return; // only flag when at least one asset is genuinely large (e.g. an uncompressed 2K texture / big RT)

            var lines = new List<string>();
            foreach (var a in assets)
            {
                if (a.Bytes < 2L * 1024 * 1024) break; // stop before listing small ones
                lines.Add($"{a.Name} ({a.Kind}) — {Human(a.Bytes)}");
                if (lines.Count >= 6) break;
            }
            if (lines.Count == 0) return;

            // Locate can only reveal *project* assets (Project window / import settings). When the biggest consumers are runtime render targets
            // (deferred G-buffer, temp pools, camera targets — no asset path, not GameObjects), there is nothing to select, so we gate the button
            // off and swap the "Click Locate" line for RT-appropriate guidance (resolution / Release) — never point at a button that isn't there.
            bool canLocate = sb.HasTopMemoryAssets;
            string closingLine = canLocate
                ? L.Tr("Click Locate to select the project textures/meshes above (opens their import settings). Runtime render targets in the list have no project location to reveal.", "点 Locate 选中上面的工程纹理/网格（会打开其导入设置）。列表中的运行时渲染目标没有可打开的工程位置。")
                : L.Tr("These biggest consumers are runtime render targets (created by the rendering path / post-processing, not project assets), so there's nothing to Locate — their size is driven by render resolution and pipeline settings: lower the resolution or enable Dynamic Resolution, and Release() the ones you no longer need.", "这些最大消耗是运行时渲染目标（由渲染路径/后处理创建，非工程资产），没有可 Locate 的对象——其大小由渲染分辨率与管线设置决定：降低分辨率或开启动态分辨率，并 Release() 不再需要的目标。");

            findings.Add(new Finding(
                ruleId: "RUN.MEM004",
                domain: Domain.Runtime,
                severity: Severity.Info,
                title: L.Tr($"Large graphics assets in memory: {assets[0].Name} {Human(assets[0].Bytes)} + more", $"显存占用大的图形资源：{assets[0].Name} {Human(assets[0].Bytes)} 等"),
                detail: L.Tr("The biggest textures / render targets / meshes currently loaded (by runtime memory):\n  • ", "当前加载的最大纹理/渲染目标/网格（按运行时内存）：\n  • ") + string.Join("\n  • ", lines) + "\n\n" +
                        L.Tr("Check whether each needs its current resolution/format: compress textures (ASTC / BCn) instead of RGBA32, lower an oversized import Max Size, generate mipmaps only where needed, and Release() runtime RenderTextures you no longer use. Whether a size is 'too big' depends on your platform's VRAM budget. ", "逐个确认是否需要当前分辨率/格式：纹理用压缩格式（ASTC / BCn）而非 RGBA32、调低过大的导入 Max Size、按需生成 mipmap，运行时不再用的 RenderTexture 及时 Release()。是否『过大』取决于目标平台显存预算。") +
                        L.Tr("(Editor-only render targets — Game/Scene view, editor windows, gizmos — are filtered out; an occasional editor-internal asset may still slip through.) ", "（编辑器专用渲染目标——Game/Scene 视图、编辑器窗口、Gizmo——已过滤；个别编辑器内部资源仍可能漏网。）") +
                        closingLine,
                ping: canLocate ? (System.Action)sb.SelectTopMemoryAssets : null));
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
                            L.Tr("Things to check: too many material/shader variants breaking batching, and SRP Batcher compatibility — run a project scan and PerfLint flags any SRP-incompatible materials it finds, ", "可排查：材质/Shader 变体过多导致批处理断裂、SRP Batcher 兼容性——运行一次项目扫描，PerfLint 会标出存在的 SRP 不兼容材质，") +
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
            if (avgTri < 300_000) return;

            Severity sev = avgTri >= 600_000 ? Severity.Critical : Severity.Warning;

            // Localize to the heaviest meshes loaded in the scene (Play-Mode-only attribution). Authored triangle counts —
            // not equal to the post-cull per-frame counter above, but they name the concrete models worth investigating first.
            var sb = r.SceneBatching;
            string breakdown = "";
            // One Locate per listed mesh — each reveals its own group of GameObjects (users expect to jump to each cluster separately, not just the heaviest one).
            List<Finding.LocateTarget> locateTargets = null;
            if (sb != null && sb.HasData && sb.TopTriangleMeshes != null && sb.TopTriangleMeshes.Count > 0)
            {
                breakdown = L.Tr("\n\nHeaviest geometry loaded in the scene (authored triangle counts; the per-frame counter above is post-cull, but these are the usual suspects):\n", "\n\n场景中最重的几何（建模三角面数；上面的每帧计数是剔除后的，而这些是首要排查对象）：\n");
                for (int i = 0; i < sb.TopTriangleMeshes.Count; i++)
                {
                    var m = sb.TopTriangleMeshes[i];
                    breakdown += m.InstanceCount > 1
                        ? L.Tr($"  • {m.MeshName}: {m.TrianglesPerInstance / 1000.0:0.0}K tris × {m.InstanceCount} = {m.TotalTriangles / 1000.0:0.0}K\n", $"  • {m.MeshName}：{m.TrianglesPerInstance / 1000.0:0.0}K 面 × {m.InstanceCount} = {m.TotalTriangles / 1000.0:0.0}K\n")
                        : L.Tr($"  • {m.MeshName}: {m.TotalTriangles / 1000.0:0.0}K tris\n", $"  • {m.MeshName}：{m.TotalTriangles / 1000.0:0.0}K 面\n");

                    if (sb.HasMeshExamples(i))
                    {
                        int rank = i; // capture per iteration for the closure
                        string label = m.InstanceCount > 1
                            ? L.Tr($"{m.MeshName} (×{m.InstanceCount})", $"{m.MeshName}（×{m.InstanceCount}）")
                            : m.MeshName;
                        (locateTargets ??= new List<Finding.LocateTarget>()).Add(
                            new Finding.LocateTarget(label, () => sb.SelectMeshExamples(rank)));
                    }
                }
            }

            findings.Add(new Finding(
                ruleId: "RUN.GPU002",
                domain: Domain.Runtime,
                severity: sev,
                title: L.Tr($"High runtime triangle count: avg {avgTri / 1000.0:0.0}K tris/frame (peak {tri.Max / 1000.0:0.0}K)", $"运行时三角面数偏高：平均 {avgTri / 1000.0:0.0}K 面/帧（峰值 {tri.Max / 1000.0:0.0}K）"),
                detail: L.Tr($"During sampling, an average of {avgTri / 1000.0:0.0}K triangles were rendered per frame — a direct indicator of GPU geometry-processing cost. ", $"采样期间平均每帧渲染 {avgTri / 1000.0:0.0}K 个三角面，这是 GPU 几何处理开销的直接指标。") +
                        breakdown + "\n\n" +
                        L.Tr("Optimization directions:\n", "优化方向：\n") +
                        L.Tr("1. Set up LOD Groups for complex models (3-4 levels recommended; distant models can drop below 10% of the triangle count);\n", "1. 为复杂模型设置 LOD Group（推荐 3~4 级，远距离模型面数可降至 10% 以下）；\n") +
                        L.Tr("2. Check visibility culling: is Occlusion Culling baked, and is the camera's Far Clip too large;\n", "2. 检查可见性剔除：Occlusion Culling 是否已烘焙，相机 Far Clip 是否过大；\n") +
                        L.Tr("3. For high-poly characters/foliage instanced in large numbers, consider GPU Instancing + simplified LODs.\n", "3. 高面数角色/植被若大量实例化，考虑 GPU Instancing + 简化 LOD。\n") +
                        L.Tr("Reference baselines: <50K for mobile, <200K for mid-range PC scenes, up to 500K+ on high-end PC/console.", "参考基准：移动端建议 <50K，PC 中档场景 <200K，高端 PC/Console 可至 500K+。"),
                locateTargets: locateTargets));
        }

        private static void BatchingEfficiency(RuntimeProfileResult r, List<Finding> findings)
        {
            var draw   = r.DrawCalls;
            var batch  = r.Batches;
            if (draw == null || !draw.HasData || batch == null || !batch.HasData) return;

            // Only evaluate when the Draw Call count is large enough for batching to provide a benefit; the ratio is meaningless when it is too small.
            if (draw.Avg < 50) return;

            double ratio = batch.Avg / draw.Avg; // The closer to 1, the fewer Draw Calls were merged into batches.
            if (ratio < 0.9) return;

            var sb = r.SceneBatching;
            bool isSrp = sb != null && sb.HasData && sb.IsSrp;
            var setPass = r.SetPassCalls;
            bool haveSetPass = setPass != null && setPass.HasData && draw.Avg > 0;
            double setPassRatio = haveSetPass ? setPass.Avg / draw.Avg : -1;
            // RUN.GPU004 is only emitted at this instancing threshold; gate every cross-reference to it so it never dangles.
            bool gpu004 = sb != null && sb.HasData && sb.InstancedMaterialRendererCount >= MaterialInstancingMinCount;

            if (isSrp)
            {
                // Under an SRP the SRP Batcher does NOT reduce the Batch count — Batches ≈ Draw Calls is *by design*. It lowers SetPass instead.
                // So a high Batches/Draw ratio is expected and not itself a problem; judging SRP projects by that ratio would false-positive on nearly every one of them.
                // The real signal is SetPass relative to Draw Calls. When SetPass is well below Draw Calls the Batcher is engaging → say nothing.
                if (haveSetPass && setPassRatio <= 0.6) return;

                string spText = haveSetPass
                    ? L.Tr($"SetPass is {setPass.Avg:0}/frame — {setPassRatio:P0} of the Draw Call count, so the SRP Batcher is barely merging render state. ", $"SetPass 为 {setPass.Avg:0}/帧——占 Draw Call 数的 {setPassRatio:P0}，说明 SRP Batcher 几乎没在合并渲染状态。")
                    : L.Tr("SetPass data isn't available this run, so Batcher health can't be confirmed from counters alone. ", "本次无 SetPass 数据，单凭计数器无法确认 Batcher 是否生效。");

                findings.Add(new Finding(
                    ruleId: "RUN.GPU003",
                    domain: Domain.Runtime,
                    severity: Severity.Info, // SRP-Batcher-not-engaging is real but rarely the top bottleneck — keep it Info, never alarm on the (expected) high Batch count.
                    title: haveSetPass
                        ? L.Tr($"SRP Batcher not engaging: SetPass {setPass.Avg:0}/frame stays high vs {draw.Avg:0} Draw Calls", $"SRP Batcher 未生效：SetPass {setPass.Avg:0}/帧 相对 {draw.Avg:0} Draw Call 仍偏高")
                        : L.Tr($"Under SRP the high Batch count ({batch.Avg:0}) is expected — judge batching by SetPass", $"SRP 下 Batch 数偏高（{batch.Avg:0}）属正常——批处理应看 SetPass"),
                    detail: L.Tr("Your project is on URP/HDRP. Under an SRP the Batches counter staying close to the Draw Call count is **expected** — the SRP Batcher lowers SetPass, not the Batch count — so the high Batches/Draw ratio is not itself the problem. ", "你的项目是 URP/HDRP。SRP 下 Batches 接近 Draw Call 数是**正常**的——SRP Batcher 降的是 SetPass，不是 Batch 数——所以高 Batches/Draw 比率本身不是问题。") +
                            spText + "\n\n" +
                            L.Tr("When SetPass stays high under SRP, the Batcher isn't engaging. Most likely causes, in order:\n", "SRP 下 SetPass 仍高，说明 Batcher 没生效，最可能的原因（按顺序）：\n") +
                            L.Tr("1. SRP Batcher-incompatible shaders (material properties not wrapped in a per-material CBUFFER) — run a project scan and PerfLint will flag any it finds;\n", "1. Shader 不兼容 SRP Batcher（材质属性未封装进 per-material CBUFFER）——运行一次项目扫描，PerfLint 会标出存在的问题；\n") +
                            (gpu004
                                ? L.Tr($"2. Runtime material instancing: {sb.InstancedMaterialRendererCount} objects cloned their material, and each clone drops out of the Batcher — see RUN.GPU004;\n", $"2. 运行时材质实例化：{sb.InstancedMaterialRendererCount} 个物体克隆了材质，每个克隆都会退出 Batcher——见 RUN.GPU004；\n")
                                : L.Tr("2. Materials with GPU Instancing enabled under SRP — Instancing kicks them out of the SRP Batcher (a scan flags these too);\n", "2. SRP 下材质开启了 GPU Instancing——Instancing 会把它们踢出 SRP Batcher（扫描同样会标出）；\n")) +
                            L.Tr("3. Frequent material / render-state switches (transparent objects, many render queues) interrupting the batch.", "3. 材质/渲染状态频繁切换（半透明、多渲染队列穿插）打断批次。")));
                return;
            }

            // Built-in pipeline: here Batches/Draw Calls genuinely reflects static/dynamic batching merging draws, so the ratio IS the signal.
            Severity sev = ratio >= 0.97 ? Severity.Warning : Severity.Info;

            // Use actual scene measurements to prune the checklist down to causes the data hasn't already ruled out.
            bool goodReuse = sb != null && sb.HasData && sb.MaterialReuseRatio >= 4.0;
            string sceneInsight = "";
            if (sb != null && sb.HasData)
            {
                sceneInsight =
                    L.Tr($"\n\nMeasured in this scene: {sb.RendererCount} mesh Renderers actually use {sb.UniqueMaterialCount} distinct material objects", $"\n\n本次场景实测：{sb.RendererCount} 个网格 Renderer 实际用了 {sb.UniqueMaterialCount} 个不同材质对象");
                if (sb.MaterialReuseRatio < 1.5)
                    sceneInsight += L.Tr(" — nearly one material per object; the lack of material reuse is the direct reason batching can't merge them.", "——几乎一物一材质，材质不复用是批处理无法合并的直接原因。");
                else if (goodReuse)
                    sceneInsight += L.Tr($" (reuse ratio {sb.MaterialReuseRatio:0.0}:1 — material reuse is healthy, so the cause lies in the batching settings below, not in material sharing).", $"（复用率 {sb.MaterialReuseRatio:0.0}:1——材质复用良好，原因应在下面的批处理设置，而非材质共享）。");
                else
                    sceneInsight += L.Tr($" (material reuse ratio {sb.MaterialReuseRatio:0.0}:1).", $"（材质复用率 {sb.MaterialReuseRatio:0.0}:1）。");
                if (gpu004)
                    sceneInsight += L.Tr($" Also detected {sb.InstancedMaterialRendererCount} objects instancing their materials at runtime — this is the primary root cause; see RUN.GPU004.", $" 并检测到 {sb.InstancedMaterialRendererCount} 个物体在运行时实例化了材质——这是首要根因，详见 RUN.GPU004。");
            }

            // Build the checklist, dropping items the measured data has already ruled out (so it reads as targeted diagnosis, not a generic dump).
            var items = new List<string>();
            if (!goodReuse) // when materials are already well-shared, runtime instancing isn't the cause — omit this item
                items.Add(L.Tr("Runtime material instancing: accessing renderer.material (instead of sharedMaterial) in a script clones the material, and each clone is a separate batch; switch to MaterialPropertyBlock or sharedMaterial", "运行时材质实例化：脚本里访问 renderer.material（而非 sharedMaterial）会克隆材质，每个克隆都是独立 batch；改用 MaterialPropertyBlock 或 sharedMaterial") +
                          (gpu004 ? L.Tr(" (see RUN.GPU004)", "（见 RUN.GPU004）") : ""));
            items.Add(L.Tr("Static batching: confirm that non-moving objects in the scene have Batching Static checked", "静态批处理：确认场景中不会移动的物体已勾选 Batching Static"));
            items.Add(L.Tr("Dynamic batching: meshes with <300 vertices can use it (Project Settings → Player → Dynamic Batching)", "动态批处理：顶点数 <300 的 Mesh 可启用（Project Settings → Player → Dynamic Batching）"));
            items.Add(L.Tr("GPU Instancing: for many objects sharing the same Mesh and Material (e.g. grass/bullets), enable \"Enable GPU Instancing\"", "GPU Instancing：大量同 Mesh 同 Material 的物体（如草地/子弹）启用 Enable GPU Instancing"));

            string checklist = L.Tr("Checklist (ordered by prevalence):\n", "排查清单（按常见程度排序）：\n");
            for (int i = 0; i < items.Count; i++)
                checklist += $"{i + 1}. {items[i]}\n";

            findings.Add(new Finding(
                ruleId: "RUN.GPU003",
                domain: Domain.Runtime,
                severity: sev,
                title: L.Tr($"Low batching efficiency: {batch.Avg:0} separate batches out of {draw.Avg:0} Draw Calls (ratio {ratio:P0})", $"批处理效率低：{draw.Avg:0} Draw Call 中 {batch.Avg:0} 个独立 Batch（比率 {ratio:P0}）"),
                detail: L.Tr($"Batches / Draw Calls ≈ {ratio:P0} on the Built-in pipeline, meaning batching is barely working — the vast majority of Draw Calls are not merged. ", $"Built-in 管线下 Batches / Draw Calls ≈ {ratio:P0}，说明批处理几乎未生效——绝大多数 Draw Call 都未被合并。") +
                        sceneInsight + "\n\n" + checklist));
        }

        /// <summary>Minimum runtime-instanced renderer count to raise RUN.GPU004; below this a few instances may be intentional (avoid noise). Shared so GPU003's cross-reference matches GPU004's actual emission.</summary>
        private const int MaterialInstancingMinCount = 5;

        /// <summary>
        /// Runtime material-instancing detection — **impossible via static scanning, unique to Play Mode**. Accessing renderer.material (not sharedMaterial)
        /// clones the material, the material name gains a "(Instance)" suffix, and every clone is a separate batch — a common true culprit behind "100% separate batches".
        /// Pinpoints the root cause directly to the specific GameObject (selectable with one click).
        /// </summary>
        private static void MaterialInstancing(RuntimeProfileResult r, List<Finding> findings)
        {
            var sb = r.SceneBatching;
            if (sb == null || !sb.HasData) return;
            if (sb.InstancedMaterialRendererCount < MaterialInstancingMinCount) return; // Threshold: a small number of instances may be intentional; avoid noise

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
                                L.Tr("Runtime has confirmed this script is a CPU hotspot. Click \"Line-level analysis\" — there are two cases: (1) if the cost is per-frame allocation, the main panel pinpoints the lines and AI Fix can patch them; ", "运行时已证实此脚本是 CPU 热点。点「逐行分析」——分两种情况：① 若开销来自每帧分配，主面板会逐行定位、AI Fix 可一键修复；") +
                                L.Tr("(2) if the analysis finds nothing, the hotspot is compute-bound (heavy loops/math, not allocation) — line-level GC analysis can't flag that; optimize the algorithm instead: do less per frame (throttle / spread across frames), cache results, or reduce iteration counts. The panel will tell you which case this is.", "② 若分析查不到东西，说明是计算密集型热点（重循环/数学，而非分配）——逐行 GC 分析标不出来，应改优化算法：每帧少干活（节流/分摊多帧）、缓存结果、减少迭代次数。面板会告诉你属于哪种情况。") + spikeDetail,
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
                                L.Tr("Turn on the \"Deep Profile\" toggle at the top of this panel (or in the Unity Profiler window), then re-enter Play Mode and sample; markers will refine to ClassName.Method(), ", "用本面板顶部的「Deep Profile」开关开启（或在 Unity Profiler 窗口开启）后重新进入 Play Mode 采样，marker 会细化为 ClassName.Method()，") +
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

        /// <summary>Extracts the method name from a marker display name: "LevelGenerator.AllVehiclesHavePaths()" → "AllVehiclesHavePaths". Returns null if none.
        /// Markers may carry a profiler sample-label suffix ("ScriptableRenderer.Execute: PC_High_Renderer") or args — strip anything from the first ':', ' ', or '(' so FindMethodLine can match the bare declaration.</summary>
        private static string MethodNameOf(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return null;
            int cut = marker.IndexOfAny(new[] { '(', ':', ' ' });
            string head = cut >= 0 ? marker.Substring(0, cut) : marker;
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
