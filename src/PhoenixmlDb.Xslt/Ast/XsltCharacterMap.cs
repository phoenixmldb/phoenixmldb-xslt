using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:character-map.
/// </summary>
public sealed class XsltCharacterMap
{
    public required QName Name { get; init; }
    public List<QName> UseCharacterMaps { get; init; } = new();

    /// <summary>
    /// Character-to-replacement mappings keyed by Unicode <em>code point</em> (not UTF-16
    /// code unit). Keying on the code point rather than <c>char</c> lets astral characters
    /// (those above U+FFFF, represented in the source as a surrogate pair) participate in
    /// character maps — see character-map-007/010. A <c>char</c> key still works because it
    /// widens implicitly to its code point.
    /// </summary>
    public required Dictionary<int, string> Mappings { get; init; }
}
