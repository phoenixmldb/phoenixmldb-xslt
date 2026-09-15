using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:assert instruction (XSLT 3.0).
/// </summary>
public sealed class XsltAssert : XsltInstruction
{
    public required XQueryExpression Test { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>@error-code as the attribute value template XSLT 3.0 §23.2 defines it (xslt#112).</summary>
    public XsltAttributeValueTemplate? ErrorCodeAvt { get; init; }

    /// <summary>The namespaces in scope on the xsl:assert element, for resolving a prefixed error code.</summary>
    public IReadOnlyDictionary<string, string>? ErrorCodeNamespaces { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAssert(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.AssertAsync(this).ConfigureAwait(false);
    }
}
