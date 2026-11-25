using System.Text;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{
    /// <summary>
    /// Evaluates a static expression with the engine's own runtime evaluator, the only one: for
    /// <c>use-when</c>, the <c>select</c> of a static variable or parameter, and a shadow attribute.
    /// </summary>
    /// <remarks>
    /// A static expression may reference only static variables and the functions a static
    /// expression may call (XSLT 3.0 §9.7, <see cref="StaticFunctions"/>), so it needs none of the
    /// stylesheet being compiled: an empty stylesheet shell with the module's base URI is a complete
    /// context. Results are typed values — function items, nodes — not serializations. A
    /// hand-written interpreter over a subset of XPath, and a string matcher for shadow attributes,
    /// evaluated most static expressions before; this evaluated only what they could not (xslt#156),
    /// so a static expression could give two answers.
    /// </remarks>
    private object? EvaluateStaticViaRuntime(XQueryExpression expr, XElement context)
    {
        // The runtime implements every XSLT function, including the ones a static expression may
        // not call (XPST0017, W3C current-output-uri-901): they are rejected before evaluation.
        // Idempotent; the declaration pass hands over expressions whose prefixed variable names
        // are still unresolved, and an unresolved $d:method would bind to $method.
        ResolveExpressionNamespaces(expr, context);
        var guard = new StaticFunctionGuard();
        guard.Walk(expr);
        if (guard.Forbidden is { } forbidden)
            throw new XsltException($"XPST0017: Function '{forbidden}' is not available in a static expression");

        // The base URI of the expression's element (xml:base and the module's location), for
        // static-base-uri() and relative URIs: one shell serves every module of the load.
        var baseUri = XsltTransformEngine.UriString(ResolveEffectiveBaseUri(context));
        // Static expressions run while the stylesheet LOADS, so they obey the same resource
        // policy as the transformation: a use-when or static variable reading a file otherwise
        // leaked it before any runtime check (e.g. use-when="contains(unparsed-text(...), ...)").
        var runtime = _staticRuntime ??= new StaticRuntime(ResourcePolicy, XQueryModules, RegexMatchTimeout);
        // A prefixed function name (xpath:available-system-properties#0) is resolved against the
        // stylesheet's namespace table at run time; give the shell the declaring element's.
        runtime.Namespaces.Clear();
        foreach (var ns in context.AncestorsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
            runtime.Namespaces.TryAdd(prefix, ns.Value);
        }
        runtime.Context.PushScope();
        if (baseUri != null)
            runtime.Context.PushStaticBaseUri(baseUri);
        try
        {
            foreach (var (name, value) in _staticVariables)
                runtime.Context.SetVariable(name, value);
            return LargeStack.Run(() => runtime.Context.EvaluateAsync(expr).AsTask()).GetAwaiter().GetResult();
        }
        finally
        {
            if (baseUri != null)
                runtime.Context.PopStaticBaseUri();
            runtime.Context.PopScope();
        }
    }

    /// <summary>
    /// Evaluates a static expression — <c>use-when</c>, the <c>select</c> of a static variable or
    /// parameter, a shadow attribute — with the engine's runtime evaluator, the only one.
    /// </summary>
    /// <remarks>
    /// False only when the expression cannot be evaluated here and that is not an error in the
    /// stylesheet: a read the resource policy refused (its message would tell the stylesheet which
    /// file it named, XsltResourcePolicyEnforcementTests). Any other error is the stylesheet's: a
    /// dynamic error in a static expression is a static error with the same code (XSLT 3.0 §9.7).
    /// </remarks>
    private bool TryEvaluateStatic(XQueryExpression expr, XElement context, out object? value)
    {
        try
        {
            value = EvaluateStaticViaRuntime(expr, context);
            return true;
        }
        catch (Exception ex) when (IsResourceRefusal(ex))
        {
            value = null;
            return false;
        }
        catch (PhoenixmlDb.XQuery.Execution.XQueryRuntimeException ex) when (ex.ErrorCode is { Length: > 0 } code)
        {
            throw AsStaticError(code, ex, expr, context);
        }
        catch (PhoenixmlDb.XQuery.Functions.XQueryException ex) when (ex.ErrorCode is { Length: > 0 } code)
        {
            throw AsStaticError(code, ex, expr, context);
        }
        catch (XsltException ex) when (ex.ErrorCode == "XPST0008")
        {
            throw AsStaticError("XPST0008", ex, expr, context);
        }
    }

    // The error of a static expression as a static error of the stylesheet, at the declaring element.
    // An undeclared variable is named as the stylesheet writes it ($v:debug, not $debug), so a
    // prefixed reference can be told from an unprefixed one.
    private XsltException AsStaticError(string code, Exception ex, XQueryExpression expr, XElement context)
    {
        if (code == "XPST0008" && FreeVariable(expr) is { } name)
            return new XsltException($"XPST0008: Variable ${FormatVariableName(name)} is not declared in the static context", GetSourceLocation(context));
        var message = ex.Message.StartsWith(code, StringComparison.Ordinal) ? ex.Message : $"{code}: {ex.Message}";
        return new XsltException(message, GetSourceLocation(context));
    }

    // A variable the expression refers to that no static variable or parameter declares. (A variable
    // the expression binds itself, by let, for or a function parameter, has a declaration in it;
    // the runtime reported none, so the first unknown name is the one.)
    private QName? FreeVariable(XQueryExpression expr)
    {
        var finder = new VariableReferenceFinder();
        finder.Walk(expr);
        foreach (var name in finder.Names)
            if (!_staticVariables.ContainsKey(name) && !finder.Bound.Contains(name))
                return name;
        return null;
    }

    private sealed class VariableReferenceFinder : XQueryExpressionWalker
    {
        internal List<QName> Names { get; } = [];
        internal HashSet<QName> Bound { get; } = [];

        public override object? VisitVariableReference(VariableReference expr)
        {
            Names.Add(expr.Name);
            return base.VisitVariableReference(expr);
        }

        public override object? VisitFlworExpression(FlworExpression expr)
        {
            foreach (var clause in expr.Clauses)
            {
                if (clause is LetClause let)
                    foreach (var binding in let.Bindings)
                        Bound.Add(binding.Variable);
                else if (clause is ForClause @for)
                    foreach (var binding in @for.Bindings)
                    {
                        Bound.Add(binding.Variable);
                        if (binding.PositionalVariable is { } position)
                            Bound.Add(position);
                    }
            }
            return base.VisitFlworExpression(expr);
        }

        public override object? VisitQuantifiedExpression(QuantifiedExpression expr)
        {
            foreach (var binding in expr.Bindings)
                Bound.Add(binding.Variable);
            return base.VisitQuantifiedExpression(expr);
        }

        public override object? VisitInlineFunctionExpression(InlineFunctionExpression expr)
        {
            foreach (var parameter in expr.Parameters)
                Bound.Add(parameter.Name);
            return base.VisitInlineFunctionExpression(expr);
        }
    }

    // A refusal by the host's resource policy: a read it did not allow, or fn:transform turned off.
    private static bool IsResourceRefusal(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is PhoenixmlDb.XQuery.Security.ResourceAccessDeniedException or XsltException { ErrorCode: "FOXT0004" })
                return true;
        return false;
    }

    private bool TryEvaluateStaticViaRuntime(XQueryExpression expr, XElement context, out object? value)
    {
        try
        {
            value = EvaluateStaticViaRuntime(expr, context);
            return true;
        }
#pragma warning disable CA1031 // any evaluation failure means "not statically evaluable"; the caller reports it
        // …except XPST0017 from the guard, which is a static error in the stylesheet, not a
        // limitation of this processor, and must be reported as one.
        // …nor a resource limit: a regex that ran into its match timeout did not fail to be
        // static, it was stopped, and quietly carrying on would load the stylesheet with that
        // expression's value simply missing.
        // …nor an arithmetic or cast error (FOAR0001 for 1 div 0, FORG0001 for a bad cast): a
        // dynamic error in a static expression is a static error with the same code (XSLT 3.0
        // §9.7), not a sign that the expression could not be evaluated here. Only these families:
        // a read the resource policy refused must stay "not evaluable", or its message would tell
        // a use-when probe which file it named (XsltResourcePolicyEnforcementTests).
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not XsltException { ErrorCode: "XPST0017" }
            && !ResourceLimitFailure.Is(ex)
            && !IsStaticValueError(ex))
#pragma warning restore CA1031
        {
            value = null;
            return false;
        }
    }

    private static bool IsStaticValueError(Exception ex)
        => ex is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException { ErrorCode: { } code }
           && (code.StartsWith("FOAR", StringComparison.Ordinal) || code.StartsWith("FOCA", StringComparison.Ordinal)
               || code.StartsWith("FORG", StringComparison.Ordinal));

    private StaticRuntime? _staticRuntime;

    /// <summary>One evaluation context per base URI, reused across the compile.</summary>
    // One evaluation context for the static expressions of every module a parser reads: building
    // one builds the whole function library, and a stylesheet has modules by the dozen. Each
    // evaluation gives the shell the static base URI of its element (PushStaticBaseUri); the
    // readers of the shell's own base URI are instructions, which a static expression has none of.
    private sealed class StaticRuntime
    {
        internal DefaultXsltExecutionContext Context { get; }
        internal Dictionary<string, string> Namespaces { get; }

        internal StaticRuntime(PhoenixmlDb.XQuery.Security.ResourcePolicy? policy,
            IReadOnlyDictionary<string, List<string>>? xqueryModules, TimeSpan? regexMatchTimeout)
        {
            var shell = new XsltStylesheet { Version = "3.0" };
            Namespaces = shell.Namespaces;
            var store = new XdmInMemoryStore();
            var empty = new XmlDocument();
            empty.LoadXml("<empty/>");
            var source = XsltTransformEngine.ConvertToXdm(empty, store);
            Context = new DefaultXsltExecutionContext(
                shell, new TemplateIndex(shell), source, new StringBuilder(),
                // The host's XQuery modules too: a static variable may load one
                // (W3C load-xquery-module-004).
                new XsltTransformOptions
                {
                    ResourcePolicy = policy,
                    XQueryModules = xqueryModules,
                    // A static expression is evaluated by the same engine as a dynamic one,
                    // so it needs the same bound on a regex that backtracks catastrophically.
                    RegexMatchTimeout = regexMatchTimeout,
                }, store)
            {
                EvaluatesStaticExpressions = true,
            };
            // Static expressions have no focus (XSLT 3.0 §9.6).
            Context.PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
        }
    }

    /// <summary>
    /// Finds a call or a named reference to a function that a static expression may not call
    /// (<see cref="StaticFunctions"/>): checked on the expression before it is evaluated, so a call
    /// in a branch that is not taken is an error too.
    /// </summary>
    private sealed class StaticFunctionGuard : XQueryExpressionWalker
    {
        internal string? Forbidden { get; private set; }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (!StaticFunctions.IsAvailable(expr.Name)) Forbidden ??= FormatVariableName(expr.Name);
            return base.VisitFunctionCallExpression(expr);
        }

        public override object? VisitNamedFunctionRef(NamedFunctionRef expr)
        {
            if (!StaticFunctions.IsAvailable(expr.Name)) Forbidden ??= FormatVariableName(expr.Name);
            return base.VisitNamedFunctionRef(expr);
        }
    }
}
