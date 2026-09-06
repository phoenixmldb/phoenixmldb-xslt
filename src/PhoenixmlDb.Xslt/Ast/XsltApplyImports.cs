using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:apply-imports instruction.
/// </summary>
public sealed class XsltApplyImports : XsltInstruction
{
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitApplyImports(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ApplyImportsAsync(WithParams).ConfigureAwait(false);
    }
}
