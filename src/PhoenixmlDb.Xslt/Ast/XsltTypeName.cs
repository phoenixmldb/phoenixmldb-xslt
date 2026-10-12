namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// The schema type that the <c>type</c> attribute of a constructing instruction, or
/// <c>xsl:type</c> on a literal result element, names: the node is validated against it
/// (XSLT 3.0 §25.4.1).
/// </summary>
/// <param name="Namespace">The namespace of the type; empty for a type in no namespace.</param>
/// <param name="LocalName">The local name of the type.</param>
public sealed record XsltTypeName(string Namespace, string LocalName)
{
    /// <inheritdoc />
    public override string ToString() => Namespace.Length == 0 ? LocalName : $"Q{{{Namespace}}}{LocalName}";
}
