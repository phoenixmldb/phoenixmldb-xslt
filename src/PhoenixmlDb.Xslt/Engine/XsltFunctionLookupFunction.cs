using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.XQuery.Execution;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// fn:function-lookup for XSLT. Inside the target expression of xsl:evaluate, the XSLT-defined
/// functions are not in the static context (XSLT 3.0 §10.4.1; spec bug 30049), so looking one up
/// there is XTDE3160, the same as naming it directly. Every other lookup goes to the XQuery function.
/// </summary>
internal sealed class XsltFunctionLookupFunction : XQueryFunction
{
    private readonly PhoenixmlDb.XQuery.Functions.FunctionLookupFunction _inner = new();

    // True for a function item (function-lookup#2) made outside xsl:evaluate: its static context
    // is the stylesheet's, so it finds the XSLT functions even when it is called inside the target.
    private readonly bool _madeOutsideEvaluate;

    public XsltFunctionLookupFunction() { }

    private XsltFunctionLookupFunction(bool madeOutsideEvaluate) => _madeOutsideEvaluate = madeOutsideEvaluate;

    public override QName Name => _inner.Name;
    public override XdmSequenceType ReturnType => _inner.ReturnType;
    public override IReadOnlyList<FunctionParameterDef> Parameters => _inner.Parameters;

    public override XQueryFunction BindCreationContext(PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => context is QueryExecutionContext { InsideXslEvaluate: false }
            ? new XsltFunctionLookupFunction(madeOutsideEvaluate: true)
            : this;

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (!_madeOutsideEvaluate
            && context is QueryExecutionContext { InsideXslEvaluate: true }
            && arguments.Count > 0
            && (arguments[0] is object?[] { Length: 1 } one ? one[0] : arguments[0]) is QName name
            && XsltEvaluateUnavailable.IsXsltOnlyFunction(name))
            throw new XsltException($"XTDE3160: The function {name.LocalName}() is not available within xsl:evaluate");
        return _inner.InvokeAsync(arguments, context);
    }
}

/// <summary>The functions that the static context of an xsl:evaluate target expression lacks.</summary>
internal static class XsltEvaluateUnavailable
{
    // The XSLT-defined functions in the fn namespace (XSLT 3.0 Appendix G.2) minus those that
    // F&O 3.1 also defines (json-to-xml, xml-to-json, collation-key), which §10.4.1 keeps.
    private static readonly HashSet<string> XsltOnlyFunctions = new(StringComparer.Ordinal)
    {
        "accumulator-after", "accumulator-before", "available-system-properties", "copy-of",
        "current", "current-group", "current-grouping-key", "current-merge-group",
        "current-merge-key", "current-output-uri", "document", "element-available",
        "function-available", "key", "regex-group", "snapshot", "stream-available",
        "system-property", "type-available", "unparsed-entity-public-id", "unparsed-entity-uri",
    };

    private const string FnUri = "http://www.w3.org/2005/xpath-functions";

    // A name in the target expression carries NamespaceId.Fn (or none, the default function
    // namespace). A QName made at run time by fn:QName or xs:QName carries its URI instead.
    internal static bool IsXsltOnlyFunction(QName name)
        => XsltOnlyFunctions.Contains(name.LocalName)
           && ((name.RuntimeNamespace ?? name.ExpandedNamespace) is { } uri
               ? uri == FnUri
               : name.Namespace == NamespaceId.None || name.Namespace == NamespaceId.Fn);
}
