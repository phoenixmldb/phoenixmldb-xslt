using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an XSLT pattern (used in match attributes).
/// </summary>
public abstract class XsltPattern
{
    /// <summary>
    /// Tests if a node matches this pattern.
    /// </summary>
    public abstract bool Matches(object node, XsltContext context);

    /// <summary>
    /// Computes the default priority per XSLT 3.0 spec section 6.5.
    /// </summary>
    public abstract double DefaultPriority { get; }

    /// <summary>
    /// Tests if a node matches the pattern's node test (excluding predicates).
    /// This is useful for computing position() context in pattern predicates.
    /// Default implementation returns the same as Matches with position=1.
    /// </summary>
    public virtual bool MatchesNodeTest(object node) => false;
}
