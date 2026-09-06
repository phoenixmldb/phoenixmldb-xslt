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

internal sealed class XsltCurrentGroupFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltCurrentGroupFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-group");
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    public override string? DynamicCallErrorCode => "XTDE1061";
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (_context.TryGetVariable(new QName(NamespaceId.None, "current-group"), out var group) && group != null)
        {
            // Return as object?[] so FunctionCallOperator yields items individually.
            // List<object> must not be returned directly because at runtime it matches
            // the "is List<object?>" check (nullable reference types are erased) and
            // would be yielded as a single XDM array item instead of being iterated.
            if (group is List<object> list)
                return ValueTask.FromResult<object?>(list.ToArray());
            return ValueTask.FromResult<object?>(group);
        }

        // XSLT 3.0: calling current-group() when there is no current group is a dynamic error
        throw new XsltException("XTDE1061: current-group() called when there is no current group");
    }
}
