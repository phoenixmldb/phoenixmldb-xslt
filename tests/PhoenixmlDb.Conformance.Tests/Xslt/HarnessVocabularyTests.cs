using FluentAssertions;
using System.Xml.Linq;
using Xunit;

namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// Fails when the W3C XSLT catalog uses an element or attribute that
/// <see cref="HarnessVocabulary"/> does not account for, so a catalog feature the harness does
/// not know about is reported rather than silently mis-scoring the cases that use it.
/// </summary>
public class HarnessVocabularyTests
{
    private static readonly XNamespace Catalog = "http://www.w3.org/2012/10/xslt-test-catalog";

    [Fact]
    public void Every_catalog_element_and_attribute_is_accounted_for()
    {
        var suite = ConformanceSuites.Locate("xslt30-test", "XSLT30_TEST_SUITE");
        var catalog = Path.Combine(suite, "catalog.xml");
        if (!File.Exists(catalog))
            Assert.Skip("W3C XSLT 3.0 suite not found. Set XSLT30_TEST_SUITE, or run scripts/fetch-conformance-suites.sh.");

        var used = new SortedSet<string>(StringComparer.Ordinal);
        var files = new List<string> { catalog };
        files.AddRange(XDocument.Load(catalog).Descendants(Catalog + "test-set")
            .Select(ts => Path.Combine(suite, ts.Attribute("file")!.Value))
            .Where(File.Exists));
        foreach (var file in files)
        {
            foreach (var element in XDocument.Load(file).Descendants().Where(e => e.Name.Namespace == Catalog))
            {
                used.Add(element.Name.LocalName);
                foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    used.Add($"{element.Name.LocalName}@{attribute.Name}");
            }
        }

        // The inventory must have seen the catalog, or an empty set would pass.
        used.Should().Contain(["test-case", "environment", "assert-xml"]);

        var unaccounted = used.Where(n => !HarnessVocabulary.Handled.Contains(n)
            && !HarnessVocabulary.Ignored.ContainsKey(n)
            && !HarnessVocabulary.KnownGaps.ContainsKey(n)).ToList();
        unaccounted.Should().BeEmpty(
            "every catalog feature must be handled by XsltTestRunner, ignored with a reason, or listed as a known gap; " +
            "a feature in none of these silently mis-scores the cases that use it");
    }

    [Fact]
    public void The_buckets_do_not_overlap()
    {
        HarnessVocabulary.Handled.Intersect(HarnessVocabulary.Ignored.Keys).Should().BeEmpty();
        HarnessVocabulary.Handled.Intersect(HarnessVocabulary.KnownGaps.Keys).Should().BeEmpty();
        HarnessVocabulary.Ignored.Keys.Intersect(HarnessVocabulary.KnownGaps.Keys).Should().BeEmpty();
    }
}
