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
/// Wraps a callable item in a coercion wrapper per XSLT 3.0 §5.4.11.
/// At invocation time, validates return value against the declared return type.
/// Argument type mismatches are detected naturally when the inner callable rejects them.
/// </summary>
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
        // Pass arguments through — atomization, untypedAtomic casting, and numeric
        // promotion are handled by the dynamic function call mechanism
        var result = await _innerFunction.InvokeAsync(arguments, context).ConfigureAwait(false);

        // Validate return value against declared return type
        ValidateCoercedReturnValue(result, _declaredReturnType, _innerFunction.Name.LocalName);

        return result;
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
