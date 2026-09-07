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
/// Lazy wrapper for variable values. Allows deferred evaluation of variables
/// to support XSLT's requirement that unused variables with circular references
/// don't cause errors.
/// </summary>
internal sealed class LazyValue
{
    private readonly Func<ValueTask<object?>> _evaluator;
    private object? _value;
    private bool _evaluated;
    private bool _evaluating; // Guard against circular evaluation
    private Exception? _error;

    public LazyValue(Func<ValueTask<object?>> evaluator)
    {
        _evaluator = evaluator;
    }

    public async ValueTask<object?> GetValueAsync()
    {
        if (_evaluated)
        {
            if (_error != null)
                throw _error;
            return _value;
        }

        if (_evaluating)
        {
            // Circular reference - return null to avoid infinite loop
            return null;
        }

        _evaluating = true;
        try
        {
            var result = await _evaluator().ConfigureAwait(false);
            // Unwrap nested LazyValue if present
            while (result is LazyValue nested)
            {
                result = await nested.GetValueAsync().ConfigureAwait(false);
            }
            _value = result;
            _evaluated = true;
            return _value;
        }
        catch (Exception ex)
        {
            _error = ex;
            _evaluated = true;
            throw;
        }
        finally
        {
            _evaluating = false;
        }
    }
}
