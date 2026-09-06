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
/// XSLT current() function - returns the current context item (outer context, not inner).
/// </summary>
internal sealed class XsltCurrentFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltCurrentFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalItem;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    public override string? DynamicCallErrorCode => "XTDE1360";

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qec && qec.InsideXslEvaluate)
            throw new XsltException("XTDE3160: The function current() is not available within xsl:evaluate");
        // current() returns the "outer" XSLT context item, not the inner XPath context
        var item = _context.CurrentItem;
        // XTDE1360: current() called when context item is absent (e.g., inside xsl:function)
        if (item != null && ReferenceEquals(item, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
            throw new XsltException("XTDE1360: The current() function is called when the context item is absent");
        return ValueTask.FromResult(item);
    }
}
