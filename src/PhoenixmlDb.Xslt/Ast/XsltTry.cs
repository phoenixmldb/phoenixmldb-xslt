using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:try instruction (XSLT 3.0).
/// </summary>
public sealed class XsltTry : XsltInstruction
{
    /// <summary>
    /// The select attribute (XPath expression) - mutually exclusive with Body.
    /// </summary>
    public XQueryExpression? SelectExpression { get; init; }

    /// <summary>
    /// The sequence constructor body - mutually exclusive with SelectExpression.
    /// </summary>
    public XsltSequenceConstructor? Body { get; init; }

    public List<XsltCatch> Catches { get; init; } = new();
    public bool Rollback { get; init; } = true;

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitTry(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.TryAsync(this).ConfigureAwait(false);
    }
}
