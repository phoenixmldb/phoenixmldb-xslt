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
/// + Interlocked counter, and ALL the QName-parsing sites (name-attribute EQNames, foreign-namespace
/// attributes, prefixed QNames, Q{}-EQNames) now funnel through that one method instead of each
/// inlining its own check-then-act. URIs are GUID-prefixed so the assertions hold regardless of
/// other namespaces any concurrently-running test registers into the shared static table.
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

    /// <summary>
    /// Drives the rerouted QName-parsing entry points end-to-end under contention: parsing a
    /// stylesheet that uses a prefixed name (<c>d:out</c>), a foreign-namespace attribute
    /// (<c>d:attr</c>), and a <c>Q{uri}</c> EQName template name all reach the shared intern table.
    /// Each thread parses with its own GUID-unique namespace, so after the storm every per-thread
    /// URI must still intern to one unique, stable id and no parse may throw.
    /// </summary>
    [Fact]
    public void Concurrent_stylesheet_parsing_interns_qnames_safely()
    {
        const int threads = 16;
        var uris = Enumerable.Range(0, threads)
            .Select(_ => $"urn:race:{Guid.NewGuid()}")
            .ToArray();

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var uri = uris[i];
            var xslt = """
                <xsl:stylesheet version="3.0"
                    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                    xmlns:d="__URI__">
                  <xsl:template match="/" name="Q{__URI__}entry">
                    <d:out d:attr="x"/>
                  </xsl:template>
                </xsl:stylesheet>
                """.Replace("__URI__", uri);
            // Each thread gets its own parser (parser state is per-instance; only the namespace
            // intern table is shared static state — exactly what we're stressing).
            var parser = new StylesheetParser(new MockExpressionParser());
            var act = () => parser.Parse(xslt);
            act.Should().NotThrow($"parsing the stylesheet for '{uri}' must not race the intern table");
        });

        // Every thread's namespace interned to a single, distinct id.
        var assigned = uris.Select(StylesheetParser.ResolveNamespaceUri).ToArray();
        assigned.Should().OnlyHaveUniqueItems("each per-thread namespace URI must intern to a unique id");
    }
}
