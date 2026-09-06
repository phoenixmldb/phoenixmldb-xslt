using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// A no-op instruction (used for xsl:fallback inside supported instructions).
/// </summary>
public sealed class XsltNoOp : XsltInstruction
{
    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNoOp(this);
    public override ValueTask ExecuteAsync(XsltExecutionContext context) => ValueTask.CompletedTask;
}
