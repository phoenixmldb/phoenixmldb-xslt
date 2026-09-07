using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Constraint on the context item for a template.
/// </summary>
public enum ContextItemUse
{
    Optional,
    Required,
    Absent
}
