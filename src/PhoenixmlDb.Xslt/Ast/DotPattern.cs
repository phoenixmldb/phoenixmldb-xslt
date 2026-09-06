using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT 3.0 "." pattern — matches any item (node, atomic value, etc.).
/// Used in count="." for xsl:number and in group-starting-with=".[predicate]" patterns.
/// </summary>
public sealed class DotPattern : XsltPattern
{
    public IReadOnlyList<XQueryExpression> Predicates { get; init; } = [];

    public override double DefaultPriority
    {
        get
        {
            if (Predicates.Count == 0)
                return -2.0; // XSLT 3.0 §6.4 Table 2: bare "." pattern has priority -2

            // XSLT 3.0 §6.4: If the first predicate has the form ". instance of T",
            // the priority is determined by the ItemType T.
            if (Predicates[0] is PhoenixmlDb.XQuery.Ast.InstanceOfExpression inst
                && inst.Expression is PhoenixmlDb.XQuery.Ast.ContextItemExpression)
            {
                return GetItemTypePriority(inst.TargetType.ItemType);
            }

            return 0.25;
        }
    }

    /// <summary>
    /// Returns the default priority for an ItemType per XSLT 3.0 §6.4 Table 2.
    /// </summary>
    private static double GetItemTypePriority(PhoenixmlDb.XQuery.Ast.ItemType itemType)
    {
        return itemType switch
        {
            PhoenixmlDb.XQuery.Ast.ItemType.Item => -2,
            PhoenixmlDb.XQuery.Ast.ItemType.Node => -1,
            PhoenixmlDb.XQuery.Ast.ItemType.Element => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Attribute => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Text => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Comment => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.ProcessingInstruction => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Document => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.AnyAtomicType => 0,
            PhoenixmlDb.XQuery.Ast.ItemType.Map => -1,
            PhoenixmlDb.XQuery.Ast.ItemType.Array => -1,
            PhoenixmlDb.XQuery.Ast.ItemType.Function => -1,
            // XSLT 3.0 §6.4 Table 2 row K: named atomic types have priority +1
            _ => 1.0
        };
    }

    public override bool Matches(object node, XsltContext context)
    {
        if (Predicates.Count == 0)
            return true; // "." matches everything

        // ".[predicate]" — evaluate predicates using the PredicateEvaluator callback
        context.MatchedNode = node;
        if (context.PredicateEvaluator == null)
            return true; // No evaluator available, match without predicates

        foreach (var pred in Predicates)
        {
            if (!context.PredicateEvaluator(node, pred, 1, 1, node))
                return false;
        }
        return true;
    }

    public override bool MatchesNodeTest(object node) => true;
}
