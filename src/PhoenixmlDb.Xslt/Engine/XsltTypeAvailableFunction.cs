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
        // Strip prefix
        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            name = parts[1];
        }
        // Common XSD types
        var available = name switch
        {
            "string" or "boolean" or "decimal" or "float" or "double" or
            "integer" or "long" or "int" or "short" or "byte" or
            "nonNegativeInteger" or "positiveInteger" or "nonPositiveInteger" or "negativeInteger" or
            "unsignedLong" or "unsignedInt" or "unsignedShort" or "unsignedByte" or
            "date" or "time" or "dateTime" or "duration" or
            "dayTimeDuration" or "yearMonthDuration" or
            "anyURI" or "QName" or "NOTATION" or "hexBinary" or "base64Binary" or
            "normalizedString" or "token" or "language" or "NMTOKEN" or "Name" or "NCName" or
            "gYearMonth" or "gYear" or "gMonthDay" or "gDay" or "gMonth" or
            "untyped" or "untypedAtomic" or "anyAtomicType" or "anySimpleType" or "anyType" or
            "IDREF" or "IDREFS" or "ENTITY" or "ENTITIES" or "NMTOKENS" or "ID" => true,
            _ => false
        };
        return ValueTask.FromResult<object?>(available);
    }
}
