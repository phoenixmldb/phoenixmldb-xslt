using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:expose declaration (XSLT 3.0).
/// Changes visibility of components within the declaring package.
/// </summary>
public sealed class ExposeDeclaration
{
    public string? Component { get; init; }
    public string? Names { get; init; }
    public Visibility Visibility { get; init; }
    public System.Xml.Linq.XElement? Element { get; init; }
}
