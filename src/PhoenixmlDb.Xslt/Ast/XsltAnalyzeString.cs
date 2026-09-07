using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:analyze-string instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltAnalyzeString : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public required XsltAttributeValueTemplate Regex { get; init; }
    public XsltAttributeValueTemplate? Flags { get; init; }
    public XsltSequenceConstructor? MatchingSubstring { get; init; }
    public XsltSequenceConstructor? NonMatchingSubstring { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAnalyzeString(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.AnalyzeStringAsync(this).ConfigureAwait(false);
    }
}
