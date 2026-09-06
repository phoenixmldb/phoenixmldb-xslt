using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Part of an AVT.
/// </summary>
public abstract class AvtPart
{
    public abstract ValueTask<string> EvaluateAsync(XsltExecutionContext context);
}
