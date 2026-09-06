using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:map-entry instruction (XSLT 3.0).
/// </summary>
public sealed class XsltMapEntry : XsltInstruction
{
    public required XQueryExpression Key { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMapEntry(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateMapEntryAsync(this).ConfigureAwait(false);
    }
}
