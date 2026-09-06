using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:where-populated - executes content and includes result only if non-empty.
/// </summary>
public sealed class XsltWherePopulated : XsltInstruction
{
    public required XsltSequenceConstructor Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitWherePopulated(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.WherePopulatedAsync(this);
}
