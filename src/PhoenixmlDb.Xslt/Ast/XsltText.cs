using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:text instruction.
/// </summary>
public sealed class XsltText : XsltInstruction
{
    public required string Value { get; init; }
    public bool DisableOutputEscaping { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitText(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        // Use WriteTextItem for sequence accumulation support (but WriteText if DOE is set)
        if (DisableOutputEscaping)
            context.WriteText(Value, true);
        else
            context.WriteTextItem(Value);
        return ValueTask.CompletedTask;
    }
}
