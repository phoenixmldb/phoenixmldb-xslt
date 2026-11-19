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
/// Represents a global declaration (param or variable) for dependency sorting.
/// </summary>
internal sealed class GlobalDeclaration
{
    public required QName Name { get; init; }
    public bool IsParam { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XdmSequenceType? As { get; init; }
    public object? OriginalDeclaration { get; init; }
    public Uri? BaseUri { get; init; }
    public string? Version { get; init; }
    /// <summary>
    /// The used package that declared this global, or null for the principal package. Set as
    /// the current component package while the global is evaluated so an intra-package
    /// reference to a private sibling global resolves (private-across-boundary enforcement).
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; init; }

    /// <summary>
    /// The compile-time value of a static variable whose select reads a global that, at run
    /// time, is a non-static declaration of higher import precedence. A static variable's value
    /// is fixed at compile time, when only static variables are in scope (XSLT 3.0 §9.7), so
    /// evaluating it again would read the wrong binding (W3C static-027). Null otherwise: then
    /// the select is evaluated at run time, which agrees with the compile-time value.
    /// </summary>
    public object? FixedStaticValue { get; set; }
}
