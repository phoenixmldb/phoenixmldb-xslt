using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:for-each-group instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltForEachGroup : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public XQueryExpression? GroupBy { get; init; }
    public XQueryExpression? GroupAdjacent { get; init; }
    public XsltPattern? GroupStartingWith { get; init; }
    public XsltPattern? GroupEndingWith { get; init; }
    public XsltAttributeValueTemplate? Collation { get; init; }
    /// <summary>
    /// XSLT 3.0: If true, group-by/group-adjacent evaluates to a sequence of values
    /// that are treated as a composite key.
    /// </summary>
    public bool Composite { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitForEachGroup(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForEachGroupAsync(this).ConfigureAwait(false);
    }
}
