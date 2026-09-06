using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Visibility for package components.
/// </summary>
public enum Visibility
{
    Private,
    Public,
    Final,
    Abstract,
    Hidden
}
