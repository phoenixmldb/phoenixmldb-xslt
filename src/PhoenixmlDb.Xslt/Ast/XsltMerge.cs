using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:merge instruction (XSLT 3.0).
/// Merges multiple pre-sorted input sequences.
/// </summary>
public sealed class XsltMerge : XsltInstruction
{
    public List<XsltMergeSource> Sources { get; init; } = new();
    public required XsltSequenceConstructor Action { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMerge(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.MergeAsync(this).ConfigureAwait(false);
    }
}
