using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Rewrites <c>copy-of((a, b, …))</c> and <c>snapshot((a, b, …))</c> as
/// <c>(copy-of(a), copy-of(b), …)</c>. The two are the same — both functions copy item by item,
/// in order — but the streaming analysis recognizes a copy of one streamed path and not a copy of
/// a sequence. In a streamed xsl:source-document,
/// <c>copy-of((account/transaction/@value, $extra/@value))</c> gave the streamed attributes as
/// untypedAtomic values, and streamed elements in such a sequence were lost (W3C sf-copy-of-004,
/// sf-snapshot-0304). Runs right after parsing, before names are bound.
/// </summary>
internal sealed class CopyOfSequenceDistributor : XQueryExpressionRewriter
{
    internal static readonly CopyOfSequenceDistributor Instance = new();

    public override XQueryExpression VisitFunctionCallExpression(FunctionCallExpression expr)
    {
        var visited = base.VisitFunctionCallExpression(expr);
        if (visited is not FunctionCallExpression call
            || call.Arguments.Count != 1
            || call.Arguments[0] is not SequenceExpression { Items.Count: > 1 } sequence
            || call.Name.LocalName is not ("copy-of" or "snapshot")
            || !IsFnName(call.Name))
            return visited;
        var items = new List<XQueryExpression>(sequence.Items.Count);
        foreach (var item in sequence.Items)
        {
            items.Add(new FunctionCallExpression
            {
                Name = call.Name,
                Arguments = [item],
                Location = item.Location ?? call.Location,
            });
        }
        return new SequenceExpression { Items = items, Location = call.Location };
    }

    // Before names are bound, an fn function is written with no prefix or with "fn".
    private static bool IsFnName(QName name)
        => name.Namespace == NamespaceId.None
            || name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn
            || name.Prefix is "fn";
}
