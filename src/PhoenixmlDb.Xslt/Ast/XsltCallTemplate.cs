using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:call-template instruction.
/// </summary>
public sealed class XsltCallTemplate : XsltInstruction
{
    public required QName Name { get; init; }
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCallTemplate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CallTemplateAsync(Name, WithParams).ConfigureAwait(false);
    }
}
