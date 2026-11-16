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

    // The transform, to see the stylesheet functions as the calling package sees them.
    private readonly DefaultXsltExecutionContext? _transform;

    public XsltFunctionLookupFunction(DefaultXsltExecutionContext? transform = null) => _transform = transform;

    private XsltFunctionLookupFunction(DefaultXsltExecutionContext? transform, bool madeOutsideEvaluate)
    {
        _transform = transform;
        _madeOutsideEvaluate = madeOutsideEvaluate;
    }

    public override QName Name => _inner.Name;
    public override XdmSequenceType ReturnType => _inner.ReturnType;
    public override IReadOnlyList<FunctionParameterDef> Parameters => _inner.Parameters;

    public override XQueryFunction BindCreationContext(PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => context is QueryExecutionContext { InsideXslEvaluate: false }
            ? new XsltFunctionLookupFunction(_transform, madeOutsideEvaluate: true)
            : this;

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (!_madeOutsideEvaluate
            && context is QueryExecutionContext { InsideXslEvaluate: true }
            && arguments.Count > 0
            && (arguments[0] is object?[] { Length: 1 } one ? one[0] : arguments[0]) is QName name
            && XsltEvaluateUnavailable.IsXsltOnlyFunction(name))
            throw new XsltException($"XTDE3160: The function {name.LocalName}() is not available within xsl:evaluate");
        if (StylesheetFunctionAsSeenByCaller(arguments, context) is { } seen)
            return ValueTask.FromResult(seen.Function is { } function
                ? (object?)new XsltUserFunctionAdapter(function, _transform!).BindCreationContext(context)
                : null);
        return _inner.InvokeAsync(arguments, context);
    }

    /// <summary>
    /// For a stylesheet function, the declaration the package that calls function-lookup sees
    /// under that name and arity, or no function. A package sees its own functions and the
    /// public ones of the packages it uses: not a private function of a used package, not an
    /// abstract one. In a used package the function is the package's own declaration, not the
    /// override a using package gives it (W3C function-lookup-004/005). Every function of every
    /// package was found, and an overridden one was the override. Null for a name that is not a
    /// stylesheet function, which the XQuery lookup answers.
    /// </summary>
    private (Ast.XsltFunction? Function, bool Found)? StylesheetFunctionAsSeenByCaller(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (_transform is null || context is not QueryExecutionContext qec || arguments.Count < 2)
            return null;
        if ((arguments[0] is object?[] { Length: 1 } n ? n[0] : arguments[0]) is not QName name)
            return null;
        var arityArg = arguments[1] is object?[] { Length: 1 } a ? a[0] : arguments[1];
        if (arityArg is null or object?[])
            return null;
        int arity;
        try { arity = Convert.ToInt32(arityArg, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { return null; }

        Ast.XsltFunction? declared;
        switch (qec.Functions.Resolve(name, arity))
        {
            case XsltUserFunctionAdapter user:
                declared = user.Function;
                break;
            case XsltAbstractFunctionAdapter:
                return (null, true);
            default:
                return null;
        }
        var package = XsltFormatNumberEngine.GetCurrentPackageStylesheet(_transform);
        var visible = (package ?? _transform._stylesheet).Functions;
        if (!visible.TryGetValue((declared.Name, arity), out var seen))
            return (null, true);
        // An override a using package gave the function stands in this package's table; it
        // belongs to that package, and keeps the declaration it replaced.
        while (package is not null && seen is { OriginalFunction: { } original }
            && !ReferenceEquals(seen.PackageStylesheet, package))
            seen = original;
        if (seen is null || seen.Visibility is Ast.Visibility.Abstract or Ast.Visibility.Hidden)
            return (null, true);
        // A private function of a used package is that package's alone.
        if (seen.Visibility is Ast.Visibility.Private && !ReferenceEquals(seen.PackageStylesheet, package))
            return (null, true);
        return (seen, true);
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
