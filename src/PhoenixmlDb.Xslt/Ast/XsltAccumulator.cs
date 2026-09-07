using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:accumulator (XSLT 3.0).
/// </summary>
public sealed class XsltAccumulator
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public required XQueryExpression InitialValue { get; init; }
    public required List<XsltAccumulatorRule> Rules { get; init; }
    public bool Streamable { get; init; }
    /// <summary>Original lexical name from the name attribute (for XTSE3350 duplicate detection).</summary>
    public string SourceName { get; init; } = "";

    /// <summary>
    /// The library package (via xsl:use-package) that declared this accumulator. Accumulators
    /// are package-local, so when a used package and the using package both declare an
    /// accumulator with the same name, a component of the used package must resolve
    /// accumulator-before/after to ITS OWN package's accumulator, not the merged one
    /// (override-misc-005). Null for locally-declared accumulators.
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }
}
