using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Text value template (TVT) — text with embedded {expr} expressions when expand-text="yes".
/// </summary>
public sealed class XsltTextValueTemplate : XsltInstruction
{
    public required XsltAttributeValueTemplate Template { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitTextValueTemplate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in Template.Parts)
        {
            sb.Append(await part.EvaluateAsync(context).ConfigureAwait(false));
        }
        // Use WriteTextItem for sequence accumulation support (same as XsltText)
        context.WriteTextItem(sb.ToString());
    }
}
