using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Phase for accumulator rule execution.
/// </summary>
public enum AccumulatorPhase
{
    /// <summary>Execute when entering the node (start-tag).</summary>
    Start,
    /// <summary>Execute when leaving the node (end-tag).</summary>
    End
}
