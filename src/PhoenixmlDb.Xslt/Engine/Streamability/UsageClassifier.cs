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

    /// <summary>
    /// The §19.6/§19.7 usage a streamable for-each body imposes on the matched context item
    /// <c>.</c>, driving the materialize-vs-atomize dispatch decision. An explicit <c>fn:data(...)</c>
    /// wrapper on the for-each select forces <see cref="Usage.Absorption"/> (the atomized value is
    /// load-bearing — si-copy-002). Otherwise a body that COPIES the context item
    /// (<c>xsl:copy-of select="."</c> or a bare <c>xsl:copy</c>) TRANSMITS it and needs a real node;
    /// anything else (value-of / atomize / unknown) absorbs — the conservative, non-materializing
    /// default that keeps streaming cheap.
    /// </summary>
    public static Usage ClassifyBodyContextItemUsage(
        PhoenixmlDb.Xslt.Ast.XsltSequenceConstructor body, bool selectAtomized)
    {
        System.ArgumentNullException.ThrowIfNull(body);
        if (selectAtomized)
            return Usage.Absorption;

        return FirstEffectiveInstruction(body) switch
        {
            PhoenixmlDb.Xslt.Ast.XsltCopyOf co when IsContextItemRef(co.Select) => Usage.Transmission,
            PhoenixmlDb.Xslt.Ast.XsltCopy => Usage.Transmission,
            _ => Usage.Absorption
        };
    }

    /// <summary>The first instruction that is not insignificant whitespace-only literal text.</summary>
    private static PhoenixmlDb.Xslt.Ast.XsltInstruction? FirstEffectiveInstruction(
        PhoenixmlDb.Xslt.Ast.XsltSequenceConstructor body)
    {
        foreach (var insn in body.Instructions)
        {
            if (insn is PhoenixmlDb.Xslt.Ast.XsltLiteralText { Value: var v } && string.IsNullOrWhiteSpace(v))
                continue;
            return insn;
        }
        return null;
    }

    /// <summary>True when the expression is the context item reference <c>.</c>.</summary>
    private static bool IsContextItemRef(PhoenixmlDb.XQuery.Ast.XQueryExpression? e) =>
        e is PhoenixmlDb.XQuery.Ast.ContextItemExpression;
}
