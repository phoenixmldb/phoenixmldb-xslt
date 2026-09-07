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
/// XSLT available-system-properties() function.
/// </summary>
internal sealed class XsltAvailableSystemPropertiesFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private const string XsltNamespaceUri = "http://www.w3.org/1999/XSL/Transform";

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "available-system-properties");
    public override XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.QName,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        string[] localNames =
        [
            "version", "vendor", "vendor-url",
            "product-name", "product-version",
            "is-schema-aware", "supports-serialization",
            "supports-backwards-compatibility",
            "supports-namespace-axis", "supports-streaming",
            "supports-dynamic-evaluation", "supports-higher-order-functions",
            "xpath-version", "xsd-version"
        ];

        // Use hash-based NamespaceId consistent with the QName() constructor function
        var nsId = new NamespaceId((uint)Math.Abs(XsltNamespaceUri.GetHashCode(StringComparison.Ordinal)));
        // Return an XDM SEQUENCE (object?[]) of xs:QName, not a List<object>. A List is
        // treated by the type-checker as a single XDM array item (List<object?> matches the
        // "don't enumerate" pattern), so binding to `as="xs:QName+"` saw one non-QName item
        // and raised XTTE0570. An object?[] is enumerated as a sequence of QName items.
        var props = new object?[localNames.Length];
        for (int i = 0; i < localNames.Length; i++)
        {
            props[i] = new QName(nsId, localNames[i], "xsl") { RuntimeNamespace = XsltNamespaceUri };
        }
        return ValueTask.FromResult<object?>(props);
    }
}
