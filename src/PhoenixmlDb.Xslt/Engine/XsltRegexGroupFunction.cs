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

internal sealed class XsltRegexGroupFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    // A function item for regex-group (regex-group#1, function-lookup): the current captured
    // substrings are cleared in its context, so every call returns "" (XSLT 3.0 §5.3.4). It
    // returned the groups of whatever xsl:analyze-string was active at the call (W3C regex-090/091).
    private readonly bool _isFunctionItem;
    public XsltRegexGroupFunction(DefaultXsltExecutionContext context) => _context = context;
    private XsltRegexGroupFunction(DefaultXsltExecutionContext context, bool isFunctionItem)
    {
        _context = context;
        _isFunctionItem = isFunctionItem;
    }
    public override PhoenixmlDb.XQuery.Ast.XQueryFunction BindCreationContext(PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => new XsltRegexGroupFunction(_context, isFunctionItem: true);
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "regex-group");
    public override XdmSequenceType ReturnType => XdmSequenceType.String;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "number"), Type = XdmSequenceType.Integer }];
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var groupNum = Convert.ToInt32(arguments[0], System.Globalization.CultureInfo.InvariantCulture);
        // Per XSLT spec, regex-group() returns empty string when called outside xsl:analyze-string
        if (!_isFunctionItem && _context.TryGetVariable(new QName(NamespaceId.None, "regex-groups"), out var groups) &&
            groups is System.Text.RegularExpressions.Match match)
        {
            if (groupNum >= 0 && groupNum < match.Groups.Count)
                return ValueTask.FromResult<object?>(match.Groups[groupNum].Value);
        }
        return ValueTask.FromResult<object?>("");
    }
}
