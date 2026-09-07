using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:merge-source child of xsl:merge.
/// </summary>
public sealed class XsltMergeSource
{
    public string? Name { get; init; }
    public XQueryExpression? ForEachItem { get; init; }
    public XQueryExpression? ForEachSource { get; init; }
    public required XQueryExpression Select { get; init; }
    public bool SortBeforeMerge { get; init; }

    /// <summary>
    /// XSLT 3.0 streaming hint: <c>streamable="yes"</c> on xsl:merge-source.
    /// Today the engine accepts and parses the attribute but executes the merge
    /// non-streaming (each source is fully materialized before K-way merge runs).
    /// Reserved for future XmlReader-driven incremental merge — when the source
    /// is paired with for-each-source URIs, a streamable runtime would open each
    /// URI as XmlReader and pull items one at a time through the K-way merge,
    /// keeping the watermark to N items rather than the full N×size sequences.
    /// </summary>
    public bool Streamable { get; init; }

    public List<XsltMergeKey> MergeKeys { get; init; } = new();
    public List<QName> UseAccumulators { get; init; } = new();
    public SourceLocation? Location { get; init; }
}
