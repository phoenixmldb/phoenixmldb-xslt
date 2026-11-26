using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:variable instruction.
/// </summary>
public sealed class XsltVariableInstruction : XsltInstruction
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public Uri? BaseUri { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitVariableInstruction(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.BindVariableAsync(this).ConfigureAwait(false);
    }

    /// <summary>
    /// This instruction with other operands and every other property kept: for the rewrite of
    /// <c>root()</c> in a rule for the document node (Engine.RootOfDocumentRewrite, BUGS.md #166).
    /// </summary>
    internal XsltVariableInstruction WithOperands(XQueryExpression? select, XsltSequenceConstructor? content)
        => KeepSettings(new XsltVariableInstruction { Name = Name, As = As, Select = select, Content = content, BaseUri = BaseUri, Location = Location });
}
