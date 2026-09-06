using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Captures an <c>xsl:import-schema</c> declaration. Resolved against the runtime
/// <c>ISchemaProvider</c> when the stylesheet is loaded into an <c>XsltTransformer</c>.
/// </summary>
public sealed class XsltSchemaImport
{
    /// <summary>The target namespace URI of the schema to import. Empty string for the no-namespace schema.</summary>
    public required string TargetNamespace { get; init; }

    /// <summary>Optional namespace prefix declared by the import. Null when the import has no namespace= attribute.</summary>
    public string? Prefix { get; init; }

    /// <summary>Schema-location hints from the schema-location attribute (space-separated URIs).</summary>
    public IReadOnlyList<string> SchemaLocations { get; init; } = Array.Empty<string>();

    /// <summary>Source location of the xsl:import-schema element (for diagnostic messages).</summary>
    public SourceLocation? Location { get; init; }
}
