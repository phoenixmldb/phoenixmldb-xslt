using System.Collections.Generic;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// Activates the <see cref="StreamabilityAnnotation"/> side-table by classifying a set of
/// independently-classifiable units (template bodies, streamable constructs) with a root
/// streaming context and caching each unit's composed <see cref="PostureSweep"/>. This makes
/// unit-level posture queryable without re-running the classifier — the substrate posture-driven
/// consumers (SP-B onward) build on.
/// </summary>
/// <remarks>
/// Scope: annotation is at the granularity of whole classifiable units. Per-node compositional
/// annotation (where a child's streaming context differs from the unit root) is deferred to the
/// first consumer (SP-B): <see cref="StreamabilityClassifier.Classify(XsltInstruction, StreamingContext)"/>
/// composes a unit's posture by recursing internally and does not surface per-child contexts, so
/// the consuming context is what defines a correct per-node annotation.
/// </remarks>
public static class AnnotationPopulator
{
    /// <summary>
    /// Classifies each unit in <paramref name="units"/> under <paramref name="ctx"/> and stores the
    /// result in <see cref="StreamabilityAnnotation"/>, keyed by unit identity.
    /// </summary>
    public static void Annotate(IEnumerable<XsltInstruction> units, StreamingContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(units);
        foreach (var unit in units)
        {
            var ps = StreamabilityClassifier.Classify(unit, ctx);
            StreamabilityAnnotation.Set(unit, ps);
        }
    }
}
