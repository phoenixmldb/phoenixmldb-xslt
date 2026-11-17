using System.Collections;
using System.Reflection;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{
    /// <summary>
    /// A reference, in a component of this package, to a component of a used package that this
    /// package cannot see: XPST0017 for a function, XTSE0650 for a named template, XTSE0710 for an
    /// attribute set. Not visible means hidden here by XSLT 3.0 §3.5.3.2: private in the used
    /// package (or hidden by xsl:accept), or taken by the used package from a package it uses and
    /// not accepted by it as public or final, so private there (xslt#544). The components of used
    /// packages are merged so that their own components can reference them, and a reference from
    /// the using package resolved to them and ran them (W3C use-package-003, expose-003).
    /// Global variables are checked where they are read (XPST0008), where a local binding of the
    /// same name is known.
    /// </summary>
    /// <remarks>
    /// Run when the package is whole, before it is merged into a package that uses it: its own
    /// components are the ones not yet stamped with a package. Nothing to do, and nothing walked,
    /// when no component of a used package is out of reach.
    /// </remarks>
    private static void ValidateCallsToInaccessibleFunctions(XsltStylesheet stylesheet)
    {
        HashSet<(QName Name, int Arity)>? unreachable = null;
        foreach (var (key, function) in stylesheet.Functions)
        {
            if (function.PackageStylesheet is null)
                continue;
            // Hidden here (§3.5.3.2): private in the used package, or public there but taken by
            // it from a package it uses without accepting it as public (xslt#544).
            if (IsPrivateToItsPackage(function) || function.VisibilityInUsingPackage is Visibility.Hidden)
                (unreachable ??= []).Add(key);
        }
        foreach (var key in stylesheet.HiddenFunctionKeys)
            if (!stylesheet.Functions.ContainsKey(key))
                (unreachable ??= []).Add(key);
        HashSet<QName>? hiddenTemplates = null;
        foreach (var (name, template) in stylesheet.NamedTemplates)
            if (template.PackageStylesheet is not null && template.VisibilityInUsingPackage is Visibility.Hidden)
                (hiddenTemplates ??= []).Add(name);
        HashSet<QName>? hiddenAttributeSets = null;
        foreach (var (name, attributeSet) in stylesheet.AttributeSets)
            if (attributeSet.PackageStylesheet is not null && attributeSet.VisibilityInUsingPackage is Visibility.Hidden)
                (hiddenAttributeSets ??= []).Add(name);
        if (unreachable is null && hiddenTemplates is null && hiddenAttributeSets is null)
            return;

        var walker = new InaccessibleCallFinder(unreachable ?? [], hiddenTemplates ?? [], hiddenAttributeSets ?? []);
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

        foreach (var attributeSet in stylesheet.AttributeSets.Values)
            if (attributeSet.PackageStylesheet is null) walker.VisitAttributeSets(attributeSet.UseAttributeSets);

        if (walker.Found is { } call)
            throw new XsltException(
                $"XPST0017: The function {DisplayName(call.Name)}#{call.Arguments.Count} is not available here: it is private to the package that declares it, or to a package between");
        if (walker.FoundTemplate is { } callTemplate)
            throw new XsltException(
                $"XTSE0650: The named template {DisplayName(callTemplate.Name)} is not available here: it is not visible in this package (a template of a used package is visible only where it is accepted)",
                callTemplate.Location);
        if (walker.FoundAttributeSet is { } attributeSetName)
            throw new XsltException(
                $"XTSE0710: The attribute set {DisplayName(attributeSetName)} is not available here: it is not visible in this package (an attribute set of a used package is visible only where it is accepted)");
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
    private sealed class InaccessibleCallFinder(HashSet<(QName Name, int Arity)> unreachable, HashSet<QName> hiddenTemplates,
        HashSet<QName> hiddenAttributeSets)
    {
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public FunctionCallExpression? Found { get; private set; }

        public XsltCallTemplate? FoundTemplate { get; private set; }

        public QName? FoundAttributeSet { get; private set; }

        private bool Done => Found is not null || FoundTemplate is not null || FoundAttributeSet is not null;

        public void VisitAttributeSets(List<QName> names)
        {
            foreach (var name in names)
            {
                if (!Done && hiddenAttributeSets.Contains(name))
                    FoundAttributeSet = name;
            }
        }

        public void Visit(object? node)
        {
            if (Done || node is null or string or ValueType)
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
            if (node is XsltCallTemplate callTemplate && hiddenTemplates.Contains(callTemplate.Name))
            {
                FoundTemplate = callTemplate;
                return;
            }
            switch (node)
            {
                case XsltLiteralResultElement { UseAttributeSets.Count: > 0 } lre: VisitAttributeSets(lre.UseAttributeSets); break;
                case XsltElement { UseAttributeSets.Count: > 0 } element: VisitAttributeSets(element.UseAttributeSets); break;
                case XsltCopy { UseAttributeSets.Count: > 0 } copy: VisitAttributeSets(copy.UseAttributeSets); break;
            }
            if (Done)
                return;
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
