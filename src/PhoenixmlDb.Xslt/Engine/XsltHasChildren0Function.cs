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
/// fn:has-children() — 0-arg version uses context item.
/// </summary>
internal sealed class XsltHasChildren0Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltHasChildren0Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "has-children");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new() { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Boolean, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ExactlyOne };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var node = context.ContextItem ?? _context.ContextItem;
        var hasChildren = node switch
        {
            XdmDocument doc => doc.Children.Count > 0,
            XdmElement elem => elem.Children.Count > 0,
            _ => false
        };
        return ValueTask.FromResult<object?>(hasChildren);
    }
}

// ─── fn:parse-xml ───────────────────────────────────────────────────────────
