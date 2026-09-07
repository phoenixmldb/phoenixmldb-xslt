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
/// EXSLT exsl:node-set() — converts a result tree fragment to a node set.
/// In XSLT 2.0+, RTFs are automatically treated as node sequences, so this is an identity function.
/// </summary>
internal sealed class ExslNodeSetFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private static readonly NamespaceId ExslNs = StylesheetParser.ResolveNamespaceUri("http://exslt.org/common");
    private readonly DefaultXsltExecutionContext _context;

    public ExslNodeSetFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(ExslNs, "node-set", "exsl");
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "rtf"), Type = XdmSequenceType.ZeroOrMoreItems }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // In XSLT 2.0+, exsl:node-set() is an identity function since RTFs are automatically
        // converted to XDM node sequences. Use ConvertRtfForXQuery to handle RTF conversion.
        return ValueTask.FromResult(_context.ConvertRtfForXQuery(arguments[0]));
    }
}
