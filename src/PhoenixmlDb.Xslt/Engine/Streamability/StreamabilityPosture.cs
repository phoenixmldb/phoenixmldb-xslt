namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// The posture of an expression's result with respect to a streamed document, per
/// XSLT 3.0 §19.1. Posture describes the shape/position of the nodes an expression
/// may return relative to the current streaming context.
/// </summary>
public enum Posture
{
    /// <summary>No streamed nodes are returned (atomics, or copied/detached nodes).</summary>
    Grounded,

    /// <summary>A set of nodes at the same level, all descendants-or-self of the context (e.g. child-axis paths).</summary>
    Striding,

    /// <summary>Nodes on the descendant/descendant-or-self axis, possibly nested.</summary>
    Crawling,

    /// <summary>Nodes reached by moving up toward ancestors of the context node.</summary>
    Climbing,

    /// <summary>Nodes at arbitrary positions relative to the context (not streamable).</summary>
    Roaming,

    /// <summary>Result whose posture cannot be determined by the composition rules (not streamable).</summary>
    Artistic
}

/// <summary>
/// The sweep of an expression with respect to the streamed input, per XSLT 3.0 §19.1.
/// Sweep describes how much of the streamed input the expression consumes.
/// </summary>
public enum Sweep
{
    /// <summary>Consumes no streamed input.</summary>
    Motionless,

    /// <summary>Consumes streamed input in a single forward pass.</summary>
    Consuming,

    /// <summary>Requires arbitrary (backward/repeated) access to the input (not streamable).</summary>
    FreeRanging
}

/// <summary>
/// The composed streamability properties (posture and sweep) of an expression,
/// per the XSLT 3.0 §19 compositional streamability model.
/// </summary>
/// <param name="Posture">The result posture of the expression.</param>
/// <param name="Sweep">The input sweep of the expression.</param>
public readonly record struct PostureSweep(Posture Posture, Sweep Sweep)
{
    /// <summary>
    /// Whether this posture/sweep combination is guaranteed-streamable per XSLT 3.0 §19.8.6:
    /// posture is grounded/striding/crawling/climbing (not roaming/artistic) AND
    /// sweep is motionless or consuming (not free-ranging).
    /// </summary>
    public bool IsGuaranteedStreamable =>
        Posture is Posture.Grounded or Posture.Striding or Posture.Crawling or Posture.Climbing
        && Sweep is Sweep.Motionless or Sweep.Consuming;
}
