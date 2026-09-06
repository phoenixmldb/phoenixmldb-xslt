using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:namespace instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltNamespace : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNamespace(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateNamespaceAsync(this).ConfigureAwait(false);
    }
}
