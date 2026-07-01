namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// The usage role of an operand relative to its containing construct, per the XSLT 3.0
/// operand-usage classification (§19.6, §19.7). Usage determines how an operand's posture
/// and sweep contribute to the composed streamability of the containing construct.
/// </summary>
public enum Usage
{
    /// <summary>§19.6: the operand's streamed nodes are read/consumed by value (their content is absorbed).</summary>
    Absorption,

    /// <summary>§19.6: the operand's nodes are examined for identity/position only, not their content.</summary>
    Inspection,

    /// <summary>§19.6: the operand's nodes are passed through unchanged (transmitted) to the containing result.</summary>
    Transmission,

    /// <summary>§19.7: the operand is used to navigate to related nodes (e.g. an axis step source).</summary>
    Navigation
}
