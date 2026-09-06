using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:on-non-empty - includes content only when parent produces non-empty output.
/// </summary>
public sealed class XsltOnNonEmpty : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }
    public XQueryExpression? Select { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitOnNonEmpty(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.OnNonEmptyAsync(this);
}
