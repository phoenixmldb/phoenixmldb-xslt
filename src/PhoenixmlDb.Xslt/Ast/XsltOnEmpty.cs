using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:on-empty - provides fallback content when parent produces no output.
/// </summary>
public sealed class XsltOnEmpty : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }
    public XQueryExpression? Select { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitOnEmpty(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.OnEmptyAsync(this);
}
