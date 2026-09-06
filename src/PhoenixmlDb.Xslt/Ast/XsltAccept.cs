using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Accept declaration in use-package.
/// </summary>
public sealed class XsltAccept
{
    public required string Component { get; init; }
    public required string Names { get; init; }
    public Visibility Visibility { get; init; }
}
