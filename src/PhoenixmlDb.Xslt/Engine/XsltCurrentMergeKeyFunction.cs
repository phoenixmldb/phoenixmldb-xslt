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

internal sealed class XsltCurrentMergeKeyFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltCurrentMergeKeyFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-merge-key");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalItem;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];
    // XTDE3510, not XTDE3480: the spec gives current-merge-key its own code (§15.4, "used when
    // the current merge key is absent"); XTDE3480 is current-merge-group's. W3C merge-056/101.
    public override string? DynamicCallErrorCode => "XTDE3510";
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (_context.TryGetVariable(new QName(NamespaceId.None, "current-merge-key"), out var key) && key != null)
            return ValueTask.FromResult<object?>(key);

        throw new XsltException("XTDE3510: current-merge-key() called when there is no current merge key");
    }
}
