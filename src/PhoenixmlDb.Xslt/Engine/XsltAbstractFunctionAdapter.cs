using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Stands in for a function of a used package that remains abstract (declared
/// visibility="abstract" and never overridden with a concrete implementation). The component
/// exists — the name is declared — but there is no body to run, so calling it is XTDE3052.
/// Without the stub the call reported "function not found", which is the answer for a name
/// nothing declares at all.
/// </summary>
internal sealed class XsltAbstractFunctionAdapter : XQueryFunction
{
    private readonly int _arity;

    public XsltAbstractFunctionAdapter(QName name, int arity)
    {
        Name = name;
        _arity = arity;
        Parameters = Enumerable.Range(0, arity)
            .Select(i => new FunctionParameterDef
            {
                Name = new QName(NamespaceId.None, $"arg{i + 1}"),
                Type = XdmSequenceType.ZeroOrMoreItems,
            })
            .ToList();
    }

    public override QName Name { get; }
    public override XdmSequenceType ReturnType => XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<FunctionParameterDef> Parameters { get; }

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => throw new XsltException(
            $"XTDE3052: Function {Name.LocalName}#{_arity} is abstract and has no concrete implementation");
}
