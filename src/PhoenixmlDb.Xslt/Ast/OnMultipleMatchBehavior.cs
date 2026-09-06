using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Behavior when multiple templates match in a mode.
/// </summary>
public enum OnMultipleMatchBehavior
{
    /// <summary>Use the last matching template (highest import precedence, then last in document order).</summary>
    UseLast,
    /// <summary>Raise an error when multiple templates match.</summary>
    Fail
}
