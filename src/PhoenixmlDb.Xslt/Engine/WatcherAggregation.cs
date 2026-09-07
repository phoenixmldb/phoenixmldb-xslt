using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Aggregation strategy for a stream watcher.
/// </summary>
internal enum WatcherAggregation
{
    Count,
    Sum,
    Max,
    Min,
    Avg,
    StringJoin,
    Snapshot,
    Sequence,
    /// <summary>
    /// Returns only the first matched item as a scalar (used for fn:head(path) patterns).
    /// </summary>
    Head
}
