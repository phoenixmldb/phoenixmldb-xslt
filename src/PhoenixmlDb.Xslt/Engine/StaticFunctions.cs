using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Functions;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The functions a static expression may call (XSLT 3.0 §9.7 and §3.13.1): the functions of F&amp;O
/// 3.1 (fn, math, map and array), the constructor functions of the built-in types (xs), the
/// registered extension functions (phx), and of XSLT's own functions only system-property,
/// function-available, element-available, type-available and available-system-properties.
/// Stylesheet functions, functions in any other namespace, and the XSLT functions that need the
/// dynamic context of a transformation (current, key, document, regex-group, …) are not available:
/// a call is XPST0017, and function-available gives false.
/// </summary>
internal static class StaticFunctions
{
    internal static bool IsAvailable(QName name)
    {
        var ns = FunctionNames.NamespaceOf(name);
        if (ns == NamespaceId.Fn)
            return !NeedsDynamicContext(name.LocalName);
        return ns == NamespaceId.Math || ns == NamespaceId.Map || ns == NamespaceId.Array
            || ns == FunctionNamespaces.Xs || ns == FunctionNamespaces.Phx;
    }

    // XSLT's functions in the fn namespace other than the five a static expression may call.
    private static bool NeedsDynamicContext(string localName)
        => localName is "current" or "current-group" or "current-grouping-key"
            or "current-merge-group" or "current-merge-key" or "current-output-uri"
            or "regex-group" or "unparsed-entity-uri" or "unparsed-entity-public-id"
            or "accumulator-before" or "accumulator-after" or "snapshot" or "copy-of"
            or "key" or "document" or "stream-available";
}
