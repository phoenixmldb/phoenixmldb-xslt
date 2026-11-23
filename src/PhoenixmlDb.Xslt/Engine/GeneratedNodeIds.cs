using System.Runtime.CompilerServices;
using PhoenixmlDb.Xdm.Nodes;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The value of fn:generate-id for a node: the same for the same node, different for different
/// nodes (XPath F&amp;O 3.1 §14.6.4), and an NCName. It is made from the key node identity uses
/// (<see cref="XdmNode.DocumentOrderKey"/>, which `is` compares). It was the hash code of the node
/// object: two nodes could get the same id (a W3C run of streamable-007 lost one of its 929
/// distinct chapter ids of streamable-007), and two objects for one node got different ones.
/// </summary>
internal static class GeneratedNodeIds
{
    // A node with no id (a parentless attribute or text node made on its own) has no key; it is
    // given a number of its own the first time, kept for as long as the node lives.
    private static readonly ConditionalWeakTable<XdmNode, string> s_unkeyed = new();
    private static long s_nextUnkeyed;

    internal static string For(object node)
    {
        if (node is not XdmNode xdm)
            return $"x{RuntimeHelpers.GetHashCode(node):x}";
        if (xdm.Id.Value != 0)
        {
            // In the engine's own store a node id alone is unique, and the tree ordinal is a
            // process-wide count (xslt#542): it would make the id depend on how many trees the
            // process built before. Its nodes keep the id they had, with 0 for the tree.
            var tree = xdm.Store is XdmInMemoryStore ? 0UL : xdm.TreeOrdinal;
            return $"d{tree:x}n{xdm.Id.Value:x}";
        }
        return s_unkeyed.GetValue(xdm, static _ => $"u{Interlocked.Increment(ref s_nextUnkeyed):x}");
    }
}
