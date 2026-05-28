namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Pairs a streaming path matcher with an <c>xsl:for-each</c> body that should
/// execute every time the matcher fires a StartElement match on the input stream.
/// </summary>
/// <remarks>
/// Registered by the streaming scanner during compile, dispatched by the
/// streaming processor as the source document is read, and wired into a
/// for-each's source document by the streaming source-document handler.
/// </remarks>
internal sealed class ForEachSubscription
{
    /// <summary>The original <c>xsl:for-each</c> AST node this subscription was derived from.</summary>
    public required Ast.XsltForEach SourceInstruction { get; init; }

    /// <summary>Path matcher fired against the input stream to identify match events.</summary>
    public required StreamPathMatcher PathMatcher { get; init; }

    /// <summary>Sequence constructor body executed once per match.</summary>
    public required Ast.XsltSequenceConstructor Body { get; init; }
}
