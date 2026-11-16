using System.Collections;
using System.Reflection;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{
    /// <summary>
    /// XPST0017 for a call, in a component of this package, to a function of a used package that
    /// this package cannot see: one hidden by xsl:accept, or one private in its own package and not
    /// accepted (XSLT 3.0 §3.5.3.2: a private component of a used package is not a component of the
    /// using package). The functions of a used package are merged so that its own components can
    /// call them, and the call from the using package resolved to the private function and ran it
    /// (W3C use-package-003, expose-003).
    /// </summary>
    /// <remarks>
    /// Run when the package is whole, before it is merged into a package that uses it: its own
    /// components are the ones not yet stamped with a package. Nothing to do, and nothing walked,
    /// when no function of a used package is out of reach.
    /// </remarks>
    private static void ValidateCallsToInaccessibleFunctions(XsltStylesheet stylesheet)
    {
        HashSet<(QName Name, int Arity)>? unreachable = null;
        foreach (var (key, function) in stylesheet.Functions)
        {
            if (function.PackageStylesheet is null)
                continue;
            if (IsPrivateToItsPackage(function))
                (unreachable ??= []).Add(key);
        }
        foreach (var key in stylesheet.HiddenFunctionKeys)
            if (!stylesheet.Functions.ContainsKey(key))
                (unreachable ??= []).Add(key);
        if (unreachable is null)
            return;

        var walker = new InaccessibleCallFinder(unreachable);
        foreach (var template in stylesheet.Templates)
            if (template.PackageStylesheet is null) walker.Visit(template.Body);
        foreach (var template in stylesheet.NamedTemplates.Values)
            if (template.PackageStylesheet is null) walker.Visit(template.Body);
        foreach (var function in stylesheet.Functions.Values)
            if (function.PackageStylesheet is null) walker.Visit(function.Body);
        foreach (var variable in stylesheet.Variables)
            if (variable.PackageStylesheet is null) { walker.Visit(variable.Select); walker.Visit(variable.Content); }
        foreach (var attributeSet in stylesheet.AttributeSets.Values)
            if (attributeSet.PackageStylesheet is null) walker.Visit(attributeSet.Attributes);
        foreach (var key in stylesheet.Keys.Values)
            if (key.PackageStylesheet is null) { walker.Visit(key.Use); walker.Visit(key.UseContent); }

        if (walker.Found is { } call)
            throw new XsltException(
                $"XPST0017: The function {DisplayName(call.Name)}#{call.Arguments.Count} is not available here: it is private to the package that declares it");
    }

    /// <summary>
    /// A function of a used package that the using package cannot call: hidden by xsl:accept, or
    /// private where it is declared, stated so or by the default of an xsl:package (§3.5).
    /// Accepted with any other visibility, including private, it is the using package's.
    /// </summary>
    private static bool IsPrivateToItsPackage(XsltFunction function)
    {
        if (function.Visibility is Visibility.Hidden)
            return true;
        if (function.VisibilityFromAccept)
            return false;
        return function.Visibility is Visibility.Private
            || (!function.VisibilityStated && function.PackageStylesheet is { IsPackage: true });
    }

    private static string DisplayName(QName name)
        => name.Prefix is { Length: > 0 } prefix ? prefix + ":" + name.LocalName : name.LocalName;

    /// <summary>
    /// Finds a call to one of the given functions anywhere in an instruction or expression tree,
    /// through every property of the AST types. It does not enter another declaration (a function
    /// or template a node refers to), only the tree it is given.
    /// </summary>
    private sealed class InaccessibleCallFinder(HashSet<(QName Name, int Arity)> unreachable)
    {
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public FunctionCallExpression? Found { get; private set; }

        public void Visit(object? node)
        {
            if (Found is not null || node is null or string or ValueType)
                return;
            if (node is XsltStylesheet or XsltFunction or XsltTemplate or XsltMode)
                return;
            if (!_seen.Add(node))
                return;
            if (node is FunctionCallExpression call && unreachable.Contains((call.Name, call.Arguments.Count)))
            {
                Found = call;
                return;
            }
            if (node is IEnumerable items)
            {
                foreach (var item in items)
                    Visit(item);
                return;
            }
            var ns = node.GetType().Namespace;
            if (ns is null || !(ns.StartsWith("PhoenixmlDb.Xslt.Ast", StringComparison.Ordinal)
                    || ns.StartsWith("PhoenixmlDb.XQuery.Ast", StringComparison.Ordinal)))
                return;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;
                var type = property.PropertyType;
                if (type == typeof(string) || type.IsValueType)
                    continue;
                object? value;
                try { value = property.GetValue(node); }
                catch (TargetInvocationException) { continue; }
                Visit(value);
            }
        }
    }
}
