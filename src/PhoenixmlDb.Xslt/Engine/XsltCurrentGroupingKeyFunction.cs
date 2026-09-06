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

internal sealed class XsltCurrentGroupingKeyFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltCurrentGroupingKeyFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-grouping-key");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalItem;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    public override string? DynamicCallErrorCode => "XTDE1071";
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (_context.TryGetVariable(new QName(NamespaceId.None, "current-grouping-key"), out var key) && key != null)
        {
            // A composite grouping key (composite="yes") is a SEQUENCE of atomic
            // values. Unwrap the backing List<object?> to object?[] so downstream
            // sequence access (indexing, count()) yields the tuple items
            // individually rather than treating the whole list as a single XDM
            // item that string-joins to "italy 5". (XSLT 3.0 §18.2)
            if (key is List<object?> compositeKey)
                return ValueTask.FromResult<object?>(compositeKey.ToArray());
            return ValueTask.FromResult<object?>(key);
        }

        // XSLT 3.0: calling current-grouping-key() when there is no current grouping key is a dynamic error
        throw new XsltException("XTDE1071: current-grouping-key() called when there is no current grouping key");
    }
}
