using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// XSLT current-output-uri() function.
/// </summary>
internal sealed class XsltCurrentOutputUriFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-output-uri");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalString;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qec && qec.InsideXslEvaluate)
            throw new XsltException("XTDE3160: The function current-output-uri() is not available within xsl:evaluate");
        // Per XSLT 3.0 §20.3.7: The current-output-uri property is initially absent.
        // When absent, calling current-output-uri() returns an empty sequence.
        // Since we don't track output URIs for result-document (inline output),
        // return explicit empty sequence (not null, which may cause issues in sequence construction).
        return ValueTask.FromResult<object?>(Array.Empty<object>());
    }
}
