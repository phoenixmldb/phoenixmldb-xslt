using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:array instruction (XSLT 3.0).
/// </summary>
public sealed class XsltArray : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }

    /// <summary>
    /// The <c>select</c> attribute. XSLT 4.0 allows the array's value to come from an
    /// expression instead of a sequence constructor; <c>&lt;xsl:array select="1 to 5"/&gt;</c>
    /// is the common form and was previously parsed as an array with no content, yielding [].
    /// </summary>
    public XQueryExpression? Select { get; init; }

    /// <summary>
    /// The <c>composite</c> attribute, default false. When false each ITEM of the value
    /// becomes its own member; when true the whole value becomes a single member.
    /// </summary>
    public bool Composite { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitArray(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateArrayAsync(this).ConfigureAwait(false);
    }
}
