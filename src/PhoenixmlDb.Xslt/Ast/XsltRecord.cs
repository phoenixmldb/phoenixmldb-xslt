using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:record instruction (XSLT 4.0).
/// Constructs a record (map with string keys) from xsl:entry children.
/// </summary>
public sealed class XsltRecord : XsltInstruction
{
    public List<(string Name, XsltSequenceConstructor Value)> Entries { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitRecord(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateRecordAsync(this).ConfigureAwait(false);
    }
}
