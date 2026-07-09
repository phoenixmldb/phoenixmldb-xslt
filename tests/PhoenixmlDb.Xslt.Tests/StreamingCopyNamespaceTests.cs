using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streaming copy of a namespaced element selected by a striding path (#192). When a
/// streamed <c>xsl:copy-of</c> / <c>fn:copy-of</c> / <c>fn:snapshot</c> selects an
/// element whose namespaces are declared on an ANCESTOR (not on the element itself),
/// the copy must preserve the element's prefix and its in-scope namespace declarations.
/// Before the fix the watcher-based subtree materializer carried only local names, so
/// the copy lost even the element's own prefix (<c>gml:description</c> became
/// <c>description</c>). Covers sf-copy-of-021/025, sf-snapshot-0321/0325,
/// si-copy-of-020/021/025/026.
/// </summary>
public class StreamingCopyNamespaceTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-copyns-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("main");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Root declares three namespaces (default, gml, xlink); the copied gml:description
    // is a child that declares none of its own — the bindings live on the ancestor.
    private const string Doc = """
        <root xmlns="urn:default" xmlns:gml="urn:gml" xmlns:xlink="urn:xlink">
          <gml:description>hello</gml:description>
        </root>
        """;

    private static string Sheet(string select, string? copyNamespaces) =>
        $$"""
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:strip-space elements="*"/>
          <xsl:template name="main">
            <out>
              <xsl:source-document streamable="yes" href="d.xml">
                <xsl:copy-of select="{{select}}"{{(copyNamespaces == null ? "" : $" copy-namespaces=\"{copyNamespaces}\"")}}/>
              </xsl:source-document>
            </out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task StreamedCopyOf_CopyNamespacesYes_PreservesAllInScopeNamespaces()
    {
        var r = await Run(Sheet("/*/*:description", "yes"), Doc, "d.xml");
        // The copied element keeps its gml prefix and all three ancestor-declared
        // namespaces (default + gml + xlink); the element namespace is urn:gml.
        r.Should().Contain("<gml:description")
            .And.Contain("xmlns=\"urn:default\"")
            .And.Contain("xmlns:gml=\"urn:gml\"")
            .And.Contain("xmlns:xlink=\"urn:xlink\"")
            .And.Contain(">hello</gml:description>");
    }

    [Fact]
    public async Task StreamedCopyOf_CopyNamespacesNo_KeepsOnlyElementNamespace()
    {
        var r = await Run(Sheet("/*/*:description", "no"), Doc, "d.xml");
        // copy-namespaces="no" emits only the namespaces the element/attributes use —
        // here just gml. The unused default and xlink bindings are dropped.
        r.Should().Contain("<gml:description")
            .And.Contain("xmlns:gml=\"urn:gml\"")
            .And.Contain(">hello</gml:description>");
        r.Should().NotContain("urn:default");
        r.Should().NotContain("urn:xlink");
    }

    [Fact]
    public async Task StreamedSnapshot_PreservesInScopeNamespaces()
    {
        // fn:snapshot over the same striding selection routes through the same
        // watcher-materialize path and must preserve namespaces identically.
        var r = await Run(Sheet("snapshot(/*/*:description)", "yes"), Doc, "d.xml");
        r.Should().Contain("<gml:description")
            .And.Contain("xmlns:gml=\"urn:gml\"")
            .And.Contain("xmlns=\"urn:default\"")
            .And.Contain("xmlns:xlink=\"urn:xlink\"")
            .And.Contain(">hello</gml:description>");
    }
}
