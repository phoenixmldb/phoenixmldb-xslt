using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Validation mode for schema validation.
/// </summary>
public enum ValidationMode
{
    Strip,
    Preserve,
    Strict,
    Lax
}
