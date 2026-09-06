using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:with-param in apply-templates or call-template.
/// </summary>
public sealed class XsltWithParam
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Tunnel { get; init; }

    /// <summary>
    /// True when this parameter came from a RUNTIME option rather than a literal
    /// <c>xsl:with-param</c> in a stylesheet — today, fn:transform's <c>template-params</c> and
    /// <c>tunnel-params</c>.
    /// </summary>
    /// <remarks>
    /// XTSE0680 — "parameter not declared in the called template" — is a STATIC error: it is
    /// about what an author wrote next to an xsl:call-template, and the engine raises it in
    /// CallTemplateAsync. A runtime map is different: fn:transform supplies template-params
    /// without knowing which parameters the chosen initial template declares, and values for
    /// parameters it does not declare are simply ignored.
    ///
    /// Without this distinction, one undeclared entry aborts the whole transform. XSpec passes
    /// its param-items to whichever template a scenario names, so a single scenario whose
    /// template does not declare it terminated the entire suite.
    /// </remarks>
    public bool FromRuntimeOptions { get; init; }

    /// <summary>
    /// A value supplied DIRECTLY, rather than one to be produced by evaluating
    /// <see cref="Select"/> or <see cref="Content"/>. Only meaningful when
    /// <see cref="HasRuntimeValue"/> is true.
    /// </summary>
    /// <remarks>
    /// fn:transform's template-params and tunnel-params already hold real XDM values — nodes,
    /// sequences, maps, arrays — and there is nothing to evaluate. Routing them through a
    /// synthesised Select expression meant first turning each value into a literal, and no
    /// literal can express a node: everything that was not a string, number or boolean was
    /// flattened with ToString(). A node-valued parameter arrived as a string, and the first
    /// axis step on it raised XPTY0020.
    /// </remarks>
    public object? RuntimeValue { get; init; }

    /// <summary>
    /// True when <see cref="RuntimeValue"/> holds the parameter's value. Distinct from testing
    /// the value for null, because null IS a value here — the empty sequence.
    /// </summary>
    public bool HasRuntimeValue { get; init; }
}
