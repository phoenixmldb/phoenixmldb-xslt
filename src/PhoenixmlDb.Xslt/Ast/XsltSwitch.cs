using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:switch instruction (XSLT 4.0).
/// Like xsl:choose but with a select expression that provides the context for each test.
/// </summary>
public sealed class XsltSwitch : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public required IReadOnlyList<XsltWhen> When { get; init; }
    public XsltSequenceConstructor? Otherwise { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSwitch(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.SwitchAsync(this).ConfigureAwait(false);
    }
}
