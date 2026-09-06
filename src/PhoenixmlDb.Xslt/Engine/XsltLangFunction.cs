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
/// fn:lang($testlang) as xs:boolean — checks xml:lang on context node and ancestors.
/// </summary>
internal sealed class XsltLangFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltLangFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "lang");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.Boolean;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "testlang"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalString }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var testLang = arguments[0]?.ToString() ?? "";
        var node = context.ContextItem;
        return ValueTask.FromResult<object?>(MatchLang(testLang, node));
    }

    internal bool MatchLang(string testLang, object? node)
    {
        // Walk up the ancestor chain looking for xml:lang attribute
        var current = node;
        while (current is XdmNode xdmNode)
        {
            if (current is XdmElement elem)
            {
                foreach (var attr in _context._nodeStore!.GetAttributes(elem))
                {
                    if (attr.Namespace == NamespaceId.Xml &&
                        string.Equals(attr.LocalName, "lang", StringComparison.Ordinal))
                    {
                        // Found xml:lang — check if it matches
                        return LangMatches(attr.Value, testLang);
                    }
                }
            }
            // Move to parent
            if (xdmNode.Parent.HasValue)
                current = _context._nodeStore?.GetNode(xdmNode.Parent.Value);
            else
                break;
        }
        return false;
    }

    /// <summary>
    /// Per XPath spec: lang('en') matches 'en', 'EN', 'en-US', etc.
    /// The language matches if testLang equals the lang value or is a prefix followed by '-'.
    /// </summary>
    private static bool LangMatches(string langValue, string testLang)
    {
        if (string.Equals(langValue, testLang, StringComparison.OrdinalIgnoreCase))
            return true;
        if (langValue.Length > testLang.Length &&
            langValue[testLang.Length] == '-' &&
            langValue.StartsWith(testLang, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
