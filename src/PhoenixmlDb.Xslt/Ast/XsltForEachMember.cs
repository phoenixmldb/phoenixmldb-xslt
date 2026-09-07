using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:for-each-member instruction (XSLT 4.0).
/// Iterates over members of an array.
/// </summary>
public sealed class XsltForEachMember : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitForEachMember(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForEachMemberAsync(this).ConfigureAwait(false);
    }
}
