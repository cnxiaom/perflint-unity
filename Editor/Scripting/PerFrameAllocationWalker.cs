#if PERFLINT_ROSLYN
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PerfLint.Core;
using PerfLint.L10n;

namespace PerfLint.Scripting
{
    /// <summary>A single script-layer issue (produced by syntax analysis, includes a line number).</summary>
    internal readonly struct ScriptIssue
    {
        public readonly string RuleId;
        public readonly Severity Severity;
        public readonly string Title;
        public readonly string Detail;
        public readonly int Line; // 1-based
        public readonly bool AllowAiFix; // Report-only rules (e.g. GC004 memory leak) set this false: the fix is a behavioral decision and cannot be mechanically auto-applied

        public ScriptIssue(string ruleId, Severity severity, string title, string detail, int line, bool allowAiFix = true)
        {
            RuleId = ruleId;
            Severity = severity;
            Title = title;
            Detail = detail;
            Line = line;
            AllowAiFix = allowAiFix;
        }
    }

    /// <summary>
    /// Syntax-only (no semantic/type resolution) detection of per-frame allocations and expensive calls. Syntax
    /// analysis is fast, stable, and needs no full-project compilation; the trade-off is that it cannot precisely
    /// determine types—so the rules are tuned for "low false positives" and use method-signature heuristics to
    /// identify Unity per-frame messages.
    ///
    /// Detection scope: inside Update / FixedUpdate / LateUpdate (the standard message signature: no parameters,
    /// returns void) method bodies:
    ///   PERF.UPD001 — GetComponent* / FindObjectOfType* / GameObject.Find
    ///   PERF.UPD002 — Resources.Load
    ///   PERF.UPD003 — Camera.main
    ///   PERF.GC001  — String interpolation / + concatenation involving a string literal
    ///   PERF.GC002  — LINQ calls (only when the file has using System.Linq)
    ///   PERF.GC003  — new WaitForSeconds (constant duration only; recommend caching for reuse; variable/Random not reported; Info)
    ///   PERF.GC004  — Per-frame Add/Insert/Enqueue(new ...) into a collection (likely continuous accumulation/memory leak; Info, report-only, not auto-fixed)
    ///   PERF.CPU001 — Per-frame method with a heavy recomputation loop bounded by a large constant (line-level CPU hotspot location; Warning, report-only, not auto-fixed)
    ///   PERF.RND001 — Per-frame read of Renderer.material (clones a material instance + breaks batching; Warning, report-only, not auto-fixed)
    ///
    /// False-positive suppression (two kinds; the GC004 accumulation type is not exempted):
    ///   1) Discrete-input guard (exempts UPD001/002/003, GC001/002, CPU001, RND001): the call is inside the then
    ///      branch of an if whose condition references a one-shot input API such as
    ///      GetKeyDown/GetMouseButtonDown/GetButtonDown (...Down/...Up)
    ///      → it runs only on the frame of the press/release, not every frame.
    ///   2) Cache initialization (exempts UPD001/003, GC003's new WaitForSeconds, RND001): the call/new is the
    ///      right-hand side of X ??= … or if(X==null) X=… → it runs only once on first execution. This also
    ///      eliminates the self-loop where, after an "AI fix caches Camera.main/GetComponent/new WaitForSeconds as
    ///      (_x ??= …)", the original expression still remains and gets flagged again by the rule.
    /// </summary>
    internal sealed class PerFrameAllocationWalker : CSharpSyntaxWalker
    {
        private static readonly HashSet<string> PerFrameMethods = new HashSet<string>
        {
            "Update", "FixedUpdate", "LateUpdate"
        };

        // Heavy-loop threshold: a per-frame for loop with a fixed iteration count >= this value is treated as a CPU
        // hotspot candidate. A high value is chosen to suppress false positives (a normal loop over a few hundred
        // entities won't trip it; this targets "millions of wasted compute cycles spinning").
        private const long HeavyLoopIterations = 100_000;

        private static readonly HashSet<string> ExpensiveCalls = new HashSet<string>
        {
            "GetComponent", "GetComponentInChildren", "GetComponentInParent",
            "GetComponents", "GetComponentsInChildren", "GetComponentsInParent",
            "FindObjectOfType", "FindObjectsOfType",
            "FindFirstObjectByType", "FindAnyObjectByType", "FindObjectsByType"
        };

        // Discrete-input APIs: they return true on "the one frame" of the press/release, so the if branch wrapping
        // them does not run every frame. Therefore per-frame rules inside that branch (GetComponent/Camera.main/
        // string concatenation/heavy loops, etc.) should be exempted (to suppress false positives).
        private static readonly HashSet<string> DiscreteInputCalls = new HashSet<string>
        {
            "GetMouseButtonDown", "GetMouseButtonUp",
            "GetKeyDown", "GetKeyUp",
            "GetButtonDown", "GetButtonUp"
        };

        // Collection "accumulation" methods: when called every frame with a newly-allocated object as the argument,
        // likely continuous accumulation (toward a memory leak).
        private static readonly HashSet<string> AccumulatingAdds = new HashSet<string>
        {
            "Add", "AddRange", "Insert", "Enqueue", "Push", "AddFirst", "AddLast"
        };

        private static readonly HashSet<string> LinqMethods = new HashSet<string>
        {
            "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy",
            "ToList", "ToArray", "ToDictionary", "First", "FirstOrDefault", "Last",
            "LastOrDefault", "Single", "SingleOrDefault", "Any", "All", "Count",
            "Sum", "Min", "Max", "Average", "Aggregate", "GroupBy", "Distinct", "Concat", "Union"
        };

        private readonly List<ScriptIssue> _issues = new List<ScriptIssue>();
        private readonly bool _hasLinqUsing;
        private string _currentMethod; // null = not inside a per-frame method

        public PerFrameAllocationWalker(bool hasLinqUsing) : base(SyntaxWalkerDepth.Node)
        {
            _hasLinqUsing = hasLinqUsing;
        }

        public IReadOnlyList<ScriptIssue> Issues => _issues;
        private bool InPerFrame => _currentMethod != null;

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            bool isPerFrame =
                PerFrameMethods.Contains(node.Identifier.Text)
                && node.ParameterList.Parameters.Count == 0
                && node.ReturnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);

            string prev = _currentMethod;
            if (isPerFrame) _currentMethod = node.Identifier.Text;
            base.VisitMethodDeclaration(node);
            _currentMethod = prev;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (InPerFrame)
            {
                string name = GetInvokedName(node.Expression, out string target);

                // A call inside a discrete-input guard, or inside cache initialization (if(_x==null) _x=call / _x??=call),
                // does not run every frame → exempt UPD001/UPD002/GC002 (per-frame rules). GC004 (accumulation) is not
                // exempted: even if one item is added per click, the collection may still grow unbounded.
                // The cache-init exemption also eliminates the self-loop where an "AI-generated _x=Camera.main/GetComponent
                // initialization line" gets flagged again by the rule.
                bool guarded = InDiscreteInputGuard(node) || InCacheInitGuard(node);

                if (!guarded && name != null && ExpensiveCalls.Contains(name))
                    Add("PERF.UPD001", Severity.Warning, L.Tr($"Per-frame call to {name} in {_currentMethod}()", $"{_currentMethod}() 内每帧调用 {name}"),
                        L.Tr($"Calling {name}(...) every frame in {_currentMethod}() is expensive. Cache the result in Awake/Start to avoid the per-frame lookup.", $"在 {_currentMethod}() 里每帧调用 {name}(...) 开销大。建议在 Awake/Start 缓存结果，避免每帧查找。"), node);
                else if (!guarded && name == "Find" && target == "GameObject")
                    Add("PERF.UPD001", Severity.Warning, L.Tr($"GameObject.Find called in {_currentMethod}()", $"{_currentMethod}() 内调用 GameObject.Find"),
                        L.Tr("GameObject.Find scans the entire scene every frame and is expensive. Cache the reference during initialization.", "GameObject.Find 每帧遍历整个场景，开销大。建议在初始化时缓存引用。"), node);
                else if (!guarded && name == "Load" && target == "Resources")
                    Add("PERF.UPD002", Severity.Warning, L.Tr($"Resources.Load called in {_currentMethod}()", $"{_currentMethod}() 内调用 Resources.Load"),
                        L.Tr("Per-frame Resources.Load repeats lookups/IO. Preload and cache the asset reference.", "每帧 Resources.Load 反复做查找/IO。建议预加载并缓存资源引用。"), node);
                else if (!guarded && _hasLinqUsing && name != null && LinqMethods.Contains(name))
                    Add("PERF.GC002", Severity.Warning, L.Tr($"LINQ used in {_currentMethod}() ({name})", $"{_currentMethod}() 内使用 LINQ（{name}）"),
                        L.Tr($"LINQ ({name}) allocates an enumerator/closure every frame, creating GC pressure. On hot paths, rewrite it as a hand-written for/foreach loop.", $"LINQ（{name}）每帧分配枚举器/闭包，产生 GC 压力。热路径建议改写为手写 for/foreach 循环。"), node);

                // PERF.GC004: adding a newly-allocated object to a collection every frame → likely continuous
                // accumulation (toward a memory leak). Restricted to "member call + argument containing new" to suppress
                // false positives (per-frame new into a container rarely has a legitimate use); but pure syntax cannot
                // see the paired Clear/Remove, so this is an Info report-only rule with wording that asks for confirmation,
                // and no auto-fix is attached (when to clean up is a logic decision).
                if (name != null && AccumulatingAdds.Contains(name)
                    && node.Expression is MemberAccessExpressionSyntax
                    && ArgumentHasNew(node.ArgumentList))
                {
                    Add("PERF.GC004", Severity.Info, L.Tr($"New objects added to a collection every frame in {_currentMethod}() (likely accumulation)", $"{_currentMethod}() 内每帧向集合添加新对象（疑似累积）"),
                        L.Tr($"{name}(new ...) runs every frame in {_currentMethod}(), continuously adding newly created objects to a collection.", $"在 {_currentMethod}() 里每帧执行 {name}(new ...)，不断把新建对象加入集合。") +
                        L.Tr("If the collection has no matching Clear/Remove, it will **accumulate indefinitely and leak memory** (can be confirmed at runtime by RUN.MEM002).", "若该集合没有配对的 Clear/Remove，会**持续累积导致内存泄漏**（运行时可由 RUN.MEM002 证实）。") +
                        L.Tr("Check: is this collection cleared somewhere? If it's a per-frame rebuilt temporary cache, make sure there is a matching Clear; ", "请确认：这个集合是否在某处被清理？若是每帧重建的临时缓存，确保有对应的 Clear；") +
                        L.Tr("if it's a long-lived collection, consider a capacity cap / object pool / periodic cleanup.", "若是长期收集，考虑容量上限 / 对象池 / 定期清理。") +
                        L.Tr("**No auto-fix**—whether and when to clear depends on your logic; use Explain to let the AI help you decide.", "**不自动修复**——是否清理、何时清理取决于你的逻辑意图，可用 Explain 让 AI 帮你判断。"),
                        node, allowAiFix: false);
                }
            }
            base.VisitInvocationExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (InPerFrame && !InDiscreteInputGuard(node) && !InCacheInitGuard(node)
                && node.Name.Identifier.Text == "main"
                && node.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Camera")
            {
                Add("PERF.UPD003", Severity.Warning, L.Tr($"Camera.main accessed in {_currentMethod}()", $"{_currentMethod}() 内访问 Camera.main"),
                    L.Tr("Camera.main internally runs FindGameObjectsWithTag, so per-frame access is both slow and allocating. Cache the Camera reference.", "Camera.main 内部会执行 FindGameObjectsWithTag，每帧调用既慢又分配。建议缓存 Camera 引用。"), node);
            }

            // PERF.RND001 — reading Renderer.material every frame clones a material instance and breaks batching (the setter does not clone, so exclude assignment left-hand sides)
            if (InPerFrame && !InDiscreteInputGuard(node) && !InCacheInitGuard(node)
                && (node.Name.Identifier.Text == "material" || node.Name.Identifier.Text == "materials")
                && !(node.Parent is AssignmentExpressionSyntax asn && asn.Left == node)
                && LooksLikeRendererReceiver(node.Expression))
            {
                Add("PERF.RND001", Severity.Warning, L.Tr($"Renderer.material accessed in {_currentMethod}()", $"{_currentMethod}() 内访问 Renderer.material"),
                    L.Tr($"Reading Renderer.material every frame in {_currentMethod}() clones a material instance, which both breaks SRP/static batching ", $"在 {_currentMethod}() 里每帧读取 Renderer.material 会克隆出一份材质实例，既打断 SRP/静态合批、") +
                    L.Tr("and leaks a new material instance every frame (can be confirmed at runtime by RUN.GPU004).", "又每帧产生材质实例泄漏（运行时可由 RUN.GPU004 证实）。") +
                    L.Tr("For shared/global read or change, use sharedMaterial; to change a single object's properties without breaking batching, use MaterialPropertyBlock.", "只读共享/想全局改用 sharedMaterial；只想改单个物体的属性又不破合批，用 MaterialPropertyBlock。") +
                    L.Tr("**No auto-fix**—sharedMaterial and material have different semantics (sharedMaterial affects every object using that material), so the fix depends on your intent; use Explain to let the AI suggest a targeted rewrite.", "**不自动修复**——sharedMaterial 与 material 语义不同（会影响所有共用该材质的物体），改法取决于你的意图，可用 Explain 让 AI 给出针对性改写。"),
                    node, allowAiFix: false);
            }
            base.VisitMemberAccessExpression(node);
        }

        public override void VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
        {
            if (InPerFrame && !InDiscreteInputGuard(node))
                Add("PERF.GC001", Severity.Warning, L.Tr($"String interpolation in {_currentMethod}()", $"{_currentMethod}() 内字符串插值"),
                    L.Tr("String interpolation ($\"...\") executed every frame allocates a new string, causing continuous GC. Update only when the value changes, or use a cache/StringBuilder.", "每帧执行字符串插值（$\"...\"）会分配新字符串，产生持续 GC。建议仅在数值变化时更新，或使用缓存/StringBuilder。") +
                    L.Tr("(Report-only: eliminating this allocation requires introducing state or changing behavior—a judgment call, so no mechanical auto-fix is offered; decide manually whether to throttle/remove/rewrite.)", "（报告类：消除该分配需引入状态或改变行为，属判断题，不提供机械自动修复——请人工决定降频/删除/改写。）"),
                    node, allowAiFix: false);
            base.VisitInterpolatedStringExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            // Report only once at the outermost + expression to avoid double-counting a chain like a + b + "x".
            bool topMostAdd = node.IsKind(SyntaxKind.AddExpression)
                              && !(node.Parent is BinaryExpressionSyntax pb && pb.IsKind(SyntaxKind.AddExpression));

            if (InPerFrame && topMostAdd && InvolvesStringLiteral(node) && !InDiscreteInputGuard(node))
                Add("PERF.GC001", Severity.Warning, L.Tr($"String concatenation in {_currentMethod}()", $"{_currentMethod}() 内字符串拼接"),
                    L.Tr("String + concatenation every frame allocates a new string, causing continuous GC. Update only when the value changes, or use a cache/StringBuilder.", "每帧字符串 + 拼接会分配新字符串，产生持续 GC。建议仅在变化时更新，或使用缓存/StringBuilder。") +
                    L.Tr("(Report-only: eliminating this allocation requires introducing state or changing behavior—a judgment call, so no mechanical auto-fix is offered; decide manually whether to throttle/remove/rewrite.)", "（报告类：消除该分配需引入状态或改变行为，属判断题，不提供机械自动修复——请人工决定降频/删除/改写。）"),
                    node, allowAiFix: false);

            base.VisitBinaryExpression(node);
        }

        public override void VisitForStatement(ForStatementSyntax node)
        {
            // PERF.CPU001: a for loop with a fixed large count inside a per-frame method → CPU hotspot candidate. This is
            // a line-level rule beyond the GC rules, dedicated to "pure computation", complementing the runtime
            // RUN.HOT001 (which confirms a script is a CPU hotspot): HOT001 says "this script is slow", CPU001 pinpoints
            // "the slowness is in this loop".
            if (InPerFrame && !InDiscreteInputGuard(node) && TryGetLargeLiteralBound(node, out long iterations))
                Add("PERF.CPU001", Severity.Warning,
                    L.Tr($"Per-frame recomputation loop in {_currentMethod}() (~{iterations:N0} iterations)", $"{_currentMethod}() 内每帧重计算循环（约 {iterations:N0} 次迭代）"),
                    L.Tr($"{_currentMethod}() contains a loop with a fixed count of ~{iterations:N0} iterations; running it every frame continuously consumes CPU", $"在 {_currentMethod}() 里有一个固定迭代约 {iterations:N0} 次的循环，每帧执行会持续吃 CPU") +
                    L.Tr(" (at runtime, RUN.HOT001 can confirm this script as a CPU hotspot).", "（运行时可由 RUN.HOT001 把该脚本证实为 CPU 热点）。") +
                    L.Tr("If the result can be cached/incrementally updated, amortized across frames, or computed at lower precision/iteration count, frame time can drop noticeably.", "若计算结果可缓存/增量更新、可分摊到多帧、或可降低精度/迭代数，能明显压低帧时间。") +
                    L.Tr("**No auto-fix**—whether the loop is necessary and whether its algorithmic complexity can be reduced depend on your intent; use Explain to let the AI suggest a targeted optimization.", "**不自动修复**——循环是否必要、能否降算法复杂度取决于你的意图，可用 Explain 让 AI 给出针对性优化。"),
                    node, allowAiFix: false);
            base.VisitForStatement(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            string typeName = (node.Type as IdentifierNameSyntax)?.Identifier.Text
                              ?? (node.Type as GenericNameSyntax)?.Identifier.Text;
            // Only report new WaitForSeconds inside executable code (method/coroutine body)—it allocates on every
            // execution; a new in a field/property initializer is the correct "cache as a field" pattern and is not reported.
            // Cache initialization (_w ??= new WaitForSeconds / if(_w==null) _w=new...) allocates only once on first use → exempt,
            // otherwise after an AI fix caches it as (_w ??= new WaitForSeconds(..)), the new literal still remains and gets
            // flagged again into a self-loop.
            // Report only [constant durations] (e.g. 0.5f): a variable / Random.Range / method call may differ each time
            // and cannot be cached (caching would freeze the first value and change behavior)—flagging it only yields AI
            // "no change needed" noise, so skip it already at the detection stage.
            if (typeName == "WaitForSeconds" && InExecutableCode(node) && !InCacheInitGuard(node) && IsCacheableConstDuration(node))
                Add("PERF.GC003", Severity.Info, L.Tr("new WaitForSeconds not cached", "new WaitForSeconds 未缓存"),
                    L.Tr("new WaitForSeconds inside a method/coroutine body allocates on every execution, causing GC (especially in coroutines).", "在方法/协程体内 new WaitForSeconds 会每次执行时分配，产生 GC（协程里尤甚）。") +
                    L.Tr("Cache the WaitForSeconds instance as a field—create it once and reuse it.", "建议把 WaitForSeconds 实例缓存为字段，一次创建、反复复用。"), node);
            base.VisitObjectCreationExpression(node);
        }

        /// <summary>
        /// Whether the WaitForSeconds duration is a "definitely cacheable" constant—only numeric literals count (e.g. 0.5f, -1f).
        /// A variable / Random.Range / method call may differ each time and cannot be cached; flagging it is just noise, so return false and skip it at the detection stage.
        /// The syntax tree cannot tell whether a const field is constant, so we prefer to under-report (harmless) rather than report variable durations (noise + inducing bad fixes).
        /// </summary>
        private static bool IsCacheableConstDuration(ObjectCreationExpressionSyntax node)
        {
            if (node.ArgumentList == null || node.ArgumentList.Arguments.Count != 1) return false;
            var expr = node.ArgumentList.Arguments[0].Expression;
            if (expr is PrefixUnaryExpressionSyntax u) expr = u.Operand; // allow signed literals
            return expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NumericLiteralExpression);
        }

        /// <summary>
        /// Whether node is inside the if branch of a "discrete-input guard": walking up (without leaving the current method)
        /// there exists an if whose condition references a one-shot input API such as GetKeyDown, and node is in that if's
        /// then branch (not else). Such a branch is entered only on the frame of the press/release, so calls inside the
        /// block do not run every frame → per-frame rules are exempted.
        /// </summary>
        private static bool InDiscreteInputGuard(SyntaxNode node)
        {
            for (var p = node.Parent; p != null; p = p.Parent)
            {
                if (p is MethodDeclarationSyntax) break; // do not leave the method
                if (p is IfStatementSyntax ifs
                    && ifs.Statement != null && ifs.Statement.Span.Contains(node.Span) // in the then branch (else still runs every frame)
                    && ConditionHasDiscreteInput(ifs.Condition))
                    return true;
            }
            return false;
        }

        private static bool ConditionHasDiscreteInput(ExpressionSyntax cond)
        {
            if (cond == null) return false;
            foreach (var n in cond.DescendantNodesAndSelf())
            {
                if (n is InvocationExpressionSyntax inv)
                {
                    string name = GetInvokedName(inv.Expression, out _);
                    if (name != null && DiscreteInputCalls.Contains(name)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether node is the right-hand side of a "cache initialization" assignment: X ??= call; or if (X == null) X = call; (same X).
        /// Such an assignment runs only once on first execution, not every frame → per-frame rules are exempted. This also eliminates the self-loop where an AI-generated cache-init line gets flagged again.
        /// </summary>
        private static bool InCacheInitGuard(SyntaxNode node)
        {
            for (var p = node.Parent; p != null; p = p.Parent)
            {
                if (p is MethodDeclarationSyntax) break; // do not leave the method
                if (p is AssignmentExpressionSyntax asn && asn.Right != null && asn.Right.Span.Contains(node.Span))
                {
                    // X ??= call: itself a one-time initialization.
                    if (asn.IsKind(SyntaxKind.CoalesceAssignmentExpression) && asn.Left is IdentifierNameSyntax)
                        return true;
                    // X = call: requires an enclosing if (X == null) guard with a matching X name.
                    if (asn.IsKind(SyntaxKind.SimpleAssignmentExpression) && asn.Left is IdentifierNameSyntax lhs)
                        return InNullCheckGuard(asn, lhs.Identifier.Text);
                    return false;
                }
            }
            return false;
        }

        /// <summary>Whether from is inside the then branch of some if (name == null) / if (null == name) (without leaving the method).</summary>
        private static bool InNullCheckGuard(SyntaxNode from, string name)
        {
            for (var p = from.Parent; p != null; p = p.Parent)
            {
                if (p is MethodDeclarationSyntax) break;
                if (p is IfStatementSyntax ifs
                    && ifs.Statement != null && ifs.Statement.Span.Contains(from.Span)
                    && IsNullCheckOf(ifs.Condition, name))
                    return true;
            }
            return false;
        }

        private static bool IsNullCheckOf(ExpressionSyntax cond, string name)
        {
            if (cond is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.EqualsExpression))
                return (IsNamed(b.Left, name) && IsNullLiteral(b.Right))
                    || (IsNamed(b.Right, name) && IsNullLiteral(b.Left));
            return false;
        }

        private static bool IsNamed(ExpressionSyntax e, string name) =>
            e is IdentifierNameSyntax id && id.Identifier.Text == name;

        private static bool IsNullLiteral(ExpressionSyntax e) =>
            e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression);

        /// <summary>
        /// Whether node is inside executable code (a method/coroutine/accessor body): walking up, encountering a statement first means yes;
        /// encountering a member declaration first (field/property initializer) means no.
        /// </summary>
        private static bool InExecutableCode(SyntaxNode node)
        {
            for (var p = node.Parent; p != null; p = p.Parent)
            {
                if (p is StatementSyntax) return true;
                if (p is MemberDeclarationSyntax) return false; // field/property initializer, etc.
            }
            return false;
        }

        /// <summary>
        /// Recognizes a large constant upper bound in a for loop condition: for (...; i &lt; LITERAL; ...) or i &lt;= LITERAL.
        /// A pure syntax heuristic—only numeric-literal upper bounds count (variable/field bounds cannot be statically bounded, so they are skipped to avoid false positives),
        /// and when matched with iterations &gt;= HeavyLoopIterations it returns the estimated iteration count.
        /// </summary>
        private static bool TryGetLargeLiteralBound(ForStatementSyntax node, out long iterations)
        {
            iterations = 0;
            if (node.Condition is BinaryExpressionSyntax cond
                && (cond.IsKind(SyntaxKind.LessThanExpression) || cond.IsKind(SyntaxKind.LessThanOrEqualExpression))
                && cond.Right is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.NumericLiteralExpression)
                && lit.Token.Value != null)
            {
                try { iterations = System.Convert.ToInt64(lit.Token.Value); }
                catch { return false; } // skip non-integer literals (e.g. floats)
                if (cond.IsKind(SyntaxKind.LessThanOrEqualExpression)) iterations += 1;
                return iterations >= HeavyLoopIterations;
            }
            return false;
        }

        /// <summary>Whether the argument list contains a new expression: new Foo() / new byte[N] / new[]{...} (array and object creation are different syntax nodes, both must be covered).</summary>
        private static bool ArgumentHasNew(ArgumentListSyntax args)
        {
            if (args == null) return false;
            foreach (var a in args.Arguments)
            {
                switch (a.Expression)
                {
                    case ObjectCreationExpressionSyntax _:         // new Foo()
                    case ArrayCreationExpressionSyntax _:          // new byte[256 * 1024]
                    case ImplicitArrayCreationExpressionSyntax _:  // new[] { ... }
                        return true;
                }
            }
            return false;
        }

        private static bool InvolvesStringLiteral(BinaryExpressionSyntax node) =>
            IsStringLiteralOrInterp(node.Left) || IsStringLiteralOrInterp(node.Right);

        private static bool IsStringLiteralOrInterp(ExpressionSyntax e)
        {
            if (e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) return true;
            if (e is InterpolatedStringExpressionSyntax) return true;
            if (e is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.AddExpression)) return InvolvesStringLiteral(be);
            return false;
        }

        private static string GetInvokedName(ExpressionSyntax expr, out string target)
        {
            target = null;
            switch (expr)
            {
                case MemberAccessExpressionSyntax ma:
                    target = (ma.Expression as IdentifierNameSyntax)?.Identifier.Text;
                    return ma.Name.Identifier.Text;
                case GenericNameSyntax gn:
                    return gn.Identifier.Text;
                case IdentifierNameSyntax idn:
                    return idn.Identifier.Text;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether the receiver expression "looks like a Renderer" (pure syntax heuristic, no type resolution, tuned for low false positives):
        ///   (1) GetComponent&lt;XxxRenderer&gt;() (the generic type-argument name ends with Renderer)—high confidence;
        ///   (2) an identifier/member name (case-insensitive) containing "render", e.g. renderer / meshRenderer / _spriteRenderer—medium confidence.
        /// This avoids false positives from same-named properties such as collider.material (PhysicMaterial); the cost is that overly short abbreviations like rend are missed (acceptable).
        /// </summary>
        private static bool LooksLikeRendererReceiver(ExpressionSyntax receiver)
        {
            switch (receiver)
            {
                case InvocationExpressionSyntax inv:
                    return IsGetComponentRenderer(inv);
                case IdentifierNameSyntax id:
                    return NameSuggestsRenderer(id.Identifier.Text);
                case MemberAccessExpressionSyntax ma:
                    return NameSuggestsRenderer(ma.Name.Identifier.Text);
                default:
                    return false;
            }
        }

        /// <summary>Whether the receiver is GetComponent&lt;XxxRenderer&gt;() (including InChildren/InParent; the generic argument ends with Renderer).</summary>
        private static bool IsGetComponentRenderer(InvocationExpressionSyntax inv)
        {
            GenericNameSyntax generic = inv.Expression as GenericNameSyntax;          // GetComponent<T>()
            if (generic == null && inv.Expression is MemberAccessExpressionSyntax m)  // x.GetComponent<T>()
                generic = m.Name as GenericNameSyntax;
            if (generic == null) return false;

            string method = generic.Identifier.Text;
            if (method != "GetComponent" && method != "GetComponentInChildren" && method != "GetComponentInParent")
                return false;

            var args = generic.TypeArgumentList.Arguments;
            if (args.Count != 1) return false;
            string typeName = (args[0] as IdentifierNameSyntax)?.Identifier.Text
                              ?? (args[0] as GenericNameSyntax)?.Identifier.Text;
            return typeName != null && typeName.EndsWith("Renderer", System.StringComparison.Ordinal);
        }

        private static bool NameSuggestsRenderer(string name) =>
            !string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains("render");

        private void Add(string ruleId, Severity sev, string title, string detail, SyntaxNode node, bool allowAiFix = true)
        {
            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _issues.Add(new ScriptIssue(ruleId, sev, title, detail, line, allowAiFix));
        }
    }
}
#endif
