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
/// Adapter for xsl:original bound to a specific overridden function. Registered per-call
/// while an overriding xsl:function body executes, so a function ITEM materialized from
/// xsl:original#N or a partial application captures the correct overridden function even
/// when invoked outside the overriding function's execution frame (override-f-017/-018).
/// </summary>
internal sealed class XsltBoundOriginalFunctionAdapter : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    private readonly XsltFunction _originalFunc;

    public XsltBoundOriginalFunctionAdapter(DefaultXsltExecutionContext context, XsltFunction originalFunc)
    {
        _context = context;
        _originalFunc = originalFunc;
    }

    public override QName Name => new(NamespaceId.Xslt, "original");
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    public override bool IsVariadic => true;
    public override int MaxArity => 20;

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => await _context.CallXsltFunctionAsync(_originalFunc, arguments).ConfigureAwait(false);
}
