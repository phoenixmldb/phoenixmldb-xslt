using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:output declaration.
/// </summary>
public sealed class XsltOutput
{
    public QName? Name { get; init; }
    /// <summary>Import precedence level: 0 = highest (main stylesheet), higher = lower precedence.</summary>
    public int ImportPrecedence { get; set; }
    public OutputMethod? Method { get; init; }
    public string? Version { get; init; }
    public string? Encoding { get; init; }
    public bool? OmitXmlDeclaration { get; init; }
    public bool? Standalone { get; init; }
    public string? DoctypePublic { get; init; }
    public string? DoctypeSystem { get; init; }
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public HashSet<QName>? CdataSectionElements { get; set; }
#pragma warning restore CA2227
    public bool? Indent { get; init; }
    public string? MediaType { get; init; }
    public bool? IncludeContentType { get; init; }
    public bool? EscapeUriAttributes { get; init; }
    public bool? UndeclarePrefixes { get; init; }
    public string? NormalizationForm { get; init; }
    public string? ItemSeparator { get; init; }
    public string? HtmlVersion { get; init; }
    public string? BuildTree { get; init; }
    public bool? AllowDuplicateNames { get; init; }
    public List<QName> UseCharacterMaps { get; init; } = new();

    /// <summary>
    /// The library package (used via xsl:use-package) that declared this xsl:output. Named
    /// output declarations and their referenced xsl:character-maps are LOCAL to their
    /// declaring package (XSLT 3.0 §3.6.7): xsl:result-document/@format naming this output
    /// must resolve against, and its use-character-maps must be looked up in, THAT package
    /// (use-package-108 / use-package-108b). Null for outputs declared in the principal
    /// package; its character maps then resolve against the principal registry as before.
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    /// <summary>
    /// Space-separated list of element QNames whose content should NOT be indented
    /// even when indent="yes" is specified on xsl:output.
    /// </summary>
    public HashSet<QName>? SuppressIndentation { get; set; }
#pragma warning restore CA2227
    /// <summary>
    /// When true, a UTF-8 BOM (U+FEFF) is prepended to the serialized output.
    /// </summary>
    public bool? ByteOrderMark { get; init; }
    /// <summary>
    /// Controls how XML/HTML nodes are serialized when they appear inside JSON output (method="json").
    /// Default is "xml".
    /// </summary>
    public string? JsonNodeOutputMethod { get; init; }

    /// <summary>
    /// Returns the effective output method, defaulting to Xml if not explicitly specified.
    /// </summary>
    public OutputMethod EffectiveMethod => Method ?? OutputMethod.Xml;

    /// <summary>
    /// Returns a shallow copy of this declaration with an overridden output method. Used when the
    /// method was not specified on xsl:output and the serializer resolves the default output
    /// method dynamically from the result tree (Serialization 4.0 §Default Output Method): an
    /// <c>html</c> document element selects the html method (no namespace) or the xhtml method
    /// (XHTML namespace). Every other serialization parameter is preserved unchanged.
    /// </summary>
    internal XsltOutput CloneWithMethod(OutputMethod method)
    {
        var clone = new XsltOutput
        {
            Name = Name,
            Method = method,
            Version = Version,
            Encoding = Encoding,
            OmitXmlDeclaration = OmitXmlDeclaration,
            Standalone = Standalone,
            DoctypePublic = DoctypePublic,
            DoctypeSystem = DoctypeSystem,
            Indent = Indent,
            MediaType = MediaType,
            IncludeContentType = IncludeContentType,
            EscapeUriAttributes = EscapeUriAttributes,
            UndeclarePrefixes = UndeclarePrefixes,
            NormalizationForm = NormalizationForm,
            ItemSeparator = ItemSeparator,
            HtmlVersion = HtmlVersion,
            BuildTree = BuildTree,
            AllowDuplicateNames = AllowDuplicateNames,
            ByteOrderMark = ByteOrderMark,
            JsonNodeOutputMethod = JsonNodeOutputMethod,
        };
        clone.ImportPrecedence = ImportPrecedence;
        clone.CdataSectionElements = CdataSectionElements;
        clone.PackageStylesheet = PackageStylesheet;
        clone.SuppressIndentation = SuppressIndentation;
        foreach (var m in UseCharacterMaps)
            clone.UseCharacterMaps.Add(m);
        return clone;
    }

    /// <summary>
    /// Returns a shallow copy of this declaration with an overridden set of cdata-section-elements.
    /// Used by xsl:result-document to expose the effective union (its own cdata-section-elements plus
    /// those of its matched xsl:output) during content emission, without mutating the shared
    /// xsl:output AST node. Every other serialization parameter is preserved unchanged.
    /// </summary>
    internal XsltOutput CloneWithCdataSectionElements(HashSet<QName> cdataSectionElements)
    {
        var clone = new XsltOutput
        {
            Name = Name,
            Method = Method,
            Version = Version,
            Encoding = Encoding,
            OmitXmlDeclaration = OmitXmlDeclaration,
            Standalone = Standalone,
            DoctypePublic = DoctypePublic,
            DoctypeSystem = DoctypeSystem,
            Indent = Indent,
            MediaType = MediaType,
            IncludeContentType = IncludeContentType,
            EscapeUriAttributes = EscapeUriAttributes,
            UndeclarePrefixes = UndeclarePrefixes,
            NormalizationForm = NormalizationForm,
            ItemSeparator = ItemSeparator,
            HtmlVersion = HtmlVersion,
            BuildTree = BuildTree,
            AllowDuplicateNames = AllowDuplicateNames,
            ByteOrderMark = ByteOrderMark,
            JsonNodeOutputMethod = JsonNodeOutputMethod,
        };
        clone.ImportPrecedence = ImportPrecedence;
        clone.CdataSectionElements = cdataSectionElements;
        clone.PackageStylesheet = PackageStylesheet;
        clone.SuppressIndentation = SuppressIndentation;
        foreach (var m in UseCharacterMaps)
            clone.UseCharacterMaps.Add(m);
        return clone;
    }
}
