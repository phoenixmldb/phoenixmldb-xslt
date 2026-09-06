using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:decimal-format.
/// </summary>
public sealed class XsltDecimalFormat
{
    public QName? Name { get; init; }
    public string DecimalSeparator { get; init; } = ".";
    public string GroupingSeparator { get; init; } = ",";
    public string Infinity { get; init; } = "Infinity";
    public string MinusSign { get; init; } = "-";
    public string NaN { get; init; } = "NaN";
    public string Percent { get; init; } = "%";
    public string PerMille { get; init; } = "\u2030";
    public string ZeroDigit { get; init; } = "0";
    public string Digit { get; init; } = "#";
    public string PatternSeparator { get; init; } = ";";
    public string ExponentSeparator { get; init; } = "e";
    /// <summary>Tracks which attributes were explicitly set (vs defaulted) for merge conflict detection.</summary>
    public HashSet<string> ExplicitAttributes { get; init; } = [];
    /// <summary>
    /// Indicates a same-precedence conflict was detected during merging.
    /// Will be resolved if a higher-precedence declaration overrides it.
    /// </summary>
    public bool HasConflict { get; set; }
    /// <summary>Description of the conflict for error reporting.</summary>
    public string? ConflictDescription { get; set; }
}
