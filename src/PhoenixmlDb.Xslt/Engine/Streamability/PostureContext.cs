namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// Ambient operand-usage state (XSLT 3.0 §19.6/§19.7) carried through the executor as it
/// descends. Held as a mutable field on the execution context and pushed/popped in place;
/// the root default is <see cref="Usage.Absorption"/> — the most separator-preserving role,
/// so any path reaching the serializer without an explicit push behaves as before.
/// </summary>
public record struct PostureContext
{
    /// <summary>The operand usage currently in effect.</summary>
    public Usage Current { get; private set; }

    /// <summary>Initializes the ambient usage to the root default (<see cref="Usage.Absorption"/>).</summary>
    public PostureContext() => Current = Usage.Absorption;

    /// <summary>Set the ambient usage, returning the previous value for the caller to restore via <see cref="Pop"/>.</summary>
    public Usage Push(Usage u)
    {
        var previous = Current;
        Current = u;
        return previous;
    }

    /// <summary>Restore the ambient usage saved by <see cref="Push"/>.</summary>
    public void Pop(Usage previous) => Current = previous;
}
