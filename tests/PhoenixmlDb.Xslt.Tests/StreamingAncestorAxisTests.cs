using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Under streaming, a matched element's ancestor axis is complete and in document order. The
/// striding descent materialises the matched element DETACHED, so its axis stopped at its own
/// parent: ancestor::* from a node under a/b answered "b" where the unstreamed run answers
/// "a b" — outermost first, with the document node above them (W3C strm/si-apply-templates-001).
/// </summary>
public sealed class StreamingAncestorAxisTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-anc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> RunAsync(string select, string streamable)
    {
        var source = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(source, """<a ID="A"><b ID="B"><c/></b></a>""").ConfigureAwait(true);
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:mode streamable="{{streamable}}" on-no-match="shallow-skip"/>
              <xsl:template name="main">
                <xsl:source-document streamable="{{streamable}}" href="{{new Uri(source).AbsoluteUri}}">
                  <xsl:apply-templates select="a/b"/>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="b"><xsl:apply-templates/></xsl:template>
              <xsl:template match="c"><xsl:value-of select="{{select}}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    /// <summary>Every ancestor, outermost first — the axis is in document order.</summary>
    [Fact]
    public async Task TheAncestorAxis_IsCompleteAndInDocumentOrder()
        => (await RunAsync("string-join(ancestor::*/name(), ' ')", "yes")).Should().Be("a b",
            "the streamed axis stopped at the matched element's own parent");

    [Fact]
    public async Task TheStreamedAxis_AgreesWithTheUnstreamedOne()
        => (await RunAsync("string-join(ancestor::*/name(), ' ')", "yes"))
            .Should().Be(await RunAsync("string-join(ancestor::*/name(), ' ')", "no"));

    /// <summary>Synthesized ancestors carry their attributes.</summary>
    [Fact]
    public async Task AncestorsKeepTheirAttributes()
        => (await RunAsync("string-join(ancestor::*/@ID, ' ')", "yes")).Should().Be("A B");

    /// <summary>Every XDM tree is rooted at a document node, so ancestor::node() counts one more.</summary>
    [Fact]
    public async Task TheChainIsRootedAtADocumentNode()
        => (await RunAsync("count(ancestor::node())", "yes")).Should().Be("3");
}
