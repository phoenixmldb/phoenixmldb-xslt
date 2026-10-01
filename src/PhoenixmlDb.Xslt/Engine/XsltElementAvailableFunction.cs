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

    // As for system-property(): a function item resolves a prefixed argument against the
    // namespaces in scope where the item was created, not where it is invoked.
    private readonly IReadOnlyDictionary<string, string>? _creationBindings;

    // True when the transformation's resource policy disables xsl:evaluate ("dynamically disabled",
    // XSLT 3.0 §27.6): element-available('xsl:evaluate') is then false at run time.
    private readonly Func<bool>? _dynamicEvaluationDisabled;

    public XsltElementAvailableFunction(HashSet<string> extensionNamespaces, Func<bool>? dynamicEvaluationDisabled = null)
    {
        _extensionNamespaces = extensionNamespaces;
        _dynamicEvaluationDisabled = dynamicEvaluationDisabled;
    }

    private XsltElementAvailableFunction(HashSet<string> extensionNamespaces, IReadOnlyDictionary<string, string> creationBindings, Func<bool>? dynamicEvaluationDisabled)
    {
        _extensionNamespaces = extensionNamespaces;
        _creationBindings = creationBindings;
        _dynamicEvaluationDisabled = dynamicEvaluationDisabled;
    }

    public override PhoenixmlDb.XQuery.Ast.XQueryFunction BindCreationContext(PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext { PrefixNamespaceBindings: { } bindings }
            ? new XsltElementAvailableFunction(_extensionNamespaces, new Dictionary<string, string>(bindings, StringComparer.Ordinal), _dynamicEvaluationDisabled)
            : this;

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
            var bindings = _creationBindings
                ?? (context as PhoenixmlDb.XQuery.Execution.QueryExecutionContext)?.PrefixNamespaceBindings;
            if (bindings != null && bindings.TryGetValue(prefix, out var resolvedNs))
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

        var available = name == "evaluate"
            ? _dynamicEvaluationDisabled?.Invoke() != true
            : IsXsltElement(name);
        return ValueTask.FromResult<object?>(available);
    }

    /// <summary>
    /// Whether a local name in the XSLT namespace is an XSLT element this processor implements:
    /// element-available() is true for every such element, declarations included (XSLT 3.0 §20.2).
    /// The one table: the parser's static evaluator used to keep its own copy, and the two drifted
    /// (the static copy lacked xsl:evaluate, the run-time copy xsl:attribute-set).
    /// </summary>
    internal static bool IsXsltElement(string localName) => localName is
        "accept" or "accumulator" or "accumulator-rule" or "analyze-string" or "apply-imports" or
        "apply-templates" or "array" or "assert" or "attribute" or "attribute-set" or "break" or
        "call-template" or "catch" or "character-map" or "choose" or "comment" or "context-item" or
        "copy" or "copy-of" or "decimal-format" or "document" or "element" or "evaluate" or
        "expose" or "fallback" or "for-each" or "for-each-group" or "fork" or "function" or
        "global-context-item" or "if" or "import" or "import-schema" or "include" or "iterate" or
        "key" or "map" or "map-entry" or "matching-substring" or "merge" or "merge-action" or
        "merge-key" or "merge-source" or "message" or "mode" or "namespace" or "namespace-alias" or
        "next-iteration" or "next-match" or "non-matching-substring" or "number" or
        "on-completion" or "on-empty" or "on-non-empty" or "otherwise" or "output" or
        "output-character" or "override" or "package" or "param" or "perform-sort" or
        "preserve-space" or "processing-instruction" or "result-document" or "sequence" or
        "sort" or "source-document" or "strip-space" or "stylesheet" or "template" or "text" or
        "transform" or "try" or "use-package" or "value-of" or "variable" or "when" or
        "where-populated" or "with-param";
}
