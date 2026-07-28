namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// The operand-bearing construct kinds whose §19.6/§19.7 operand usage SP-A threads
/// into the runtime. The executor maps its AST node to one of these at the thread site;
/// successors (SP-B–E) extend this enum and <see cref="UsageClassifier.OperandUsage"/>.
/// </summary>
public enum ConstructKind
{
    /// <summary>§5.7.1 element/complex content (LRE, xsl:element body).</summary>
    ElementContent,

    /// <summary>§5.7.1 document node content (xsl:document body, xsl:variable temp tree).</summary>
    DocumentContent,

    /// <summary>§5.7.2 attribute value content.</summary>
    AttributeContent,

    /// <summary>§5.7.2 comment/PI/text simple content.</summary>
    TextContent,

    /// <summary>§5.7.2 explicit atomization (fn:data, string-join argument, etc.).</summary>
    SimpleContentAtomize,

    /// <summary>§19.6 name/identity inspection (fn:node-name, fn:name argument).</summary>
    NodeNameInspection,

    /// <summary>§19.7 navigation source of an axis step.</summary>
    AxisStepSource
}

/// <summary>
/// Pure, table-driven classifier for the §19.6/§19.7 operand-usage role a construct
/// imposes on its operand. No runtime state; safe to call from any thread.
/// </summary>
public static class UsageClassifier
{
    /// <summary>
    /// The <see cref="Usage"/> role the given construct imposes on its content/argument operand.
    /// Unrecognized kinds fall back to <see cref="Usage.Absorption"/> — the most
    /// separator-preserving role, so a classification gap never silently drops separators.
    /// </summary>
    public static Usage OperandUsage(ConstructKind kind) => kind switch
    {
        ConstructKind.ElementContent => Usage.Transmission,
        ConstructKind.DocumentContent => Usage.Transmission,
        ConstructKind.AttributeContent => Usage.Absorption,
        ConstructKind.TextContent => Usage.Absorption,
        ConstructKind.SimpleContentAtomize => Usage.Absorption,
        ConstructKind.NodeNameInspection => Usage.Inspection,
        ConstructKind.AxisStepSource => Usage.Navigation,
        _ => Usage.Absorption
    };
}
