using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Type annotations handling.
/// </summary>
public enum TypeAnnotations
{
    Unspecified,
    Strip,
    Preserve
}
