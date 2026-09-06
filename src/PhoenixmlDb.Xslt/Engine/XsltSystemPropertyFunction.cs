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
/// XSLT system-property() function.
/// </summary>
internal sealed class XsltSystemPropertyFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "system-property");
    public override XdmSequenceType ReturnType => XdmSequenceType.String;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "property-name"), Type = XdmSequenceType.String }];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qec && qec.InsideXslEvaluate)
            throw new XsltException("XTDE3160: The function system-property() is not available within xsl:evaluate");
        var name = arguments[0]?.ToString() ?? "";
        // XTDE1390: Validate name is a valid QName
        XsltFunctionValidation.ValidateQNameArgument(name, "XTDE1390", "system-property");
        // Resolve and strip prefix if present
        string? namespaceUri = null;
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
            var bindings = (context as PhoenixmlDb.XQuery.Execution.QueryExecutionContext)?.PrefixNamespaceBindings;
            if (bindings != null)
            {
                // XTDE1390: Verify prefix is declared in scope
                if (!bindings.TryGetValue(prefix, out var resolvedUri))
                    throw new XsltException($"XTDE1390: Namespace prefix '{prefix}' in the argument to system-property() has not been declared");
                namespaceUri = resolvedUri;
            }
            name = parts[1];
        }
        // Only return system properties for the XSLT namespace.
        // Unprefixed names are in no namespace — they don't match XSLT properties.
        var xsltNs = "http://www.w3.org/1999/XSL/Transform";
        if (namespaceUri != xsltNs)
            return ValueTask.FromResult<object?>("");
        var result = name switch
        {
            "version" => "3.0",
            "vendor" => "PhoenixmlDb",
            "vendor-url" => "https://endpointsystems.com",
            "product-name" => "PhoenixmlDb XSLT",
            "product-version" => typeof(XsltTransformEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0",
            "is-schema-aware" => "no",
            "supports-serialization" => "yes",
            "supports-backwards-compatibility" => "yes",
            "supports-namespace-axis" => "yes",
            "supports-streaming" => "yes",
            "supports-dynamic-evaluation" => "yes",
            "supports-higher-order-functions" => "yes",
            "xpath-version" => "4.0",
            "xsd-version" => "1.1",
            _ => ""
        };
        return ValueTask.FromResult<object?>(result);
    }
}
