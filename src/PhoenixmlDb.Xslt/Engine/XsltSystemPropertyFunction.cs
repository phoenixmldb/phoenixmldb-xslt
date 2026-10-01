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
    // The in-scope namespaces where a function item for this function was created (system-property#1,
    // system-property(?), function-lookup). A prefixed argument resolves against them, so an item
    // made in the stylesheet still resolves 'xsl:version' when invoked inside xsl:evaluate, whose
    // expression may bind 'xsl' differently or not at all (W3C system-property-101d and siblings).
    // Null for the registered function: a direct call resolves against the calling expression.
    private readonly IReadOnlyDictionary<string, string>? _creationBindings;

    // True when the transformation's resource policy disables xsl:evaluate ("dynamically disabled",
    // XSLT 3.0 §27.6): supports-dynamic-evaluation is then "no" at run time.
    private readonly Func<bool>? _dynamicEvaluationDisabled;

    public XsltSystemPropertyFunction(Func<bool>? dynamicEvaluationDisabled = null)
        => _dynamicEvaluationDisabled = dynamicEvaluationDisabled;

    private XsltSystemPropertyFunction(IReadOnlyDictionary<string, string> creationBindings, Func<bool>? dynamicEvaluationDisabled)
    {
        _creationBindings = creationBindings;
        _dynamicEvaluationDisabled = dynamicEvaluationDisabled;
    }

    public override PhoenixmlDb.XQuery.Ast.XQueryFunction BindCreationContext(PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext { PrefixNamespaceBindings: { } bindings }
            ? new XsltSystemPropertyFunction(new Dictionary<string, string>(bindings, StringComparer.Ordinal), _dynamicEvaluationDisabled)
            : this;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "system-property");
    public override XdmSequenceType ReturnType => XdmSequenceType.String;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "property-name"), Type = XdmSequenceType.String }];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
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
            var bindings = _creationBindings
                ?? (context as PhoenixmlDb.XQuery.Execution.QueryExecutionContext)?.PrefixNamespaceBindings;
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
        var result = name == "supports-dynamic-evaluation" && _dynamicEvaluationDisabled?.Invoke() == true
            ? "no"
            : PropertyValue(name);
        return ValueTask.FromResult<object?>(result);
    }

    /// <summary>
    /// The value of an XSLT-namespace system property, by local name. The one table: the parser's
    /// compile-time evaluators used to carry their own copies, which disagreed with this one
    /// (product-version was "1.0" statically and the assembly version at run time), and the W3C
    /// system-property tests require the static and dynamic answers to match.
    /// </summary>
    /// <remarks>
    /// xpath-version is "3.1": an XSLT 3.0 processor reports "3.0" or "3.1" (XSLT 3.0 §20.4.3),
    /// and xsl:version here is "3.0". It was "4.0" because the engine implements XPath 4.0
    /// functions, but that is not a value an XSLT 3.0 processor may report
    /// (W3C system-property-108*).
    /// </remarks>
    internal static string PropertyValue(string localName) => localName switch
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
        "xpath-version" => "3.1",
        "xsd-version" => "1.1",
        _ => ""
    };
}
