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

/// <summary>
/// Adapts an XSLT user-defined function (xsl:function) to the XQuery function interface.
/// </summary>
internal sealed class XsltUserFunctionAdapter : PhoenixmlDb.XQuery.Ast.XQueryFunction,
    PhoenixmlDb.XQuery.Execution.ITailCallTarget, PhoenixmlDb.XQuery.Execution.IStringSliceParameters
{
    private readonly XsltFunction _xsltFunc;
    private readonly DefaultXsltExecutionContext _context;

    public XsltUserFunctionAdapter(XsltFunction xsltFunc, DefaultXsltExecutionContext context)
    {
        _xsltFunc = xsltFunc;
        _context = context;
    }

    public override QName Name
    {
        get
        {
            var name = _xsltFunc.Name;
            if (name.ResolvedNamespace == null && name.Namespace != NamespaceId.None)
            {
                // Resolve NamespaceId to URI string for fn:function-name() introspection
                var nsUri = PhoenixmlDb.XQuery.Functions.FunctionNamespaces.ResolveNamespace(name.Namespace);
                if (nsUri == null)
                {
                    // Check dynamic namespaces (user-defined function namespaces)
                    foreach (var (uri, nsId) in StylesheetParser.DynamicNamespaces)
                    {
                        if (nsId == name.Namespace)
                        {
                            nsUri = uri;
                            break;
                        }
                    }
                }
                if (nsUri != null)
                    return new QName(name.Namespace, name.LocalName, name.Prefix) { RuntimeNamespace = nsUri };
            }
            return name;
        }
    }

    /// <summary>The stylesheet function this adapter calls.</summary>
    internal XsltFunction Function => _xsltFunc;

    /// <summary>
    /// Whether calls to and from <paramref name="function"/> may be tail calls (xquery#296): not a
    /// memoised function (cache="yes", new-each-time="no"), whose result is stored per call, nor
    /// a streamable one.
    /// </summary>
    internal static bool TakesTailCalls(XsltFunction function)
        => !function.Cache && function.NewEachTime != "no" && function.Streamability == null;

    /// <summary>The tail-call key of a stylesheet function: its kind and its declared return type.</summary>
    internal static string TailCallKeyOf(XsltFunction function)
        => PhoenixmlDb.XQuery.Execution.TailCalls.ReturnTypeKey("xslt", function.As);

    /// <summary>A key no call matches for a function that takes no tail calls.</summary>
    public string TailCallKey => TakesTailCalls(_xsltFunc) ? TailCallKeyOf(_xsltFunc) : "";

    /// <summary>
    /// The binding stores a string slice as it is for a parameter without an as type or of
    /// exactly xs:string (CallXsltFunctionOnceAsync). A memoised function compares its arguments,
    /// and takes none.
    /// </summary>
    public bool TakesSlice(int parameterIndex)
        => TakesTailCalls(_xsltFunc) && parameterIndex < _xsltFunc.Parameters.Count
            && DefaultXsltExecutionContext.ParameterTakesSlice(_xsltFunc.Parameters[parameterIndex].As);

    public override XdmSequenceType ReturnType =>
        _xsltFunc.As ?? XdmSequenceType.ZeroOrMoreItems;

    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        _xsltFunc.Parameters.Select(p => new FunctionParameterDef
        {
            Name = p.Name,
            Type = p.As ?? XdmSequenceType.ZeroOrMoreItems
        }).ToList();

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // XTDE3160: Private stylesheet functions cannot be called from xsl:evaluate
        if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qec && qec.InsideXslEvaluate
            && _xsltFunc.Visibility != Ast.Visibility.Public && _xsltFunc.Visibility != Ast.Visibility.Final)
            throw new XsltException($"XTDE3160: The stylesheet function {_xsltFunc.Name.LocalName}() is not public and cannot be called from xsl:evaluate");
        return await _context.CallXsltFunctionAsync(_xsltFunc, arguments).ConfigureAwait(false);
    }
}
