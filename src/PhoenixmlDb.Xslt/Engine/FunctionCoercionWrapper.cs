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
/// Wraps a callable item in a coercion wrapper (function coercion, XPath 3.1 §3.1.5.3; XSLT 3.0
/// §5.4.11). At invocation time each argument is converted to the wrapper's parameter type
/// (xs:untypedAtomic is cast, numerics are promoted) before the inner callable gets it, and the
/// return value is checked against the declared return type.
/// </summary>
/// <remarks>
/// The arguments were passed through as they were. Bound to function(xs:double),
/// function($x as xs:float) then got an xs:decimal and promoted it to xs:float itself; the
/// wrapper must give it an xs:double, which it cannot take: XPTY0004 (W3C
/// higher-order-functions-066, as Saxon-HE 12.10).
/// </remarks>
internal sealed class FunctionCoercionWrapper : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly PhoenixmlDb.XQuery.Ast.XQueryFunction _innerFunction;
    private readonly IReadOnlyList<XdmSequenceType> _declaredParamTypes;
    private readonly XdmSequenceType _declaredReturnType;

    public FunctionCoercionWrapper(
        PhoenixmlDb.XQuery.Ast.XQueryFunction innerFunction,
        IReadOnlyList<XdmSequenceType> declaredParamTypes,
        XdmSequenceType declaredReturnType)
    {
        _innerFunction = innerFunction;
        _declaredParamTypes = declaredParamTypes;
        _declaredReturnType = declaredReturnType;
    }

    public override QName Name => _innerFunction.Name;

    public override XdmSequenceType ReturnType => _declaredReturnType;

    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        _declaredParamTypes.Select((t, i) => new FunctionParameterDef
        {
            Name = i < _innerFunction.Parameters.Count
                ? _innerFunction.Parameters[i].Name
                : new QName(NamespaceId.None, $"arg{i + 1}"),
            Type = t
        }).ToList();

    public override bool IsAnonymous => _innerFunction.IsAnonymous;

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var converted = new object?[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
            converted[i] = i < _declaredParamTypes.Count
                ? ConvertArgument(arguments[i], _declaredParamTypes[i].ItemType)
                : arguments[i];
        var result = await _innerFunction.InvokeAsync(converted, context).ConfigureAwait(false);

        // Validate return value against declared return type
        ValidateCoercedReturnValue(result, _declaredReturnType, _innerFunction.Name.LocalName);

        return result;
    }

    /// <summary>
    /// The function conversion rules for one argument, as far as they change the value: an
    /// xs:untypedAtomic is cast to an atomic parameter type, and a numeric is promoted to
    /// xs:double, xs:float or xs:decimal. Anything else is passed on for the inner callable to
    /// check.
    /// </summary>
    private static object? ConvertArgument(object? value, ItemType target)
    {
        if (value is object?[] items)
        {
            object?[]? copy = null;
            for (var i = 0; i < items.Length; i++)
            {
                var c = ConvertArgument(items[i], target);
                if (!ReferenceEquals(c, items[i]))
                    (copy ??= (object?[])items.Clone())[i] = c;
            }
            return copy ?? value;
        }
        if (value is null || target is ItemType.Item or ItemType.AnyAtomicType
            || XQuery.Execution.TypeCastHelper.MatchesItemType(value, target))
            return value;
        if (value is XsUntypedAtomic && target is not (ItemType.Node or ItemType.Element or ItemType.Attribute
                or ItemType.Text or ItemType.Document or ItemType.Comment or ItemType.ProcessingInstruction
                or ItemType.Function or ItemType.Map or ItemType.Array))
            return XQuery.Execution.TypeCastHelper.CastValue(value, target);
        var promotable = target switch
        {
            ItemType.Double => value is int or long or float or decimal or BigInteger,
            ItemType.Float => value is int or long or decimal or BigInteger,
            ItemType.Decimal => value is int or long or BigInteger,
            ItemType.String => value is XsAnyUri,
            _ => false,
        };
        return promotable ? XQuery.Execution.TypeCastHelper.PromoteNumeric(value, target) : value;
    }

    private static void ValidateCoercedReturnValue(object? result, XdmSequenceType declaredReturn, string funcName)
    {
        // Check return type compatibility
        if (result == null)
        {
            if (declaredReturn.Occurrence is Occurrence.ExactlyOne or Occurrence.OneOrMore)
                throw new XsltException(
                    $"XPTY0004: Coercion wrapper for {funcName}() — returned empty sequence " +
                    $"but requires return type {declaredReturn}");
            return;
        }

        if (result is object?[] arr)
        {
            foreach (var item in arr)
            {
                if (item != null && !IsReturnTypeCompatible(item, declaredReturn.ItemType))
                    throw new XsltException(
                        $"XPTY0004: Coercion wrapper for {funcName}() — returned value of type " +
                        $"{item.GetType().Name} but requires return type {declaredReturn}");
            }
        }
        else if (!IsReturnTypeCompatible(result, declaredReturn.ItemType))
        {
            throw new XsltException(
                $"XPTY0004: Coercion wrapper for {funcName}() — returned value of type " +
                $"{result.GetType().Name} but requires return type {declaredReturn}");
        }
    }

    /// <summary>
    /// Checks if a return value is compatible with the declared return type,
    /// including numeric promotion (integer/decimal → double, integer → decimal).
    /// </summary>
    private static bool IsReturnTypeCompatible(object value, ItemType targetType)
    {
        if (XQuery.Execution.TypeCastHelper.MatchesItemType(value, targetType))
            return true;

        // Numeric promotion: int/long → double/decimal/float, float → double, decimal → double
        return targetType switch
        {
            ItemType.Double => value is int or long or float or decimal,
            ItemType.Decimal => value is int or long,
            ItemType.Float => value is int or long,
            _ => false
        };
    }
}
