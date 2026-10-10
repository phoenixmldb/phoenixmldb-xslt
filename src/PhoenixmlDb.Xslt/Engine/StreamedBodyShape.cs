using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// What the body of a template rule in a streamable mode needs from the streamed input, as far
/// as the choice between running it on the live reader and on a buffered copy of the matched
/// element goes (xslt#340).
/// </summary>
/// <remarks>
/// The live reader gives the children of the matched element to an instruction that walks them
/// itself: xsl:apply-templates, xsl:for-each, xsl:iterate, xsl:for-each-group and the like. A
/// body with no such instruction that still reads the children (a variable, a conditional
/// expression, an attribute value) has nothing to walk them for it. Run on the live reader it
/// saw no children, or was run late and lost the end tags of the elements around it. Such a
/// body is run on a buffered copy of the matched element, which costs the memory of that one
/// element and gives the result the tree gives.
/// </remarks>
internal static class StreamedBodyShape
{
    private static readonly ConditionalWeakTable<XsltSequenceConstructor, StrongBox<bool>> _cache = new();

    /// <summary>True when the body holds, at any depth, an instruction that walks the streamed input itself.</summary>
    public static bool WalksTheInputItself(XsltSequenceConstructor body)
        => _cache.GetValue(body, b => new StrongBox<bool>(Walks(b, new HashSet<object>(ReferenceEqualityComparer.Instance)))).Value;

    private static readonly ConditionalWeakTable<XsltSequenceConstructor, StrongBox<bool>> _reads = new();

    /// <summary>
    /// True when the body reads below the matched node: by the rules of the specification its
    /// sweep is not motionless. A body that reads only the node itself, its attributes and its
    /// ancestors is motionless, and runs on the live reader, where the ancestors are.
    /// </summary>
    public static bool ReadsTheChildren(XsltSequenceConstructor body, XsltStylesheet stylesheet)
        => _reads.GetValue(body, b => new StrongBox<bool>(
            Streamability.StreamabilityClassifier.Classify(b, new Streamability.StreamingContext(
                Streamability.Posture.Striding, InStreamedScope: true, BySpec: true,
                Functions: stylesheet.Functions, Stylesheet: stylesheet)).Sweep != Streamability.Sweep.Motionless)).Value;

    private static bool Walks(object? node, HashSet<object> seen)
    {
        if (node is null or string or ValueType or XQueryExpression)
            return false;
        if (!seen.Add(node))
            return false;
        if (node is IEnumerable items)
        {
            foreach (var item in items)
                if (Walks(item, seen))
                    return true;
            return false;
        }
        if (node is XsltApplyTemplates or XsltForEach or XsltIterate or XsltForEachGroup or XsltFork
            or XsltSourceDocument or XsltMerge or XsltNextMatch or XsltApplyImports or XsltCallTemplate)
            return true;
        if (node is not (XsltInstruction or XsltWhen))
            return false;
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && Walks(property.GetValue(node), seen))
                return true;
        }
        return false;
    }
}
