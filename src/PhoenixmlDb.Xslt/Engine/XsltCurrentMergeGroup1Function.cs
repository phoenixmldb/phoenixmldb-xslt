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

internal sealed class XsltCurrentMergeGroup1Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltCurrentMergeGroup1Function(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-merge-group");
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "source-name"), Type = XdmSequenceType.String }];
    public override string? DynamicCallErrorCode => "XTDE3480";
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var sourceName = arguments[0]?.ToString();
        if (sourceName != null &&
            _context.TryGetVariable(new QName(NamespaceId.None, $"current-merge-group:{sourceName}"), out var group) && group != null)
        {
            if (group is List<object> list)
                return ValueTask.FromResult<object?>(list.ToArray());
            return ValueTask.FromResult<object?>(group);
        }

        // XTDE3490: source name not recognized
        throw new XsltException($"XTDE3490: current-merge-group() source name '{sourceName}' does not match any xsl:merge-source");
    }
}
