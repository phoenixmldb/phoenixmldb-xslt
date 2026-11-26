using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:copy-of instruction.
/// </summary>
public sealed class XsltCopyOf : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public bool? CopyNamespaces { get; init; }
    public bool? CopyAccumulators { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }

    /// <summary>The schema type the <c>type</c> attribute names, which the node is validated against; null when there is none.</summary>
    public XsltTypeName? ValidationType { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCopyOf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CopyOfAsync(this).ConfigureAwait(false);
    }

    /// <summary>
    /// This instruction with other operands and every other property kept: for the rewrite of
    /// <c>root()</c> in a rule for the document node (Engine.RootOfDocumentRewrite, BUGS.md #166).
    /// </summary>
    internal XsltCopyOf WithSelect(XQueryExpression select)
        => KeepSettings(new XsltCopyOf
        {
            Select = select, CopyNamespaces = CopyNamespaces, CopyAccumulators = CopyAccumulators, Validation = Validation,
            Type = Type, ValidationType = ValidationType, Location = Location,
        });
}
