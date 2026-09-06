using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Override declaration in use-package.
/// </summary>
public sealed class XsltOverride
{
    public List<XsltTemplate> Templates { get; init; } = new();
    public List<XsltFunction> Functions { get; init; } = new();
    public List<XsltVariable> Variables { get; init; } = new();
    public List<XsltParam> Parameters { get; init; } = new();
    public List<XsltAttributeSet> AttributeSets { get; init; } = new();
}
