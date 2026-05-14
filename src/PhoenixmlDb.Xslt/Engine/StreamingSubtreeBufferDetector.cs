using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Detects whether a template body needs the matched element's full subtree
/// materialized before execution. Triggered by snapshot()/copy-of() over the
/// context node or descendants — operations that demand a buffered XdmElement
/// and therefore cannot run against the streaming reader directly.
/// </summary>
/// <remarks>
/// Narrowed: only triggers when the snapshot()/copy-of() argument touches the
/// matched subtree — bare <c>.</c>, or a relative path whose first step uses
/// a downward axis (child / descendant / descendant-or-self / attribute / self).
/// Absolute paths (<c>/foo</c>), <c>doc()</c> calls, and external variable
/// references don't need buffering — they read from in-memory trees the
/// streaming pass never touches. <see cref="XsltCopyOf"/> with a context-only
/// or downward select still triggers buffering; absolute selects do not.
/// </remarks>
internal static class StreamingSubtreeBufferDetector
{
    // Compile-time-stable AST nodes — same body reference recurs across every
    // element match. Cache the scan so we pay it once per body, not per element.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XsltSequenceConstructor, object> _cache = new();
    private static readonly object _trueBox = true;
    private static readonly object _falseBox = false;

    public static bool RequiresSubtreeBuffer(XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        if (_cache.TryGetValue(body, out var cached))
            return ReferenceEquals(cached, _trueBox);

        var result = false;
        foreach (var insn in body.Instructions)
        {
            if (InstructionRequiresBuffer(insn)) { result = true; break; }
        }
        _cache.Add(body, result ? _trueBox : _falseBox);
        return result;
    }

    private static bool InstructionRequiresBuffer(XsltInstruction insn)
    {
        switch (insn)
        {
            case XsltCopyOf cof:
                return TouchesMatchedSubtree(cof.Select);

            case XsltVariableInstruction v:
                if (v.Select != null && ExpressionUsesSnapshot(v.Select)) return true;
                if (v.Content != null && RequiresSubtreeBuffer(v.Content)) return true;
                return false;

            case XsltValueOf vo:
                return vo.Select != null && ExpressionUsesSnapshot(vo.Select);

            case XsltSequence s:
                if (s.Select != null && ExpressionUsesSnapshot(s.Select)) return true;
                if (s.Content != null && RequiresSubtreeBuffer(s.Content)) return true;
                return false;

            case XsltIf i:
                if (ExpressionUsesSnapshot(i.Test)) return true;
                if (i.Then != null && RequiresSubtreeBuffer(i.Then)) return true;
                return false;

            case XsltChoose c:
                foreach (var w in c.When)
                {
                    if (ExpressionUsesSnapshot(w.Test)) return true;
                    if (RequiresSubtreeBuffer(w.Body)) return true;
                }
                if (c.Otherwise != null && RequiresSubtreeBuffer(c.Otherwise)) return true;
                return false;

            case XsltForEach fe:
                if (ExpressionUsesSnapshot(fe.Select)) return true;
                return RequiresSubtreeBuffer(fe.Body);

            case XsltLiteralResultElement lre:
                return RequiresSubtreeBuffer(lre.Content);

            case XsltCopy cp:
                return cp.Content != null && RequiresSubtreeBuffer(cp.Content);

            case XsltFork fk:
                foreach (var seq in fk.Sequences)
                    if (RequiresSubtreeBuffer(seq)) return true;
                foreach (var feg in fk.ForEachGroups)
                    if (feg.Body != null && RequiresSubtreeBuffer(feg.Body)) return true;
                foreach (var rd in fk.ResultDocuments)
                    if (rd.Content != null && RequiresSubtreeBuffer(rd.Content)) return true;
                return false;

            case XsltWherePopulated wp:
                return RequiresSubtreeBuffer(wp.Content);
            case XsltOnEmpty oe:
                return (oe.Content != null && RequiresSubtreeBuffer(oe.Content))
                       || (oe.Select != null && ExpressionUsesSnapshot(oe.Select));
            case XsltOnNonEmpty one:
                return (one.Content != null && RequiresSubtreeBuffer(one.Content))
                       || (one.Select != null && ExpressionUsesSnapshot(one.Select));

            case XsltSequenceConstructor ctor:
                return RequiresSubtreeBuffer(ctor);

            default:
                return false;
        }
    }

    private static bool ExpressionUsesSnapshot(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc when IsSnapshotOrCopyOf(fc):
                return fc.Arguments.Count > 0 && TouchesMatchedSubtree(fc.Arguments[0]);

            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    if (ExpressionUsesSnapshot(arg)) return true;
                return false;

            case BinaryExpression bin:
                return ExpressionUsesSnapshot(bin.Left) || ExpressionUsesSnapshot(bin.Right);

            case UnaryExpression un:
                return ExpressionUsesSnapshot(un.Operand);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (ExpressionUsesSnapshot(item)) return true;
                return false;

            case PathExpression path:
                foreach (var step in path.Steps)
                    foreach (var pred in step.Predicates)
                        if (ExpressionUsesSnapshot(pred)) return true;
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="expr"/> evaluates relative to the matched
    /// element (the streaming context node) — the only case the streaming
    /// pass cannot satisfy without buffering the subtree. Bare <c>.</c>,
    /// relative paths whose first step uses a downward axis, and parenthesized
    /// versions of these all qualify. Absolute paths, <c>doc()</c>/<c>doc-available()</c>,
    /// and external variable refs read from already-materialized trees and
    /// don't need buffering.
    /// </summary>
    private static bool TouchesMatchedSubtree(XQueryExpression expr)
    {
        switch (expr)
        {
            case ContextItemExpression:
                return true;

            case PathExpression path:
                if (path.IsAbsolute) return false;
                if (path.Steps.Count == 0) return false;
                var firstAxis = path.Steps[0].Axis;
                return firstAxis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf
                    or Axis.Attribute or Axis.Self;

            // snapshot()/copy-of() of an already-grounded subtree (rare nesting)
            case FunctionCallExpression fc when IsSnapshotOrCopyOf(fc):
                return fc.Arguments.Count > 0 && TouchesMatchedSubtree(fc.Arguments[0]);

            default:
                return false;
        }
    }

    private static bool IsSnapshotOrCopyOf(FunctionCallExpression fc)
    {
        return fc.Name.LocalName is "snapshot" or "copy-of"
            && (fc.Name.Namespace == NamespaceId.None
                || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }
}
