using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:param instruction (in template body).
/// </summary>
public sealed class XsltParamInstruction : XsltInstruction
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Required { get; init; }
    public bool Tunnel { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitParamInstruction(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.BindParamAsync(this).ConfigureAwait(false);
    }
}
