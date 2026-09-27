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
/// fn:has-children($node as node()?) as xs:boolean
/// </summary>
internal sealed class XsltHasChildrenFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltHasChildrenFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "has-children");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new() { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Boolean, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ExactlyOne };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "node"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalNode }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var node = arguments[0];
        if (node is null)
            return ValueTask.FromResult<object?>(false);

        return ValueTask.FromResult<object?>(Answer(node, _context));
    }

    /// <summary>
    /// The one implementation, shared with <see cref="XsltHasChildren0Function"/>. The two arities
    /// each carried their own copy, which is how a streaming fix to one of them missed the form
    /// match patterns actually use — <c>*[has-children()]</c> is the zero-argument call.
    /// </summary>
    internal static bool Answer(object? node, DefaultXsltExecutionContext context) => node switch
    {
        XdmDocument doc => doc.Children.Count > 0,
        XdmElement elem when elem.Children.Count > 0 => true,
        // A streamed element is shallow — its children have not been read — so Children is empty
        // whatever the source holds, and this always answered false (BUGS #92, streamable-135).
        // Ask the live reader to look one event ahead instead.
        XdmElement elem => context.TryStreamedHasChildren(elem) ?? false,
        _ => false
    };
}
