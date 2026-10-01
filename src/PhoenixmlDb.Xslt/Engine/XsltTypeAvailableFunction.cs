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
/// XSLT type-available() function.
/// </summary>
internal sealed class XsltTypeAvailableFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "type-available");
    public override XdmSequenceType ReturnType => XdmSequenceType.Boolean;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "type-name"), Type = XdmSequenceType.String }];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var name = arguments[0]?.ToString() ?? "";
        // XTDE1428: Validate name is a valid EQName
        XsltFunctionValidation.ValidateQNameArgument(name, "XTDE1428", "type-available");
        var bindings = (context as PhoenixmlDb.XQuery.Execution.QueryExecutionContext)?.PrefixNamespaceBindings;
        var available = IsBuiltInType(name, prefix => bindings is not null && bindings.TryGetValue(prefix, out var uri) ? uri : null);
        return ValueTask.FromResult<object?>(available);
    }

    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    /// <summary>
    /// Whether a type name (lexical QName or EQName) names a built-in type this processor knows.
    /// Shared by the run-time function and the parser's static evaluation, which kept separate
    /// tables that had drifted. A prefixed name is resolved with <paramref name="resolvePrefix"/>;
    /// an unknown prefix, or one bound to another namespace, is not an XSD type. An unprefixed name
    /// is matched by local name, as before.
    /// </summary>
    /// <remarks>
    /// Splitting on ':' cut an EQName at "Q{http:", so type-available('Q{…XMLSchema}date') was
    /// false (W3C type-available-0151a, which also needs the XSD 1.1 xs:dateTimeStamp).
    /// </remarks>
    internal static bool IsBuiltInType(string name, Func<string, string?> resolvePrefix)
    {
        string local;
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var close = name.IndexOf('}', StringComparison.Ordinal);
            if (close < 0 || name[2..close] != XsdNamespace)
                return false;
            local = name[(close + 1)..];
        }
        else if (name.IndexOf(':', StringComparison.Ordinal) is var colon and > 0)
        {
            var uri = resolvePrefix(name[..colon]);
            if (uri is not null && uri != XsdNamespace)
                return false;
            local = name[(colon + 1)..];
        }
        else
            local = name;
        return local is
            "string" or "boolean" or "decimal" or "float" or "double" or
            "integer" or "long" or "int" or "short" or "byte" or
            "nonNegativeInteger" or "positiveInteger" or "nonPositiveInteger" or "negativeInteger" or
            "unsignedLong" or "unsignedInt" or "unsignedShort" or "unsignedByte" or
            "date" or "time" or "dateTime" or "dateTimeStamp" or "duration" or
            "dayTimeDuration" or "yearMonthDuration" or
            "anyURI" or "QName" or "NOTATION" or "hexBinary" or "base64Binary" or
            "normalizedString" or "token" or "language" or "NMTOKEN" or "Name" or "NCName" or
            "gYearMonth" or "gYear" or "gMonthDay" or "gDay" or "gMonth" or
            "untyped" or "untypedAtomic" or "anyAtomicType" or "anySimpleType" or "anyType" or
            "numeric" or
            "IDREF" or "IDREFS" or "ENTITY" or "ENTITIES" or "NMTOKENS" or "ID";
    }
}
