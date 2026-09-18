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
internal sealed class XsltCurrentOutputUriFunction(DefaultXsltExecutionContext ctx)
    : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "current-output-uri");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalString;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var qec = context as PhoenixmlDb.XQuery.Execution.QueryExecutionContext;
        if (qec is { InsideXslEvaluate: true })
            throw new XsltException("XTDE3160: The function current-output-uri() is not available within xsl:evaluate");
        // Per XSLT 3.0 §20.3.7: the current-output-uri property is absent unless a destination
        // URI is known, and when absent this returns the empty sequence. It is NOT absent whenever
        // the host told us where the principal result goes (the CLI's -o), nor inside an
        // xsl:result-document with an href — this used to return empty unconditionally, so it was
        // silently empty in exactly the cases where it should have had an answer.
        // The current output URI is absent in temporary output state (XSLT 3.0 §20.3.7): while evaluating a variable,
        // a stylesheet or inline function, a dynamic call or an initial function. This returned the base output URI
        // there (W3C current-output-uri-005, -006, -016, -017), which went unnoticed while the runner set none.
        //
        // A DYNAMIC call is the other half of the same rule and does not go through
        // _temporaryOutputDepth at all: XSLT 3.0 spec bug 30411 makes the current output URI
        // absent inside a call through a function item ($f()) and inside an inline function body
        // however it is invoked. The XQuery engine counts those for us — DynamicCallDepth is
        // documented for precisely this caller — and nothing in the XSLT engine can tell a
        // dynamic call from a static one on its own (W3C current-output-uri-016, -017).
        if (ctx.InTemporaryOutputState || qec is { DynamicCallDepth: > 0 })
            return ValueTask.FromResult<object?>(Array.Empty<object>());
        var uri = ctx.CurrentOutputUri;
        return ValueTask.FromResult<object?>(
            uri is null ? Array.Empty<object>() : new PhoenixmlDb.Xdm.XsAnyUri(uri.AbsoluteUri));
    }
}
