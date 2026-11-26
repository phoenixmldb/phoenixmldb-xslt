using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:for-each instruction.
/// </summary>
public sealed class XsltForEach : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitForEach(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForEachAsync(Select, Sorts, Body).ConfigureAwait(false);
    }

    /// <summary>
    /// This instruction with other operands and every other property kept: for the rewrite of
    /// <c>root()</c> in a rule for the document node (Engine.RootOfDocumentRewrite, BUGS.md #166).
    /// </summary>
    internal XsltForEach WithSelect(XQueryExpression select)
        => KeepSettings(new XsltForEach { Select = select, Sorts = Sorts, Body = Body, Location = Location });
}
