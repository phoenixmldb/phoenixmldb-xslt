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
/// The XQuery execution context's context item, or <c>null</c> when it cannot be read.
/// </summary>
/// <remarks>
/// Four call sites hand-rolled this as
/// <code>
/// try { node = context.ContextItem; } catch (InvalidOperationException) { /* absent focus */ }
/// node ??= _context.ContextItem;
/// </code>
/// which was wrong twice over. <c>ExecutionContext</c> is an INTERFACE with two
/// implementations and NEITHER throws InvalidOperationException:
/// <see cref="DefaultXsltExecutionContext"/> returns the
/// <see cref="PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus"/> SENTINEL, and
/// QueryExecutionContext throws <c>XQueryRuntimeException</c> (XPDY0002). So the catch was
/// unreachable — and because the sentinel is NON-NULL, <c>??=</c> did not fall back either:
/// the sentinel travelled on as if it were the context item.
///
/// This removes the misleading guards WITHOUT changing behaviour. Making them actually catch
/// something — either by folding the sentinel to null or by catching the XQueryRuntimeException
/// that is really thrown — breaks W3C accumulator-061 both ways. The absent-focus signal is
/// load bearing as it stands; the four dead catches were harmless only because they never
/// fired.
/// </remarks>
internal static class XQueryFocus
{
    internal static object? ItemOrNull(PhoenixmlDb.XQuery.Ast.ExecutionContext? context)
    {
        // No try/catch, deliberately. Two things were tried here and both changed behaviour:
        //
        //   folding the AbsentFocus sentinel to null   -> the callers' `?? _context.ContextItem`
        //                                                 fallback fires where it did not before
        //   catching XQueryRuntimeException (XPDY0002) -> same, by a different route
        //
        // Both broke W3C accumulator-061, which reads an accumulator at a node the fallback
        // then changes. So the propagating XPDY0002 and the travelling sentinel are both load
        // bearing, and the dead `catch (InvalidOperationException)` this replaces was harmless
        // precisely BECAUSE it never fired.
        //
        // What is fixed here is only the lie: four sites claimed to handle absent focus and
        // could not. Why the current behaviour is correct is worth understanding before anyone
        // makes those guards work — see BUGS.md.
        return context?.ContextItem;
    }
}
