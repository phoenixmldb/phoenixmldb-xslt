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
/// XSLT unparsed-text-available() function.
/// </summary>
internal sealed class XsltUnparsedTextAvailableFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltUnparsedTextAvailableFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "unparsed-text-available");
    public override XdmSequenceType ReturnType => XdmSequenceType.Boolean;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "href"), Type = XdmSequenceType.OptionalString }];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var href = arguments[0]?.ToString();
        if (string.IsNullOrEmpty(href))
            return ValueTask.FromResult<object?>(false);

        try
        {
            // The host's resolver serves it: available, with no file to look for.
            if (_context.HostTextAvailability(UnparsedTextHelper.Absolute(href, _context)) is { } answer)
                return ValueTask.FromResult<object?>(answer);
            if (!_context.IsUnparsedTextAvailableViaPolicy(UnparsedTextHelper.Absolute(href, _context)))
                return ValueTask.FromResult<object?>(false);

            var filePath = UnparsedTextHelper.ResolveFilePath(href, UnparsedTextHelper.StaticBase(_context), _context.Policy);
            return ValueTask.FromResult<object?>(filePath != null);
        }
        catch (IOException)
        {
            return ValueTask.FromResult<object?>(false);
        }
    }
}
