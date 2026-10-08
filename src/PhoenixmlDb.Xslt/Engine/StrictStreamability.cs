using System.Runtime.CompilerServices;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The template rules of a streamable mode that the engine runs although they are not
/// guaranteed-streamable (XSLT 3.0 §19): it buffers the matched element's subtree, or evaluates
/// the transformation on a tree, and the result is right. XSLT 3.0 §19.10 allows that only where
/// the user has asked for it; a processor is otherwise to report XTSE3430. Under
/// <see cref="XsltTransformOptions.StrictStreamability"/> the engine reports it (xslt#295, #298).
/// </summary>
/// <remarks>
/// One finding, chosen so that it refuses nothing that is streamable: more than one operand
/// reading downward from the matched node. Forced on for the W3C streaming sets it changed no
/// result in 2,374 cases. A stylesheet without that finding may still not be guaranteed-streamable;
/// this is not the full posture and sweep classification. In particular a rule that reads outside
/// the node it matched (<see cref="StreamedScopeEscapeDetector"/>, xslt#298) is not reported:
/// that detector is over-inclusive by design, which suits a fallback to the tree and not an
/// error. As an error it refused five streamable W3C cases (si-for-each-801 and -807,
/// si-group-062 and -065, si-iterate-037), such as <c>//x</c> in a rule that matches <c>/</c>.
/// </remarks>
internal static class StrictStreamability
{
    private static readonly ConditionalWeakTable<XsltStylesheet, StrongBox<XsltException?>> _cache = new();

    /// <summary>The XTSE3430 error for the first such rule, or null when none is found.</summary>
    public static XsltException? Finding(XsltStylesheet stylesheet)
        => _cache.GetValue(stylesheet, s => new StrongBox<XsltException?>(Compute(s))).Value;

    private static XsltException? Compute(XsltStylesheet stylesheet)
    {
        foreach (var template in StreamedScopeEscapeDetector.StreamableModeRules(stylesheet))
        {
            if (StreamabilityChecker.HasSeveralConsumingOperands(template.Body))
                return new XsltException(
                    "XTSE3430: The body of this template in a streamable mode is not guaranteed streamable: "
                    + "more than one instruction reads the children of the matched node, and the streamed input passes once",
                    template.Body.Location);
        }
        return null;
    }
}
