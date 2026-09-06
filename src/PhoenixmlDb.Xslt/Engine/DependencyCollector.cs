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
/// Collects variable and function references from expressions for dependency analysis.
/// </summary>
internal sealed class DependencyCollector : XQueryExpressionWalker
{
    public HashSet<QName> VariableRefs { get; } = new();
    public HashSet<QName> FunctionRefs { get; } = new();

    public override object? VisitVariableReference(VariableReference expr)
    {
        VariableRefs.Add(expr.Name);
        return null;
    }

    public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
    {
        // Track function calls - we need to analyze function bodies for transitive dependencies
        FunctionRefs.Add(expr.Name);
        // Also walk the arguments
        foreach (var arg in expr.Arguments)
            Walk(arg);
        return null;
    }
}
