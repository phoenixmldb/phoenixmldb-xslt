using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:array-member instruction (XSLT 3.0).
/// </summary>
public sealed class XsltArrayMember : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitArrayMember(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateArrayMemberAsync(this).ConfigureAwait(false);
    }
}
