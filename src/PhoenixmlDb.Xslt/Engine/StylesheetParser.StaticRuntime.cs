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
    /// Evaluates a static expression with the engine's own runtime evaluator (xslt#156).
    /// </summary>
    /// <remarks>
    /// <see cref="EvaluateStaticExpression"/> is a hand-written interpreter over 19 of the XPath
    /// expression kinds. It stays the fast path; this runs only for what it cannot evaluate —
    /// inline function items, dynamic calls, partial application, <c>!</c>, <c>let</c>,
    /// <c>doc()</c> with paths and predicates.
    /// <para>
    /// A static expression may reference only static variables and the built-in functions
    /// (XSLT 3.0 §9.6), so it needs none of the stylesheet being compiled: an empty stylesheet
    /// shell with the module's base URI is a complete context. Results are typed values —
    /// function items, nodes — not serializations, which is what sank the earlier attempt
    /// through <c>XQueryFacade</c>, whose API returns strings.
    /// </para>
    /// </remarks>
    private object? EvaluateStaticViaRuntime(XQueryExpression expr, XElement context)
    {
        // The runtime implements every XSLT function, including the ones a static expression may
        // not call (XPST0017, W3C current-output-uri-901). The hand-written evaluator rejects them
        // by omission; here they have to be rejected by name.
        // Idempotent; the declaration pass hands over expressions whose prefixed variable names
        // are still unresolved, and an unresolved $d:method would bind to $method.
        ResolveExpressionNamespaces(expr, context);
        var guard = new StaticFunctionGuard();
        guard.Walk(expr);
        if (guard.Forbidden is { } forbidden)
            throw new XsltException($"XPST0017: Function '{forbidden}' is not available in a static expression");

        var baseUri = ResolveEffectiveBaseUri(context);
        // Static expressions run while the stylesheet LOADS, so they obey the same resource
        // policy as the transformation: a use-when or static variable reading a file otherwise
        // leaked it before any runtime check (e.g. use-when="contains(unparsed-text(...), ...)").
        _staticRuntime ??= new StaticRuntime(ResourcePolicy, XQueryModules);
        var runtime = _staticRuntime.For(baseUri);
        // A prefixed function name (xpath:available-system-properties#0) is resolved against the
        // stylesheet's namespace table at run time; give the shell the declaring element's.
        runtime.Namespaces.Clear();
        foreach (var ns in context.AncestorsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
            runtime.Namespaces.TryAdd(prefix, ns.Value);
        }
        runtime.Context.PushScope();
        try
        {
            foreach (var (name, value) in _staticVariables)
                runtime.Context.SetVariable(name, value);
            return LargeStack.Run(() => runtime.Context.EvaluateAsync(expr).AsTask()).GetAwaiter().GetResult();
        }
        finally
        {
            runtime.Context.PopScope();
        }
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
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not XsltException { ErrorCode: "XPST0017" })
#pragma warning restore CA1031
        {
            value = null;
            return false;
        }
    }

    private StaticRuntime? _staticRuntime;

    /// <summary>One evaluation context per base URI, reused across the compile.</summary>
    private sealed class StaticRuntime(PhoenixmlDb.XQuery.Security.ResourcePolicy? policy,
        IReadOnlyDictionary<string, List<string>>? xqueryModules)
    {
        private readonly Dictionary<string, Entry> _byBase = new(StringComparer.Ordinal);

        internal Entry For(Uri? baseUri)
        {
            var key = baseUri?.AbsoluteUri ?? "";
            if (!_byBase.TryGetValue(key, out var entry))
                _byBase[key] = entry = new Entry(baseUri, policy, xqueryModules);
            return entry;
        }

        internal sealed class Entry
        {
            internal DefaultXsltExecutionContext Context { get; }
            internal Dictionary<string, string> Namespaces { get; }

            internal Entry(Uri? baseUri, PhoenixmlDb.XQuery.Security.ResourcePolicy? policy,
                IReadOnlyDictionary<string, List<string>>? xqueryModules)
            {
                var shell = new XsltStylesheet { Version = "3.0", BaseUri = baseUri };
                Namespaces = shell.Namespaces;
                var store = new XdmInMemoryStore();
                var empty = new XmlDocument();
                empty.LoadXml("<empty/>");
                var source = XsltTransformEngine.ConvertToXdm(empty, store);
                Context = new DefaultXsltExecutionContext(
                    shell, new TemplateIndex(shell), source, new StringBuilder(),
                    // The host's XQuery modules too: a static variable may load one
                    // (W3C load-xquery-module-004).
                    new XsltTransformOptions { ResourcePolicy = policy, XQueryModules = xqueryModules }, store);
                // Static expressions have no focus (XSLT 3.0 §9.6).
                Context.PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
            }
        }
    }

    /// <summary>
    /// Finds a call to an XSLT function that is not available in a static expression
    /// (XSLT 3.0 §9.6: static expressions see no dynamic XSLT context).
    /// </summary>
    private sealed class StaticFunctionGuard : XQueryExpressionWalker
    {
        internal string? Forbidden { get; private set; }

        private static bool IsRuntimeOnly(QName name)
            => (name.Namespace == NamespaceId.None || name.Namespace == NamespaceId.Fn)
               && name.LocalName is "current" or "current-group" or "current-grouping-key"
                   or "current-merge-group" or "current-merge-key" or "current-output-uri"
                   or "regex-group" or "unparsed-entity-uri" or "unparsed-entity-public-id"
                   or "accumulator-before" or "accumulator-after" or "snapshot" or "copy-of"
                   or "key" or "document";

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (IsRuntimeOnly(expr.Name)) Forbidden ??= expr.Name.LocalName;
            return base.VisitFunctionCallExpression(expr);
        }

        public override object? VisitNamedFunctionRef(NamedFunctionRef expr)
        {
            if (IsRuntimeOnly(expr.Name)) Forbidden ??= expr.Name.LocalName;
            return base.VisitNamedFunctionRef(expr);
        }
    }
}
