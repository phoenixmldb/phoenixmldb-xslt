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
/// fn:format-number($value, $picture) — XSLT-aware 2-arg version that uses the default decimal format.
/// </summary>
internal sealed class XsltFormatNumberFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltFormatNumberFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "format-number");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.String;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "value"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Double, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne } },
        new() { Name = new QName(NamespaceId.None, "picture"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.String }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var raw = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(arguments[0]);
        var picture = arguments[1]?.ToString() ?? "";
        var df = XsltFormatNumberEngine.GetDecimalFormat(_context, null);
        var result = raw is System.Numerics.BigInteger bi
            ? XsltFormatNumberEngine.FormatBigInteger(bi, picture, df)
            : XsltFormatNumberEngine.Format(XsltFormatNumberEngine.CoerceToDouble(arguments[0]), picture, df);
        return ValueTask.FromResult<object?>(result);
    }
}
