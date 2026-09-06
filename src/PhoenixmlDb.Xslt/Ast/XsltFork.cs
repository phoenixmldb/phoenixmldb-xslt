using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:fork instruction (XSLT 3.0).
/// </summary>
public sealed class XsltFork : XsltInstruction
{
    public List<XsltForEachGroup> ForEachGroups { get; init; } = new();
    public List<XsltSequenceConstructor> Sequences { get; init; } = new();
    public List<XsltResultDocument> ResultDocuments { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitFork(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForkAsync(this).ConfigureAwait(false);
    }
}
