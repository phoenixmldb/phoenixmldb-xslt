using System.Collections.Concurrent;
using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression test for #116: <see cref="StylesheetParser.ResolveNamespaceUri"/> interns URIs into a
/// process-wide table. It used a plain <c>Dictionary</c> with a check-then-act plus a non-atomic
/// <c>_nextNamespaceId++</c>, so concurrent transforms/parses could corrupt the table (throw /
/// loop), hand the same id to two URIs, or lose an increment. The fix uses a ConcurrentDictionary
/// + Interlocked counter. URIs are GUID-prefixed so the assertions hold regardless of other
/// namespaces any concurrently-running test registers into the shared static table.
/// </summary>
public class ResolveNamespaceUriRaceTests
{
    [Fact]
    public void ResolveNamespaceUri_is_threadsafe_under_concurrent_interning()
    {
        var prefix = $"urn:race:{Guid.NewGuid()}:";
        const int distinct = 200;
        const int threads = 16;
        var uris = Enumerable.Range(0, distinct).Select(i => prefix + i).ToArray();

        // Many threads intern the same URIs at the same time AND the whole set is interned
        // concurrently — exactly the contention that corrupted the plain Dictionary.
        var observed = new ConcurrentDictionary<string, ConcurrentBag<NamespaceId>>();

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            foreach (var uri in uris)
            {
                var id = StylesheetParser.ResolveNamespaceUri(uri);
                observed.GetOrAdd(uri, _ => new ConcurrentBag<NamespaceId>()).Add(id);
            }
        });

        // Every URI was resolved, and each resolves to exactly ONE id across all threads
        // (no torn read, no duplicate allocation for the same URI).
        observed.Should().HaveCount(distinct);
        foreach (var (uri, ids) in observed)
            ids.Distinct().Should().ContainSingle($"'{uri}' must intern to a single stable id across all threads");

        // Distinct URIs must hold distinct ids — no two URIs collided onto one id.
        var assigned = uris.Select(StylesheetParser.ResolveNamespaceUri).ToArray();
        assigned.Should().OnlyHaveUniqueItems("each distinct namespace URI must intern to a unique id");
    }
}
