using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:document instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltDocument : XsltInstruction
{
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitDocument(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateDocumentAsync(this).ConfigureAwait(false);
    }
}
