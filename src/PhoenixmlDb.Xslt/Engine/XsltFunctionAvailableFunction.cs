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

internal sealed class XsltFunctionAvailableFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly PhoenixmlDb.XQuery.Functions.FunctionLibrary _library;
    public XsltFunctionAvailableFunction(PhoenixmlDb.XQuery.Functions.FunctionLibrary library) => _library = library;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "function-available");
    public override XdmSequenceType ReturnType => XdmSequenceType.Boolean;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "name"), Type = XdmSequenceType.String }];
    public override bool IsVariadic => true;
    public override int MaxArity => 2;
    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var nameArg = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(arguments[0]);
        var name = nameArg?.ToString() ?? "";
        // XTDE1400: Validate name is a valid EQName
        XsltFunctionValidation.ValidateQNameArgument(name, "XTDE1400", "function-available");
        var arityArg = arguments.Count > 1 ? PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(arguments[1]) : null;
        // Handle arity arg that may come as object?[] wrapper
        if (arityArg is object?[] arityArr && arityArr.Length > 0)
            arityArg = arityArr[0];
        var arity = arityArg != null ? Convert.ToInt32(arityArg, System.Globalization.CultureInfo.InvariantCulture) : -1;
        // Parse the function name
        QName qname;
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            // EQName syntax: Q{namespace-uri}local-name
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                var namespaceUri = name[2..braceClose];
                var localName = name[(braceClose + 1)..];
                qname = new QName(NamespaceId.None, localName, "") { ExpandedNamespace = namespaceUri };
            }
            else
            {
                qname = new QName(NamespaceId.None, name);
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            qname = new QName(NamespaceId.None, parts[1], parts[0]);
        }
        else
        {
            qname = new QName(NamespaceId.None, name);
        }
        // Try to resolve with various arities
        if (arity >= 0)
        {
            return ValueTask.FromResult<object?>(_library.Resolve(qname, arity) != null);
        }
        // Try common arities 0-3
        for (int i = 0; i <= 3; i++)
        {
            if (_library.Resolve(qname, i) != null)
                return ValueTask.FromResult<object?>(true);
        }
        return ValueTask.FromResult<object?>(false);
    }
}
