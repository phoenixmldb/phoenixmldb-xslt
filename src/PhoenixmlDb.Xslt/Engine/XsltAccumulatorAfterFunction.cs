using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed class XsltAccumulatorAfterFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltAccumulatorAfterFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "accumulator-after");
    // An accumulator is declared with a sequence type (as="element()*", as="xs:string*"),
    // so its value is a SEQUENCE. Declaring OptionalItem here collapsed it into one boxed
    // item, and every consumer saw a single opaque array instead of N nodes.
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "name"), Type = XdmSequenceType.String }];

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var name = arguments[0]?.ToString() ?? throw new XsltException("XTDE3340: accumulator-after() requires a name argument");
        // XTDE3340: Validate the name is a valid EQName
        XsltFunctionValidation.ValidateQNameArgument(name, "XTDE3340", "accumulator-after");
        var accName = _context.ResolveAccumulatorName(name);

        // Use XQuery context item (from path step) if available, fall back to XSLT context item.
        // The XQuery context is preferred because in path expressions like $v/w/accumulator-after('x'),
        // the XSLT context is the template match node, not the path step's current node.
        var node = XQueryFocus.ItemOrNull(context) ?? _context.ContextItem
            ?? throw new XsltException("XTDE3340: accumulator-after() called with no context item");
        node = XsltFunctionValidation.RequireAccumulatorContextNode(node, "accumulator-after");

        // Check if the accumulator is applicable in the current mode. When it is not, the
        // error code depends on the tree: for the principal source document, the initial
        // mode governs which accumulators were evaluated, so an excluded accumulator is
        // XTDE3362 (§18.2); on any other tree the general XTDE3340 applies.
        if (!_context.IsAccumulatorApplicable(accName))
        {
            if (_context.IsPrincipalSourceNode(node))
                throw new XsltException($"XTDE3362: Accumulator '{name}' is not applicable to the principal source tree (not listed in use-accumulators for the initial mode)");
            throw new XsltException($"XTDE3340: Accumulator '{name}' is not applicable in the current mode (not listed in use-accumulators)");
        }

        // Lazily compute accumulators for this document if not yet done
        await _context.EnsureAccumulatorsComputedAsync(accName, node).ConfigureAwait(false);
        await _context.EnsureAccumulatorPhaseAsync(accName, node, isAfter: true).ConfigureAwait(false);

        // A declared accumulator with no value here was not applicable to this node's tree —
        // e.g. not in the merge source's use-accumulators — which is XTDE3362; XTDE3340 is for a
        // name that is not an accumulator at all (W3C merge-067).
        var values = _context.GetAccumulatorValue(accName, node, isAfter: true)
            ?? throw new XsltException(_context._stylesheet.Accumulators.ContainsKey(accName)
                ? $"XTDE3362: Accumulator '{name}' is not applicable to the tree containing the context node"
                : $"XTDE3340: No accumulator named '{name}' is available for the current node");

        // Re-throw deferred errors from accumulator evaluation
        if (values.after is AccumulatorDeferredError deferredError)
            deferredError.Rethrow();

        return values.after;
    }
}
