using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Identifies one call of a memoized stylesheet function: the function and its arguments,
/// compared by the XSLT 3.0 §10.3.2 notion of identical — nodes and function items by
/// identity, atomic values by type and value, maps and arrays by content.
/// </summary>
/// <remarks>
/// This replaces a string key built by concatenating each argument's text form, which was not
/// an identity at all. A multi-item sequence rendered as its CLR type name, so
/// <c>f((1, 2))</c> and <c>f((3, 4))</c> shared one entry; <c>f('1')</c> and <c>f(1)</c>
/// rendered alike; a comma inside a string collided across argument boundaries; and nodes were
/// told apart by hash code, which distinct nodes can share. Each returned another call's result.
/// </remarks>
internal sealed class FunctionMemoKey : IEquatable<FunctionMemoKey>
{
    private readonly XsltFunction _function;
    private readonly object?[] _arguments;
    private readonly int _hash;

    public FunctionMemoKey(XsltFunction function, IReadOnlyList<object?> arguments)
    {
        _function = function;
        _arguments = arguments.ToArray();
        var hash = new HashCode();
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(function));
        foreach (var argument in _arguments)
            hash.Add(ValueHash(argument));
        _hash = hash.ToHashCode();
    }

    public bool Equals(FunctionMemoKey? other)
    {
        if (other is null || _hash != other._hash || !ReferenceEquals(_function, other._function)
            || _arguments.Length != other._arguments.Length)
            return false;
        for (var i = 0; i < _arguments.Length; i++)
            if (!ValueEquals(_arguments[i], other._arguments[i]))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is FunctionMemoKey other && Equals(other);

    public override int GetHashCode() => _hash;

    /// <summary>A value is a sequence: null is empty, object?[] is its items, anything else one item.</summary>
    private static bool ValueEquals(object? a, object? b)
    {
        var aItems = a as object?[];
        var bItems = b as object?[];
        if (aItems is null && bItems is null)
            return ItemEquals(a, b);
        var aLength = aItems?.Length ?? (a is null ? 0 : 1);
        var bLength = bItems?.Length ?? (b is null ? 0 : 1);
        if (aLength != bLength)
            return false;
        for (var i = 0; i < aLength; i++)
            if (!ItemEquals(aItems is null ? a : aItems[i], bItems is null ? b : bItems[i]))
                return false;
        return true;
    }

    private static bool ItemEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        switch (a)
        {
            case XdmNode:
            case PhoenixmlDb.XQuery.Ast.XQueryFunction:
                return false;
            case QName qa:
                return b is QName qb
                    && string.Equals(qa.ResolvedNamespace ?? "", qb.ResolvedNamespace ?? "", StringComparison.Ordinal)
                    && string.Equals(qa.LocalName, qb.LocalName, StringComparison.Ordinal);
            case IDictionary<object, object?> map:
                if (b is not IDictionary<object, object?> otherMap || map.Count != otherMap.Count)
                    return false;
                foreach (var entry in map)
                    if (!otherMap.TryGetValue(entry.Key, out var otherValue) || !ValueEquals(entry.Value, otherValue))
                        return false;
                return true;
            case List<object?> array:
                if (b is not List<object?> otherArray || array.Count != otherArray.Count)
                    return false;
                for (var i = 0; i < array.Count; i++)
                    if (!ValueEquals(array[i], otherArray[i]))
                        return false;
                return true;
            default:
                return a.GetType() == b.GetType() && a.Equals(b);
        }
    }

    private static int ValueHash(object? value)
    {
        if (value is not object?[] items)
            return ItemHash(value);
        var hash = new HashCode();
        hash.Add(items.Length);
        foreach (var item in items)
            hash.Add(ItemHash(item));
        return hash.ToHashCode();
    }

    private static int ItemHash(object? item) => item switch
    {
        null => 0,
        XdmNode or PhoenixmlDb.XQuery.Ast.XQueryFunction => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item),
        QName q => HashCode.Combine(q.ResolvedNamespace ?? "", q.LocalName),
        // Content equality, so the hash may only use what equal maps and arrays share.
        IDictionary<object, object?> map => HashCode.Combine(1, map.Count),
        List<object?> array => HashCode.Combine(2, array.Count),
        _ => HashCode.Combine(item.GetType(), item),
    };
}
