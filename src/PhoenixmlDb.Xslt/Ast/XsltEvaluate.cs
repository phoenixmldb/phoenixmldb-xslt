using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:evaluate - dynamically evaluates an XPath expression (XSLT 3.0).
/// </summary>
public sealed class XsltEvaluate : XsltInstruction
{
    public required XQueryExpression Xpath { get; init; }
    public XQueryExpression? ContextItem { get; init; }
    public XsltAttributeValueTemplate? BaseUri { get; init; }
    public XQueryExpression? NamespaceContext { get; init; }
    public XQueryExpression? WithParamsExpr { get; init; }
    public XdmSequenceType? As { get; init; }
    public string? EvaluateDefaultCollation { get; init; }
    public List<XsltWithParam> WithParams { get; init; } = [];
    public XsltSequenceConstructor? Fallback { get; init; }
    /// <summary>
    /// Default in-scope namespace bindings (prefix → URI) from the xsl:evaluate element.
    /// Used when namespace-context is not specified.
    /// </summary>
    public Dictionary<string, string> DefaultNamespaceBindings { get; init; } = new();
    /// <summary>
    /// The xpath-default-namespace from the xsl:evaluate element.
    /// </summary>
    public string? XpathDefaultNamespace { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitEvaluate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.EvaluateInstructionAsync(this).ConfigureAwait(false);
    }
}
