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

    /// <summary>
    /// Set by the parser when this is the last instruction a template body executes (directly, or as the
    /// last instruction of the chosen xsl:choose branch or xsl:if) and nothing inspects its output
    /// afterwards. Such a call may run as a tail call — see <see cref="XsltExecutionContext.TryScheduleTailCallAsync"/>.
    /// </summary>
    public bool IsTailCall { get; set; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCallTemplate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        if (IsTailCall && await context.TryScheduleTailCallAsync(Name, WithParams).ConfigureAwait(false))
            return;
        await context.CallTemplateAsync(Name, WithParams).ConfigureAwait(false);
    }
}
