using System.Xml.Linq;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{
    /// <summary>The namespace of PhoeniXML's own extension serialization parameters and error codes.</summary>
    internal const string PhxNamespace = "https://schemas.phoenixml.dev/2026/functions";

    /// <summary><c>phx:indent-style</c> on xsl:output and xsl:result-document (xslt#499).</summary>
    internal static readonly XName IndentStyleAttribute = XName.Get("indent-style", PhxNamespace);

    /// <summary>
    /// The value of <c>phx:indent-style</c>. Any value but <c>saxon-he-10</c> is XTSE0020: an
    /// attribute value that is not one of the permitted values.
    /// </summary>
    internal static IndentStyle ParseIndentStyle(string value, XElement element)
        => TryParseIndentStyle(value) ?? throw new XsltException(
            $"XTSE0020: phx:indent-style must be \"saxon-he-10\", not \"{value.Trim()}\"", GetSourceLocation(element));

    /// <summary>The style a value names, or null when it names none.</summary>
    internal static IndentStyle? TryParseIndentStyle(string value) => value.Trim() switch
    {
        "saxon-he-10" => IndentStyle.SaxonHe10,
        _ => null,
    };
}
