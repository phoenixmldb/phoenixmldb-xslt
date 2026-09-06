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

internal sealed class XsltCurrentMergeGroupFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltCurrentMergeGroupFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-merge-group");
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    public override string? DynamicCallErrorCode => "XTDE3480";
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (_context.TryGetVariable(new QName(NamespaceId.None, "current-merge-group"), out var group) && group != null)
        {
            // Same shape as current-group(): unwrap List<object> to object?[] so
            // FunctionCallOperator yields items individually rather than treating
            // the whole list as a single XDM array item (path navigation breaks).
            if (group is List<object> list)
                return ValueTask.FromResult<object?>(list.ToArray());
            return ValueTask.FromResult<object?>(group);
        }

        throw new XsltException("XTDE3480: current-merge-group() called when there is no current merge group");
    }
}
