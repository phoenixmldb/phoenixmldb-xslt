using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:map instruction (XSLT 3.0).
/// </summary>
public sealed class XsltMap : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMap(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateMapAsync(this).ConfigureAwait(false);
    }
}
