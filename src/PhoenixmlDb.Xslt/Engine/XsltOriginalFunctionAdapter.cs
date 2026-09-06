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
/// Dynamic adapter for xsl:original() function calls in package overrides.
/// Resolves the original function at runtime via _currentXsltFunctionStack.
/// </summary>
internal sealed class XsltOriginalFunctionAdapter : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltOriginalFunctionAdapter(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(NamespaceId.Xslt, "original");
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    public override bool IsVariadic => true;
    public override int MaxArity => 20;

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (_context._currentXsltFunctionStack.Count == 0)
            throw new XsltException("XTDE3058: xsl:original invoked but no overriding function is active");

        var currentFunc = _context._currentXsltFunctionStack.Peek();
        var originalFunc = currentFunc.OriginalFunction
            ?? throw new XsltException("XTDE3058: xsl:original invoked but no overridden function is available");

        return await _context.CallXsltFunctionAsync(originalFunc, arguments).ConfigureAwait(false);
    }
}
