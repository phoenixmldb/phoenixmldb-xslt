using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:key declaration.
/// </summary>
public sealed class XsltKey
{
    public required QName Name { get; init; }
    public required XsltPattern Match { get; init; }
    public XQueryExpression? Use { get; init; }
    public XsltSequenceConstructor? UseContent { get; init; }
    public string? Collation { get; init; }
    public bool Composite { get; init; }

    /// <summary>
    /// Additional key definitions with the same name.
    /// Per XSLT spec, multiple xsl:key declarations with the same name
    /// all contribute to the same key index (union of matches).
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public List<XsltKey>? OtherDefinitions { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// The package (stylesheet) that declared this key. Keys are LOCAL to their
    /// declaring package (XSLT 3.0 §3.6.2): a key name is resolvable only from code
    /// in the same package. <c>null</c> means the principal (top-level) package.
    /// Used to filter <see cref="AllDefinitions"/> to the calling package so that a
    /// used-package key does not leak into the using package and same-named keys in
    /// different packages index independently (use-package-102 / use-package-105).
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }

    /// <summary>
    /// Returns all definitions for this key name (including this one).
    /// </summary>
    public IEnumerable<XsltKey> AllDefinitions
    {
        get
        {
            yield return this;
            if (OtherDefinitions != null)
                foreach (var other in OtherDefinitions)
                    yield return other;
        }
    }
}
