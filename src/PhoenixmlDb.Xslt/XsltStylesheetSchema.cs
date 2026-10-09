using PhoenixmlDb.Core.Schema;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// The schema for XSLT 3.0 stylesheets, for a host that wants to check a stylesheet's structure
/// before compiling it (an editor, a store that accepts stylesheets).
/// </summary>
/// <remarks>
/// <para>
/// The W3C publishes this schema in XSD 1.1, which this processor does not implement. What is
/// supplied here is a copy an XSD 1.0 processor loads; the file states every change made to it.
/// </para>
/// <para>
/// The copy checks less than the original. The original states 71 rules as assertions (for
/// example: <c>xsl:value-of</c> has a <c>select</c> attribute or content, not both), and XSD 1.0
/// has no assertions, so those rules are not checked. A stylesheet this schema accepts can
/// still have a static error; a simplified stylesheet (a literal result element as the root)
/// is outside what it describes.
/// </para>
/// </remarks>
public static class XsltStylesheetSchema
{
    private const string ResourceName = "PhoenixmlDb.Xslt.Schemas.xslt30-xsd10.xsd";

    /// <summary>The namespace the schema describes.</summary>
    public const string TargetNamespace = "http://www.w3.org/1999/XSL/Transform";

    /// <summary>The URI the schema document is known by when compiled from here.</summary>
    public static Uri DocumentUri { get; } = new("urn:phoenixmldb:xslt:schema-for-xslt30-xsd10");

    /// <summary>The schema document, as a source for <see cref="SchemaCompiler"/>.</summary>
    public static SchemaSource Source { get; } =
        SchemaSource.FromResource(typeof(XsltStylesheetSchema).Assembly, ResourceName, DocumentUri);

    /// <summary>Opens the schema document.</summary>
    public static Stream Open() =>
        typeof(XsltStylesheetSchema).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"The embedded schema '{ResourceName}' is missing from the assembly.");

    /// <summary>
    /// Compiles the schema. Nothing outside the assembly is read. The result is immutable and
    /// can be kept and shared.
    /// </summary>
    public static CompiledSchema Compile() =>
        SchemaCompiler.Compile([DocumentUri], SchemaAccessGate.SuppliedOnly, [Source]);
}
