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
/// Options for XSLT transformation.
/// </summary>
/// <summary>
/// Mutable holder for the raw XDM result of a transformation invoked with
/// <see cref="XsltTransformOptions.ReturnRawXdm"/>. Used so the engine can write
/// back to the caller's options object while the rest of <see cref="XsltTransformOptions"/>
/// stays init-only. The boxed `Value` is the raw XDM: a single item, an `object?[]`
/// for sequences, or `null` for the empty sequence.
/// </summary>
public sealed class RawResultBox
{
    /// <summary>The raw XDM items captured during the most recent transformation.</summary>
    public object? Value { get; set; }
}
