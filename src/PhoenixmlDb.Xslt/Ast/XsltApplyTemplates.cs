using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:apply-templates instruction.
/// </summary>
public sealed class XsltApplyTemplates : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public QName? Mode { get; init; }
    public bool UseCurrentMode { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitApplyTemplates(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        var effectiveMode = UseCurrentMode ? context.CurrentMode : Mode;
        await context.ApplyTemplatesAsync(Select, effectiveMode, Sorts, WithParams).ConfigureAwait(false);
    }
}
