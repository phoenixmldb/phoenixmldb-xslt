using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents xsl:use-package (XSLT 3.0).
/// </summary>
public sealed class XsltUsePackage
{
    public required string Name { get; init; }
    public string? PackageVersion { get; init; }
    public List<XsltAccept> Accepts { get; init; } = new();
    public List<XsltOverride> Overrides { get; init; } = new();
}
