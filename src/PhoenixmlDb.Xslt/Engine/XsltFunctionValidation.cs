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

// Widened from `file` to `internal` when XsltTransformer.cs was split one-type-per-file.
// A file-scoped helper is only visible inside its own file, so it could not survive the
// split — and "file-private" inside a 38,000-line file was not meaningful encapsulation
// anyway. Still assembly-internal; no public surface change.
/// <summary>
/// Validates that a string argument is a valid QName or EQName.
/// Used by system-property, function-available, element-available, and key functions.
/// </summary>
internal static class XsltFunctionValidation
{
    internal static void ValidateQNameArgument(string name, string errorCode, string functionName)
    {
        if (string.IsNullOrEmpty(name))
            throw new XsltException($"{errorCode}: The argument to {functionName}() is a zero-length string");

        // EQName syntax Q{...}local is always valid if it can be parsed
        if (name.StartsWith("Q{", StringComparison.Ordinal))
            return;

        try
        {
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx >= 0)
            {
                System.Xml.XmlConvert.VerifyNCName(name[..colonIdx]);
                System.Xml.XmlConvert.VerifyNCName(name[(colonIdx + 1)..]);
            }
            else
            {
                System.Xml.XmlConvert.VerifyNCName(name);
            }
        }
        catch (System.Xml.XmlException)
        {
            throw new XsltException($"{errorCode}: The argument to {functionName}() ('{name}') is not a valid EQName");
        }
    }
}

// ─── fn:analyze-string ──────────────────────────────────────────────────────
