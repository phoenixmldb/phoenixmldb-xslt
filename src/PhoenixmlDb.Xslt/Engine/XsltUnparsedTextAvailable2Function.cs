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
/// XSLT unparsed-text-available($href, $encoding) function — 2-parameter overload.
/// </summary>
internal sealed class XsltUnparsedTextAvailable2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltUnparsedTextAvailable2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "unparsed-text-available");
    public override XdmSequenceType ReturnType => XdmSequenceType.Boolean;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "href"), Type = XdmSequenceType.OptionalString },
        new() { Name = new QName(NamespaceId.None, "encoding"), Type = XdmSequenceType.String }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var href = arguments[0]?.ToString();
        if (string.IsNullOrEmpty(href))
            return ValueTask.FromResult<object?>(false);

        var encodingName = arguments[1]?.ToString() ?? "utf-8";
        try
        {
            System.Text.Encoding.GetEncoding(encodingName);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult<object?>(false);
        }

        try
        {
            if (!_context.IsUnparsedTextAvailableViaPolicy(href))
                return ValueTask.FromResult<object?>(false);

            var filePath = UnparsedTextHelper.ResolveFilePath(href, _context._stylesheet.BaseUri);
            return ValueTask.FromResult<object?>(filePath != null);
        }
        catch (IOException)
        {
            return ValueTask.FromResult<object?>(false);
        }
    }
}
