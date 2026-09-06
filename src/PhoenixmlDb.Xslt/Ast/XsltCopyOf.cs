using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:copy-of instruction.
/// </summary>
public sealed class XsltCopyOf : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public bool? CopyNamespaces { get; init; }
    public bool? CopyAccumulators { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCopyOf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CopyOfAsync(this).ConfigureAwait(false);
    }
}
