using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:perform-sort instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltPerformSort : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitPerformSort(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.PerformSortAsync(this).ConfigureAwait(false);
    }
}
