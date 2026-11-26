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

    /// <summary>
    /// This instruction with other operands and every other property kept: for the rewrite of
    /// <c>root()</c> in a rule for the document node (Engine.RootOfDocumentRewrite, BUGS.md #166).
    /// </summary>
    internal XsltForEachGroup WithSelect(XQueryExpression select)
        => KeepSettings(new XsltForEachGroup
        {
            Select = select, GroupBy = GroupBy, GroupAdjacent = GroupAdjacent, GroupStartingWith = GroupStartingWith,
            GroupEndingWith = GroupEndingWith, Collation = Collation, Composite = Composite, Sorts = Sorts, Body = Body,
            Location = Location,
        });
}
