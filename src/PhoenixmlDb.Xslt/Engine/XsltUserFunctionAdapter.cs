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
internal sealed class XsltUserFunctionAdapter : PhoenixmlDb.XQuery.Ast.XQueryFunction
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
