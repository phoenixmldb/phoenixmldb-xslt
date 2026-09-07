using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Literal text in AVT.
/// </summary>
public sealed class AvtLiteral : AvtPart
{
    public required string Value { get; init; }

    public override ValueTask<string> EvaluateAsync(XsltExecutionContext context)
    {
        return ValueTask.FromResult(Value);
    }
}
