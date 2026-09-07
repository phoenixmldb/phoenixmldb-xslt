using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:number instruction.
/// </summary>
public sealed class XsltNumber : XsltInstruction
{
    public XQueryExpression? Value { get; init; }
    public XQueryExpression? Select { get; init; }
    public NumberLevel Level { get; init; } = NumberLevel.Single;
    public XsltPattern? Count { get; init; }
    public XsltPattern? From { get; init; }
    public XsltAttributeValueTemplate? Format { get; init; }
    public XsltAttributeValueTemplate? Lang { get; init; }
    public XsltAttributeValueTemplate? LetterValue { get; init; }
    public XsltAttributeValueTemplate? OrdinalValue { get; init; }
    public XsltAttributeValueTemplate? GroupingSeparator { get; init; }
    public XsltAttributeValueTemplate? GroupingSize { get; init; }
    public XsltAttributeValueTemplate? StartAt { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNumber(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.NumberAsync(this).ConfigureAwait(false);
    }
}
