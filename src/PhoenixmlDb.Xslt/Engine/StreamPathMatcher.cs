using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Matches a path pattern against the streaming element stack.
/// </summary>
internal sealed class StreamPathMatcher
{
    private readonly string[] _steps;

    public StreamPathMatcher(string pathPattern)
    {
        // Parse "transactions/transaction" into ["transactions", "transaction"]
        // or "transactions/transaction/@value" into ["transactions", "transaction", "@value"]
        _steps = pathPattern.Split('/');
    }

    /// <summary>
    /// Checks if the current element stack matches this pattern.
    /// </summary>
    /// <param name="ancestorStack">Current element name stack (outermost first).</param>
    /// <param name="currentName">The current element's local name.</param>
    /// <param name="contextRootDepth">
    /// Stack index (in <paramref name="ancestorStack"/> terms, 0-indexed
    /// outermost-first) of the watcher's context-root element: the leftmost concrete
    /// step is anchored as a child of that root. A value of <c>null</c> (the default)
    /// preserves the legacy floating behavior — a relative path matches at any depth.
    /// <c>-1</c> anchors to the document NODE (xsl:source-document): its child
    /// elements fire at stack index 0, i.e. with an empty ancestor stack. A deferred
    /// matched-template passes the active reader depth (its <c>ParentDepth</c>), the
    /// stack index of the matched element.
    /// </param>
    public bool Matches(IReadOnlyList<string> ancestorStack, string currentName, int? contextRootDepth = null)
    {
        // Build full path: ancestors + current
        // Match against steps from the end
        if (_steps.Length == 0) return false;

        var lastStep = _steps[^1];

        // Attribute step — matched separately via MatchesAttribute
        if (lastStep.StartsWith('@')) return false;

        if (lastStep != currentName && lastStep != "*") return false;

        if (_steps.Length == 1) return contextRootDepth is null || ancestorStack.Count == contextRootDepth.Value + 1;

        return MatchAncestors(_steps, _steps.Length - 2, ancestorStack, ancestorStack.Count - 1, contextRootDepth);
    }

    /// <summary>
    /// Walks the step list backward against the ancestor stack. A "**" step is a
    /// descendant-axis marker that consumes zero or more ancestor entries, so the
    /// match becomes nondeterministic at that position — we try the shortest
    /// alignment first and fall back to deeper skips.
    /// </summary>
    private static bool MatchAncestors(string[] steps, int stepIdx, IReadOnlyList<string> ancestorStack, int stackIdx, int? contextRootDepth)
    {
        while (stepIdx >= 0)
        {
            var step = steps[stepIdx];
            if (step == "**")
            {
                // Descendant marker: match zero or more ancestor names. Recurse
                // for each possible alignment of the next concrete step.
                if (stepIdx == 0) return true; // unbounded left edge — anything above is fine (stays floating)
                var nextStep = steps[stepIdx - 1];
                for (var i = stackIdx; i >= 0; i--)
                {
                    if (nextStep == "**" || nextStep == ancestorStack[i] || nextStep == "*")
                    {
                        if (MatchAncestors(steps, stepIdx - 2, ancestorStack, i - 1, contextRootDepth)) return true;
                    }
                }
                return false;
            }
            if (stackIdx < 0) return false;
            if (step != ancestorStack[stackIdx] && step != "*") return false;
            stepIdx--;
            stackIdx--;
        }
        // Reached the leftmost concrete step. With a null sentinel the relative path
        // floats (matches at any depth); otherwise anchor that step as a child of the
        // context root, i.e. the next unconsumed ancestor index must equal
        // contextRootDepth (the stack index of the context-root element).
        return contextRootDepth is null || stackIdx == contextRootDepth.Value;
    }

    /// <summary>
    /// Checks if the pattern ends with an attribute step and the element path matches.
    /// Returns the attribute local name if matched, null otherwise.
    /// </summary>
    public string? MatchesAttribute(IReadOnlyList<string> ancestorStack, string currentElementName, int? contextRootDepth = null)
    {
        if (_steps.Length < 2) return null;
        var lastStep = _steps[^1];
        if (!lastStep.StartsWith('@')) return null;

        // Check the element path (all steps except the last)
        var elementSteps = _steps[..^1];
        if (elementSteps[^1] != currentElementName && elementSteps[^1] != "*") return null;

        if (!MatchAncestors(elementSteps, elementSteps.Length - 2, ancestorStack, ancestorStack.Count - 1, contextRootDepth))
            return null;

        return lastStep[1..]; // Strip the @ prefix
    }
}
