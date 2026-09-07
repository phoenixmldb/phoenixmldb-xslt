using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Behavior when no template matches in a mode.
/// </summary>
public enum OnNoMatchBehavior
{
    /// <summary>Perform a deep copy of the node.</summary>
    DeepCopy,
    /// <summary>Perform a shallow copy of the node.</summary>
    ShallowCopy,
    /// <summary>Skip the node entirely.</summary>
    DeepSkip,
    /// <summary>Skip only the node (not descendants).</summary>
    ShallowSkip,
    /// <summary>Output text nodes, skip other nodes.</summary>
    TextOnlyCopy,
    /// <summary>Raise an error.</summary>
    Fail
}
