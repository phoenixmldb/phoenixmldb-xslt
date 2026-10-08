using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Interface for parsing XPath/XQuery expressions.
/// </summary>
public interface IExpressionParser
{
    XQueryExpression Parse(string expression);
}

/// <summary>
/// An expression parser that can take the prefix of a type name from the host's static context.
/// </summary>
internal interface ITypeNamespaceAwareExpressionParser : IExpressionParser
{
    /// <param name="expression">The expression text.</param>
    /// <param name="typeNamespaceResolver">The namespace URI in scope for a prefix, or null.</param>
    XQueryExpression Parse(string expression, Func<string, string?> typeNamespaceResolver);
}
