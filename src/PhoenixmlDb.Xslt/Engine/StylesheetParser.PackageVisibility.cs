using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{
    /// <summary>
    /// The visibility of each named component of a used package Q in Q itself, before the using
    /// package's xsl:accept is applied (XSLT 3.0 §3.5.3.2). For a component Q declares, the
    /// visibility it declares (private by default in an xsl:package); for one Q took from a
    /// package it uses in turn, the visibility it has in Q, set when Q was built. Keyed by the
    /// component object, so that a clone xsl:accept makes is told apart.
    /// </summary>
    private static Dictionary<object, Visibility> VisibilitiesInUsedPackage(XsltStylesheet package)
    {
        var visibilities = new Dictionary<object, Visibility>(ReferenceEqualityComparer.Instance);
        foreach (var template in package.NamedTemplates.Values)
            visibilities[template] = template.PackageStylesheet is not null
                ? template.VisibilityInUsingPackage ?? template.Visibility
                : Declared(template.VisibilityAttr, template.Visibility, package);
        foreach (var function in package.Functions.Values)
            visibilities[function] = function.PackageStylesheet is not null
                ? function.VisibilityInUsingPackage ?? function.Visibility
                : function.VisibilityStated || function.VisibilityAttr is not null || !package.IsPackage
                    ? function.Visibility : Visibility.Private;
        foreach (var variable in package.Variables)
            visibilities[variable] = variable.PackageStylesheet is not null
                ? variable.VisibilityInUsingPackage ?? variable.Visibility
                : variable.ProvidedByPackage ? variable.Visibility : Visibility.Private;
        foreach (var attributeSet in package.AttributeSets.Values)
            visibilities[attributeSet] = attributeSet.PackageStylesheet is not null
                ? attributeSet.VisibilityInUsingPackage ?? attributeSet.Visibility
                : attributeSet.ProvidedByPackage ? attributeSet.Visibility : Visibility.Private;
        return visibilities;

        static Visibility Declared(string? attribute, Visibility visibility, XsltStylesheet owner)
            => attribute is null && owner.IsPackage ? Visibility.Private : visibility;
    }

    /// <summary>
    /// Records the visibility each named component of the used package has in the using package
    /// (§3.5.3.2): an accepted component has the visibility xsl:accept gave it; any other public
    /// or final one is private, and the rest hidden. A component that xsl:override replaced is
    /// the using package's own and is left alone. Also records, for a global variable, the
    /// packages whose components may reference it.
    /// </summary>
    /// <param name="package">The used package, after xsl:accept.</param>
    /// <param name="inUsedPackage">Each component's visibility in the used package, from <see cref="VisibilitiesInUsedPackage"/> before xsl:accept.</param>
    /// <param name="usingPackage">The package being built.</param>
    /// <param name="overridden">The components xsl:override replaced, which are the using package's own.</param>
    private static void RecordVisibilitiesInUsingPackage(XsltStylesheet package, Dictionary<object, Visibility> inUsedPackage,
        XsltStylesheet usingPackage, HashSet<(string, PhoenixmlDb.Core.QName, int)> overridden)
    {
        foreach (var (name, template) in package.NamedTemplates)
            template.VisibilityInUsingPackage = overridden.Contains(("T", name, 0)) ? null : InUsingPackage(template, template.Visibility);
        foreach (var (key, function) in package.Functions)
            function.VisibilityInUsingPackage = overridden.Contains(("F", key.Name, key.Arity)) ? null : InUsingPackage(function, function.Visibility);
        foreach (var (name, attributeSet) in package.AttributeSets)
            attributeSet.VisibilityInUsingPackage = overridden.Contains(("A", name, 0)) ? null : InUsingPackage(attributeSet, attributeSet.Visibility);
        foreach (var variable in package.Variables)
        {
            if (overridden.Contains(("V", variable.Name, 0)))
                continue;
            var visibility = InUsingPackage(variable, variable.Visibility);
            variable.VisibilityInUsingPackage = visibility;
            variable.VisibleTo ??= [variable.PackageStylesheet ?? package];
            if (visibility is not Visibility.Hidden)
                variable.VisibleTo.Add(usingPackage);
        }

        // A clone that xsl:accept made is not in the map: its visibility is the accepted one.
        Visibility InUsingPackage(object component, Visibility visibility)
            => !inUsedPackage.TryGetValue(component, out var used) ? visibility
                : used is Visibility.Public or Visibility.Final ? Visibility.Private
                : Visibility.Hidden;
    }
}
