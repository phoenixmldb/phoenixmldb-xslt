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

/// <summary>
/// Sentinel value stored in accumulator value maps when a rule raises a dynamic error.
/// The error is deferred until the accumulator value is accessed.
/// </summary>
/// <remarks>
/// <para>
/// Deferral is what XSLT 3.0 permits, not a shortcut: the spec notes that "an implementation
/// that aims to achieve efficiency might evaluate the accumulator LAZILY … only … the first
/// time its value is requested", and dynamic errors are "signaled only if the XPath expression
/// is actually evaluated". Evaluating during descent but reporting on access is observationally
/// equivalent to evaluating on access, so it stays inside the spec. Poisoning the remaining
/// rules is likewise correct — the fold's next value reads <c>$value</c>, so an error genuinely
/// propagates forward.
/// </para>
/// <para>
/// <b>What is NOT deferrable is the point of <see cref="IsDeferrable"/>.</b> The catch sites
/// used to be a bare <c>catch (Exception)</c>, which meant "nothing in here can fail" rather
/// than "dynamic errors are deferred". Two consequences, both real:
/// </para>
/// <list type="bullet">
///   <item><see cref="OperationCanceledException"/> was swallowed and the descent CONTINUED —
///   so a cancelled transform (the conformance harness cancels at 10s) kept walking the tree.</item>
///   <item>A <see cref="NullReferenceException"/> from an engine bug became indistinguishable
///   from a legitimate XPath dynamic error, and vanished entirely when the value was never
///   read. That is how a missing untypedAtomic arm in CoerceAtomicValue surfaced as
///   <c>min="9.99999999999E11"</c> — the seed, reported as though it were the answer. A wrong
///   number that looks like data is a worse failure than a crash.</item>
/// </list>
/// <para>
/// The spec defers <em>dynamic errors</em>. An implementation fault is not one.
/// </para>
/// </remarks>
internal sealed record AccumulatorDeferredError(Exception Error)
{
    /// <summary>
    /// True only for errors XSLT 3.0 lets an accumulator defer: XSLT and XPath/XQuery dynamic
    /// errors, including fn:error. Cancellation, CLR faults and anything else propagate.
    /// </summary>
    internal static bool IsDeferrable(Exception ex)
    {
        // XTDE3400 — a cyclic set of dependencies among accumulators — must surface
        // immediately. It is a defect in the stylesheet's accumulator graph, not a per-node
        // dynamic error, and deferring it hides the cycle behind whichever value happens to be
        // read first.
        if (ex is XsltException { ErrorCode: "XTDE3400" })
            return false;

        return ex is XsltException
            or PhoenixmlDb.XQuery.Execution.XQueryRuntimeException
            or PhoenixmlDb.XQuery.Functions.XQueryException;
    }

    /// <summary>
    /// Rethrows the deferred error with its ORIGINAL stack intact. A bare <c>throw ex</c> resets
    /// the trace to the accumulator-before/after call site, discarding the one fact a reader
    /// needs — which rule, and which code path inside it, actually failed.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void Rethrow()
        => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Error).Throw();
}
