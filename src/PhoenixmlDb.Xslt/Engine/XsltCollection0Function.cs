using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// fn:collection() with no argument: the default collection.
/// </summary>
/// <remarks>
/// The XQuery function raises FODC0002 whenever the resolver returns no documents, so it can't
/// tell an EMPTY default collection from none at all. A host that declared the default collection
/// (SetCollection("", …)) with no documents got FODC0002 instead of the empty sequence (W3C
/// collection-001/003). A declared default collection is returned as it is, through the context's
/// resolver so a resource policy still applies; otherwise the XQuery function decides.
/// </remarks>
internal sealed class XsltCollection0Function(DefaultXsltExecutionContext context) : XQueryFunction
{
    private static readonly PhoenixmlDb.XQuery.Functions.Collection0Function s_xquery = new();

    public override QName Name => s_xquery.Name;
    public override XdmSequenceType ReturnType => s_xquery.ReturnType;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext executionContext)
    {
        if (context.HasDeclaredCollection("")
            && executionContext is PhoenixmlDb.XQuery.Execution.QueryExecutionContext { DocumentResolver: { } resolver })
            return ValueTask.FromResult<object?>(resolver.ResolveCollection(null).ToArray());
        return s_xquery.InvokeAsync(arguments, executionContext);
    }
}
