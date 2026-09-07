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
/// XSLT unparsed-text($href, $encoding) function — 2-parameter overload.
/// </summary>
internal sealed class XsltUnparsedText2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltUnparsedText2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "unparsed-text");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalString;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "href"), Type = XdmSequenceType.OptionalString },
        new() { Name = new QName(NamespaceId.None, "encoding"), Type = XdmSequenceType.String }
    ];

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var href = arguments[0]?.ToString();
        if (string.IsNullOrEmpty(href))
            return null;

        var encodingName = arguments[1]?.ToString() ?? "utf-8";
        System.Text.Encoding encoding;
        try
        {
            encoding = System.Text.Encoding.GetEncoding(encodingName);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Unknown encoding: {encodingName}");
        }

        try
        {
            var policyText = _context.ResolveUnparsedTextViaPolicy(href, encodingName);
            if (policyText != null)
            {
                UnparsedTextHelper.ValidateTextContent(policyText);
                return policyText;
            }

            var filePath = UnparsedTextHelper.ResolveFilePath(href, _context._stylesheet.BaseUri);
            if (filePath != null)
            {
                var text = await System.IO.File.ReadAllTextAsync(filePath, encoding).ConfigureAwait(false);
                UnparsedTextHelper.ValidateTextContent(text);
                return text;
            }
        }
        catch (IOException)
        {
            // unparsed-text returns error for inaccessible resources
        }
        return null;
    }
}
