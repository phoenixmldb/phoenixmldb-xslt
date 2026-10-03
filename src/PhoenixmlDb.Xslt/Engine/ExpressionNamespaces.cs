using System.Runtime.CompilerServices;
using System.Xml.Linq;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The in-scope prefixed namespaces of an XPath expression whose stylesheet element declares
/// namespaces of its own, below its module's root element. At run time the XPath engine resolves
/// prefixes in lexical QNames (casting xs:untypedAtomic 'my:x' to xs:QName in a comparison,
/// system-property('p:x')) against the bindings it is given. Those were the stylesheet-wide set,
/// so an xmlns:my on the instruction itself was invisible (W3C choose-0106/0107: three xsl:when
/// each binding my differently all compared in the same namespace). Expressions whose element
/// declares nothing locally, nearly all of them, have no entry and use the stylesheet set.
/// </summary>
internal static class ExpressionNamespaces
{
    private static readonly ConditionalWeakTable<XQueryExpression, IReadOnlyDictionary<string, string>> s_table = new();

    internal static void Record(XQueryExpression expr, XElement element)
    {
        var root = element.Document?.Root ?? element.AncestorsAndSelf().Last();
        var declaresBelowRoot = false;
        foreach (var e in element.AncestorsAndSelf())
        {
            if (ReferenceEquals(e, root)) break;
            if (e.Attributes().Any(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns))
            {
                declaresBelowRoot = true;
                break;
            }
        }
        if (!declaresBelowRoot)
            return;

        // Nearest declaration wins: walk from the element outward and keep the first binding seen.
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in element.AncestorsAndSelf())
            foreach (var a in e.Attributes())
                if (a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns)
                    bindings.TryAdd(a.Name.LocalName, a.Value);
        s_table.AddOrUpdate(expr, bindings);
    }

    internal static bool TryGet(XQueryExpression expr, out IReadOnlyDictionary<string, string> bindings)
        => s_table.TryGetValue(expr, out bindings!);
}
