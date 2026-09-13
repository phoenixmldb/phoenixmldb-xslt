using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.XQuery;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// <c>fn:serialize</c> for XSLT.
/// </summary>
/// <remarks>
/// <para>
/// The XQuery implementation resolves a namespace id back to its URI only when the node
/// provider is an <c>XdmDocumentStore</c>. The XSLT engine has its own
/// <see cref="XdmInMemoryStore"/>, so the URI came back empty and every prefixed declaration
/// serialized as <c>xmlns:p=""</c> — which XML does not permit, since only the default
/// namespace can be undeclared. The output did not reparse. The same store gate made an
/// <c>&lt;output:serialization-parameters&gt;</c> element raise XPTY0004 from a stylesheet,
/// because the check on the element's own namespace could never succeed.
/// </para>
/// <para>
/// This is the third defect from that one type test; the two earlier ones are recorded on
/// <c>fn_serialize_adaptive_method_emits_map_with_node_values</c>. Rather than resolve the
/// namespace differently, these overrides route serialization through the engine's own
/// store-aware serializers, which is where XSLT already gets this right for
/// <c>xsl:result-document</c> and for the payload of <c>fn:transform</c>.
/// </para>
/// <para>
/// Option parsing still uses <c>XQueryResultSerializer.ParseSerializationOptions</c>, so the
/// serialization-parameter validation (SEPM/SESU) is the same one every other caller gets.
/// </para>
/// </remarks>
internal static class XsltSerialize
{
    internal const string SerializationParamsNs = "http://www.w3.org/2010/xslt-xquery-serialization";

    // SENR0001: an attribute, namespace or function item has no serialization of its own.
    // Adaptive is the exception — it defines a form for them — so the caller decides.
    internal static void CheckSenr0001(object? arg)
    {
        Check(arg);
        if (arg is IEnumerable<object?> seq && arg is not string)
            foreach (var it in seq)
                Check(it);

        static void Check(object? item)
        {
            if (item is Xdm.Nodes.XdmAttribute || item is Xdm.Nodes.XdmNamespace || item is XQueryFunction)
                throw new PhoenixmlDb.XQuery.Execution.XQueryRuntimeException("SENR0001",
                    "Attribute, namespace, or function item cannot be serialized at the top level");
        }
    }

    /// <summary>
    /// Serializes one item using the XSLT engine's store-aware serializers. Mirrors the
    /// method dispatch of the XQuery twin so that only namespace resolution changes.
    /// </summary>
    internal static string SerializeItem(
        DefaultXsltExecutionContext context, object? item, PhoenixmlDb.XQuery.OutputMethod method)
    {
        if (item == null)
            return "";
        var store = context._nodeStore;
        // A sequence is handled here rather than deeper down because the engine's adaptive
        // serializer has no case for one: a map entry holding element()+ arrives as object?[]
        // and fell through to its string value ("a b") instead of the two elements' markup.
        // XPath 3.1 §27.7: items of a sequence are space-separated, with no parentheses.
        if (item is object?[] seq)
        {
            return string.Join(" ", seq.Where(x => x != null)
                .Select(x => SerializeItem(context, x, method)));
        }
        if (method == PhoenixmlDb.XQuery.OutputMethod.Adaptive)
            return XsltTransformEngine.SerializeItemAdaptive(item, store);
        return item switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            // The store-aware walk: it resolves each declaration's URI through the node store
            // and drops a prefixed declaration whose URI is empty rather than emitting
            // unparseable markup.
            Xdm.Nodes.XdmNode node => context.SerializeXdmNodeToXml(node),
            IDictionary<object, object?> map =>
                XsltTransformEngine.SerializeItemAsJson(map, adaptive: false, store: store),
            List<object?> array =>
                XsltTransformEngine.SerializeItemAsJson(array, adaptive: false, store: store),
            object?[] arr => string.Join(" ",
                arr.Where(x => x != null).Select(x => SerializeItem(context, x, method))),
            _ => item.ToString() ?? ""
        };
    }
}

/// <summary>fn:serialize($arg) — see <see cref="XsltSerialize"/>.</summary>
internal sealed class XsltSerializeFunction : XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltSerializeFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "serialize");
    public override XdmSequenceType ReturnType => XdmSequenceType.String;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
    [
        new()
        {
            Name = new QName(NamespaceId.None, "arg"),
            Type = new XdmSequenceType { ItemType = ItemType.Item, Occurrence = Occurrence.ZeroOrMore }
        }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var arg = arguments.Count > 0 ? arguments[0] : null;
        if (arg == null || (arg is object?[] empty && empty.Length == 0))
            return ValueTask.FromResult<object?>("");

        XsltSerialize.CheckSenr0001(arg);
        // XPath 3.1 §17.1.3: the one-argument form uses the default serialization parameters,
        // whose method is adaptive.
        return ValueTask.FromResult<object?>(
            XsltSerialize.SerializeItem(_context, arg, PhoenixmlDb.XQuery.OutputMethod.Adaptive));
    }
}

/// <summary>fn:serialize($arg, $params) — see <see cref="XsltSerialize"/>.</summary>
internal sealed class XsltSerialize2Function : XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltSerialize2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "serialize");
    public override XdmSequenceType ReturnType => XdmSequenceType.String;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
    [
        new()
        {
            Name = new QName(NamespaceId.None, "arg"),
            Type = new XdmSequenceType { ItemType = ItemType.Item, Occurrence = Occurrence.ZeroOrMore }
        },
        new()
        {
            Name = new QName(NamespaceId.None, "params"),
            Type = new XdmSequenceType { ItemType = ItemType.Item, Occurrence = Occurrence.ZeroOrOne }
        }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var arg = arguments.Count > 0 ? arguments[0] : null;
        var paramsArg = arguments.Count > 1 ? arguments[1] : null;
        if (paramsArg is object?[] emptyParams && emptyParams.Length == 0)
            paramsArg = null;

        var paramsMap = paramsArg as IDictionary<object, object?>;
        var paramsFromMap = paramsMap != null;

        if (paramsMap == null && paramsArg is Xdm.Nodes.XdmElement paramsElem)
        {
            // Resolving the element's OWN namespace through the XSLT store is what the XQuery
            // twin cannot do; there the check compared against null and always threw.
            var nsUri = _context._nodeStore?.GetNamespaceUri(paramsElem.Namespace);
            if (nsUri != XsltSerialize.SerializationParamsNs
                || paramsElem.LocalName != "serialization-parameters")
            {
                throw new PhoenixmlDb.XQuery.Execution.XQueryRuntimeException("XPTY0004",
                    "serialization parameters element must be <output:serialization-parameters>");
            }
            paramsMap = PhoenixmlDb.XQuery.XQueryResultSerializer
                .ParseSerializationParamsElement(paramsElem, _context._nodeStore);
        }

        var options = PhoenixmlDb.XQuery.XQueryResultSerializer
            .ParseSerializationOptions(paramsMap, paramsFromMap);
        var method = options.Method;

        // Adaptive defines a serialization for attributes, so they are allowed there.
        if (method != PhoenixmlDb.XQuery.OutputMethod.Adaptive)
            XsltSerialize.CheckSenr0001(arg);

        if (method == PhoenixmlDb.XQuery.OutputMethod.Json)
        {
            if (arg == null || (arg is object?[] nullArr && nullArr.Length == 0))
                return ValueTask.FromResult<object?>("null");
            if (CountsMoreThanOne(arg))
            {
                throw new PhoenixmlDb.XQuery.Execution.XQueryRuntimeException("SERE0023",
                    "JSON output method cannot serialize a sequence of more than one item");
            }
        }

        if (arg == null)
        {
            return ValueTask.FromResult<object?>(
                method == PhoenixmlDb.XQuery.OutputMethod.Json ? "null" : "");
        }

        return ValueTask.FromResult<object?>(XsltSerialize.SerializeItem(_context, arg, method));
    }

    // A map and an array are single items even though both are enumerable, so neither counts
    // as a sequence here.
    private static bool CountsMoreThanOne(object? arg)
    {
        if (arg is object?[] arr)
            return arr.Length > 1;
        if (arg is string || arg is IDictionary<object, object?> || arg is List<object?>)
            return false;
        if (arg is IEnumerable<object?> seq)
        {
            var count = 0;
            foreach (var _ in seq)
            {
                count++;
                if (count > 1)
                    return true;
            }
        }
        return false;
    }
}
