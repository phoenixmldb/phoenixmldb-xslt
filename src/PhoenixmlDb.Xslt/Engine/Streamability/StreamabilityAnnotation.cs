using System.Runtime.CompilerServices;

namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// A side-table that caches the computed <see cref="PostureSweep"/> for an AST node,
/// keyed by node identity, WITHOUT modifying the AST classes. Using a
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> keeps the annotation lifetime tied to
/// the node (entries are collected when the node is) and leaves the existing AST types
/// entirely untouched (zero-touch, Task 0.2).
/// </summary>
/// <remarks>
/// Thread-safety: the <see cref="ConditionalWeakTable{TKey, TValue}"/> members used here
/// (<c>AddOrUpdate</c> and <c>TryGetValue</c>) are documented as thread-safe, so concurrent
/// annotation and lookup are safe without external locking.
/// </remarks>
public static class StreamabilityAnnotation
{
    // The struct is boxed into object so it can live in the reference-keyed table.
    private static readonly ConditionalWeakTable<object, object> Table = new();

    /// <summary>
    /// Annotates <paramref name="astNode"/> with the given <paramref name="ps"/>, replacing any
    /// existing annotation for that node.
    /// </summary>
    /// <param name="astNode">The AST node to annotate (used as an identity key).</param>
    /// <param name="ps">The posture/sweep to cache for the node.</param>
    public static void Set(object astNode, PostureSweep ps) => Table.AddOrUpdate(astNode, ps);

    /// <summary>
    /// Retrieves a previously set annotation for <paramref name="astNode"/>.
    /// </summary>
    /// <param name="astNode">The AST node to look up.</param>
    /// <param name="ps">The cached posture/sweep, if present; otherwise <c>default</c>.</param>
    /// <returns><c>true</c> if an annotation exists for the node; otherwise <c>false</c>.</returns>
    public static bool TryGet(object astNode, out PostureSweep ps)
    {
        if (Table.TryGetValue(astNode, out var boxed) && boxed is PostureSweep found)
        {
            ps = found;
            return true;
        }

        ps = default;
        return false;
    }
}
