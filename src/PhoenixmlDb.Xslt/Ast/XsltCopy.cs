using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:copy instruction.
/// </summary>
public sealed class XsltCopy : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public bool? CopyNamespaces { get; init; }
    public bool? InheritNamespaces { get; init; }
    public List<QName> UseAttributeSets { get; init; } = new();
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCopy(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CopyAsync(this).ConfigureAwait(false);
    }
}
