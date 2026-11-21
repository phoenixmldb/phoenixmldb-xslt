using PhoenixmlDb.Core;
using FunctionNamespaces = PhoenixmlDb.XQuery.Functions.FunctionNamespaces;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The namespace of the name in a function call or a named function reference, for code that
/// asks whether the call is to a built-in function (fn:, map:, array:, math:, xs:) or which
/// stylesheet function it names.
/// </summary>
/// <remarks>
/// <para>A name can reach the engine in three forms. A prefixed name (<c>map:get</c>) carries the
/// identifier of its namespace. An unprefixed name carries <see cref="NamespaceId.None"/> and is
/// in the default function namespace, fn. An EQName (<c>Q{http://www.w3.org/2005/xpath-functions/map}get</c>)
/// is built by the XPath parser with <see cref="NamespaceId.None"/> and its namespace as text in
/// <see cref="QName.ExpandedNamespace"/>, and a name resolved at run time carries the text in
/// <see cref="QName.RuntimeNamespace"/>.</para>
/// <para>Code that read only <see cref="QName.Namespace"/> took every EQName for an fn: name:
/// <c>Q{…/map}get(…)</c> was looked up as fn:get and found nothing, and a stylesheet function
/// <c>Q{urn:other}sum</c> was run as fn:sum (xslt#582). Read the namespace through this class.</para>
/// </remarks>
internal static class FunctionNames
{
    /// <summary>
    /// The identifier of the namespace a function name is in, as the stylesheet parser gives it
    /// (<see cref="StylesheetParser.ResolveNamespaceUri"/>), with an unprefixed name in fn: and the
    /// XML Schema namespace as <see cref="FunctionNamespaces.Xs"/>. Null for <c>Q{}local</c>, a
    /// name in no namespace, which no built-in function has.
    /// </summary>
    internal static NamespaceId? NamespaceOf(QName name)
    {
        if (name.ResolvedNamespace is { } uri)
            return uri.Length == 0 ? null : StylesheetParser.ResolveNamespaceUri(uri);
        if (name.Namespace == NamespaceId.None)
            return NamespaceId.Fn;
        // Core's identifier for the XML Schema namespace; the function library uses another.
        if (name.Namespace == NamespaceId.Xsd)
            return FunctionNamespaces.Xs;
        return name.Namespace;
    }

    /// <summary>True when the name is in the fn: namespace, written with or without a prefix or as an EQName.</summary>
    internal static bool IsFn(QName name) => NamespaceOf(name) == NamespaceId.Fn;

    /// <summary>True when the name is fn:<paramref name="localName"/>.</summary>
    internal static bool IsFn(QName name, string localName) => name.LocalName == localName && IsFn(name);

    /// <summary>True when the name is in the map: namespace.</summary>
    internal static bool IsMap(QName name) => NamespaceOf(name) == NamespaceId.Map;

    /// <summary>True when the name is in the array: namespace.</summary>
    internal static bool IsArray(QName name) => NamespaceOf(name) == NamespaceId.Array;

    /// <summary>True when the name is in the math: namespace.</summary>
    internal static bool IsMath(QName name) => NamespaceOf(name) == NamespaceId.Math;

    /// <summary>True when the name is in the XML Schema namespace (a constructor function).</summary>
    internal static bool IsXs(QName name) => NamespaceOf(name) == FunctionNamespaces.Xs;

    /// <summary>True when the name is in fn: or in the XML Schema namespace.</summary>
    internal static bool IsFnOrXs(QName name) => NamespaceOf(name) is { } ns && (ns == NamespaceId.Fn || ns == FunctionNamespaces.Xs);

    /// <summary>
    /// True when two function names are the same expanded name, whichever of the three forms
    /// each is written in: a call <c>Q{urn:f}g(…)</c> names the function declared as <c>f:g</c>.
    /// </summary>
    internal static bool SameName(QName a, QName b)
        => a.LocalName == b.LocalName && NamespaceOf(a) == NamespaceOf(b);
}
