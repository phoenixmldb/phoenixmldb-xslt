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

internal sealed class XsltElementAvailableFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly HashSet<string> _extensionNamespaces;

    public XsltElementAvailableFunction(HashSet<string> extensionNamespaces)
    {
        _extensionNamespaces = extensionNamespaces;
    }

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "element-available");
    public override XdmSequenceType ReturnType => XdmSequenceType.Boolean;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "name"), Type = XdmSequenceType.String }];
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var name = arguments[0]?.ToString() ?? "";
        // XTDE1440: Validate name is a valid EQName
        XsltFunctionValidation.ValidateQNameArgument(name, "XTDE1440", "element-available");
        string? namespaceUri = null;

        // Handle EQName syntax: Q{namespace-uri}local-name
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                namespaceUri = name[2..braceClose];
                name = name[(braceClose + 1)..];
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];
            name = parts[1];
            // Resolve the prefix to a namespace URI
            if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qec
                && qec.PrefixNamespaceBindings != null
                && qec.PrefixNamespaceBindings.TryGetValue(prefix, out var resolvedNs))
            {
                namespaceUri = resolvedNs;
            }
            else if (prefix == "xsl")
            {
                namespaceUri = "http://www.w3.org/1999/XSL/Transform";
            }
            else
            {
                // Unresolvable prefix — definitely not an XSLT element
                return ValueTask.FromResult<object?>(false);
            }
        }

        // element-available returns true for extension element namespaces
        if (namespaceUri != null && namespaceUri != "http://www.w3.org/1999/XSL/Transform")
            return ValueTask.FromResult<object?>(_extensionNamespaces.Contains(namespaceUri));

        // XSLT 3.0 instruction elements
        var available = name switch
        {
            "apply-templates" or "call-template" or "choose" or "copy" or "copy-of" or
            "element" or "attribute" or "text" or "value-of" or "variable" or "param" or
            "if" or "for-each" or "for-each-group" or "sort" or "message" or "number" or
            "comment" or "processing-instruction" or "sequence" or "iterate" or
            "try" or "catch" or "next-match" or "apply-imports" or "result-document" or
            "analyze-string" or "matching-substring" or "non-matching-substring" or
            "where-populated" or "on-empty" or "on-non-empty" or "fallback" or
            "namespace" or "output" or "strip-space" or "preserve-space" or
            "stylesheet" or "transform" or "template" or "function" or
            "import" or "include" or "import-schema" or "decimal-format" or
            "character-map" or "output-character" or "key" or
            "document" or "source-document" or "with-param" or
            "when" or "otherwise" or "break" or "next-iteration" or
            "accumulator" or "accumulator-rule" or
            "context-item" or "global-context-item" or
            "map" or "map-entry" or "array" or "assert" or
            "merge" or "merge-source" or "merge-action" or "merge-key" or
            "fork" or "accept" or "expose" or "override" or "use-package" => true,
            _ => false
        };
        return ValueTask.FromResult<object?>(available);
    }
}
