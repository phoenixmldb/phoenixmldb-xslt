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

public sealed class XsltTransformOptions
{
    /// <summary>
    /// Initial mode for template matching.
    /// </summary>
    public QName? InitialMode { get; init; }

    /// <summary>
    /// Initial template to call (instead of applying templates).
    /// </summary>
    public QName? InitialTemplate { get; init; }

    /// <summary>
    /// Initial parameters to pass to the stylesheet (global stylesheet parameters).
    /// </summary>
    public Dictionary<QName, object?> InitialParameters { get; init; } = new();

    /// <summary>
    /// Parameters to pass specifically to the initial template call (via xsl:with-param).
    /// When non-empty, these are used instead of InitialParameters for the template call.
    /// </summary>
    public Dictionary<QName, object?> InitialTemplateParameters { get; init; } = new();

    /// <summary>
    /// Tunnel parameters to pass to the initial template call.
    /// </summary>
    public Dictionary<QName, object?> InitialTunnelParameters { get; init; } = new();

    /// <summary>
    /// Initial function to call (instead of applying templates).
    /// The function is identified by name; arity is determined from InitialFunctionArguments.
    /// </summary>
    public QName? InitialFunction { get; init; }

    /// <summary>
    /// Positional arguments to pass to the initial function call.
    /// </summary>
    public List<object?> InitialFunctionArguments { get; init; } = [];

    /// <summary>
    /// When <c>true</c>, the engine captures the raw XDM return value of the
    /// transformation (initial-function call, initial-template call, or
    /// apply-templates) into <see cref="RawResult"/> instead of serializing it
    /// to the output buffer. Used by <c>fn:transform()</c> with
    /// <c>delivery-format='raw'</c>, where callers expect typed XDM items
    /// (booleans, maps, nodes…) preserved end-to-end rather than serialized
    /// as XML markup and reparsed.
    /// </summary>
    public bool ReturnRawXdm { get; init; }

    /// <summary>
    /// Output channel populated by the engine when <see cref="ReturnRawXdm"/>
    /// is <c>true</c>. Holds the raw XDM result of the transformation: a single
    /// item, an <c>object?[]</c> for sequences, or <c>null</c> for the empty
    /// sequence. Mutable so the engine can write to it from inside the
    /// transformation; callers read it after <c>TransformAsync</c> returns.
    /// </summary>
    public RawResultBox RawResult { get; init; } = new();

    /// <summary>
    /// Output format to use.
    /// </summary>
    public QName? OutputFormat { get; init; }

    /// <summary>
    /// Base URI for resolving relative URIs.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Document URI for the source document (used by fn:base-uri, fn:document-uri).
    /// </summary>
    public Uri? SourceDocumentUri { get; init; }

    /// <summary>
    /// URI of the PRINCIPAL output destination — where the transformation's main result will be
    /// written. Supplied by the host (the <c>xslt</c> CLI passes the <c>-o</c> target).
    /// <para>
    /// This is the "base output URI" of XSLT 3.0 §2.3. It is what <c>fn:current-output-uri()</c>
    /// reports while the principal result is being written, and what a relative
    /// <c>xsl:result-document/@href</c> resolves against. Leave it null when the result goes
    /// somewhere with no URI, such as stdout or an in-memory string — <c>current-output-uri()</c>
    /// then correctly returns the empty sequence, which is the spec's "absent" case.
    /// </para>
    /// </summary>
    public Uri? BaseOutputUri { get; init; }

    /// <summary>
    /// When <c>true</c>, <c>xi:include</c> elements in the principal source document are
    /// expanded (XInclude 1.0, <c>parse="xml"</c>) before the document is converted to XDM
    /// and transformed. Off by default. Expansion requires a source base URI — supply it via
    /// <see cref="SourceDocumentUri"/> (or <see cref="BaseUri"/>) so relative <c>href</c>s resolve.
    /// </summary>
    public bool ExpandXInclude { get; init; }

    /// <summary>
    /// When <see cref="ExpandXInclude"/> is on, controls whether remote (<c>http:</c>/<c>https:</c>)
    /// XInclude targets may be fetched. Off by default — only <c>file:</c>/relative targets resolve.
    /// </summary>
    public bool AllowRemoteXInclude { get; init; }

    /// <summary>
    /// Optional host-supplied resolver for XInclude targets. When null, a
    /// <see cref="PhoenixmlDb.Core.Xml.LocalFileResourceResolver"/> honoring
    /// <see cref="AllowRemoteXInclude"/> is used.
    /// </summary>
    public PhoenixmlDb.Core.Xml.IXmlResourceResolver? XIncludeResolver { get; init; }

    /// <summary>
    /// Message listener for xsl:message output. Receives (message, terminate).
    /// </summary>
    public Action<string, bool>? MessageListener { get; init; }

    /// <summary>
    /// Receives processor warnings — currently xsl:mode warning-on-no-match. A warning is a
    /// diagnostic the transform continues past, so it has its own channel rather than sharing
    /// xsl:message's.
    /// </summary>
    public Action<string>? WarningListener { get; init; }

    /// <summary>
    /// Extended message listener that also receives source location.
    /// Receives (message, terminate, line, column). Takes precedence over <see cref="MessageListener"/>.
    /// </summary>
    public Action<string, bool, int, int>? MessageListenerWithLocation { get; init; }

    /// <summary>
    /// Trace listener for debugging. Receives (depth, eventType, details) where eventType
    /// is "match", "apply", "call-template", "call-function", or "built-in".
    /// Depth indicates call nesting level.
    /// </summary>
    public Action<int, string, string>? TraceListener { get; init; }

    /// <summary>
    /// Whether a source document was explicitly supplied by the calling application.
    /// Used for xsl:global-context-item enforcement.
    /// </summary>
    public bool HasSourceDocument { get; set; }

    /// <summary>
    /// Cancellation token for cooperative cancellation of transformation.
    /// When cancelled, the transformation throws OperationCanceledException.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Maximum output size in characters. When the output StringBuilder exceeds this limit,
    /// the transformation throws an XsltException. Default is 50MB (~100MB in memory for UTF-16).
    /// Set to 0 to disable the limit.
    /// </summary>
    public int MaxOutputSize { get; init; } = 50 * 1024 * 1024;

    /// <summary>
    /// XPath expression to evaluate against the source document to determine the initial context node.
    /// When null, the document root is used. Example: "/doc" selects the document element named "doc".
    /// </summary>
    public string? SourceSelect { get; init; }

    /// <summary>
    /// XPath expression to determine the initial match selection for the initial mode.
    /// When set, the expression is evaluated with the source document as context,
    /// and templates are applied to each item in the resulting sequence instead of the document root.
    /// </summary>
    public string? InitialModeSelect { get; init; }

    /// <summary>
    /// The initial match selection supplied as a VALUE rather than as an expression to evaluate.
    /// Takes precedence over <see cref="InitialModeSelect"/>.
    /// </summary>
    /// <remarks>
    /// fn:transform's <c>initial-match-selection</c> option already holds real XDM items, and
    /// XSpec routes a context there whenever it is not a single node — so in practice this is a
    /// SEQUENCE, often of nodes. Turning such a value back into an XPath string cannot work:
    /// there is no expression that denotes an arbitrary existing node, and the previous
    /// conversion fell back to <c>value.ToString()</c>, which for a sequence is the CLR text
    /// "System.Object[]". That was then parsed as XPath and failed with
    /// "mismatched input ']'" — the empty predicate of <c>Object[]</c>.
    /// </remarks>
    public object? InitialModeSelectValue { get; init; }

    /// <summary>
    /// The context item to establish when an initial template is invoked with no source document.
    /// </summary>
    /// <remarks>
    /// fn:transform may be given both an initial-template and an initial-match-selection. The
    /// selection is not a source document - it can be an atomic value - so it cannot go through
    /// HasSourceDocument, but the named template still has to see it as the context item. Without
    /// this the engine pushed AbsentFocus and any template evaluating "." raised XPDY0002.
    /// XSpec relies on exactly that shape: its mirror templates are named entry points whose body
    /// is &lt;xsl:sequence select="."/&gt;.
    /// </remarks>
    public object? InitialContextItem { get; init; }

    /// <summary>
    /// fn:transform's <c>global-context-item</c> option: the item that serves as the GLOBAL
    /// context item, i.e. the focus while global variables and parameters are evaluated
    /// (XSLT 3.0 §5.4.1).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InitialContextItem"/>, which is only the focus for the initial
    /// template or match selection and is established after globals have already been built.
    /// A stylesheet declaring <c>xsl:global-context-item</c> and reading "." in a global
    /// variable needs the focus DURING that initialization, which is why the two cannot share
    /// one field. It is also frequently an atomic value, so it cannot travel as source-node.
    /// </remarks>
    public object? GlobalContextItem { get; init; }

    /// <summary>
    /// Named collections of document file paths for fn:collection().
    /// Key is the collection URI, value is the list of document file paths.
    /// </summary>
    public Dictionary<string, List<string>>? Collections { get; init; }

    /// <summary>
    /// Optional resource security policy. When set, controls which URIs the transformation
    /// can access via doc(), unparsed-text(), collection(), xsl:result-document, and xsl:import/include.
    /// </summary>
    public PhoenixmlDb.XQuery.Security.ResourcePolicy? ResourcePolicy { get; init; }

    /// <summary>
    /// Optional pre-fetched contents for URIs that <c>fn:doc()</c> / <c>document()</c> would
    /// otherwise need to fetch over HTTP synchronously. Required on Blazor WebAssembly,
    /// which cannot block the calling thread; ignored on runtimes that can. See
    /// <see cref="PreloadedResources"/>.
    /// </summary>
    public PreloadedResources? PreloadedResources { get; init; }
}
