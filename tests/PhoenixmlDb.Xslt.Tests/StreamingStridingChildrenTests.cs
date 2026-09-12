using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:apply-templates with a striding select inside a streamable xsl:source-document processes
/// the matched element's CHILDREN. The dispatch materialises the whole element and leaves the
/// reader on its end tag, but the built-in shallow-copy still left the element open for children
/// the streaming loop would never deliver — so the copy came out empty, and the templates that
/// should have run for those children never ran (W3C strm/si-apply-templates-001).
/// </summary>
public sealed class StreamingStridingChildrenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-striding-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> RunAsync(string streamable)
    {
        var source = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(source,
            """<a><b DESC="outer"><c CODE="P"/><c CODE="H"/></b></a>""").ConfigureAwait(true);
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode streamable="{{streamable}}" on-no-match="shallow-copy"/>
              <xsl:template name="main">
                <xsl:source-document streamable="{{streamable}}" href="{{new Uri(source).AbsoluteUri}}">
                  <out><xsl:apply-templates select="a/b"/></out>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="c"><got code="{@CODE}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Fact]
    public async Task AStridingDispatch_ProcessesTheMatchedElementsChildren()
        => (await RunAsync("yes"))
            .Should().Be("""<out><b DESC="outer"><got code="P"/><got code="H"/></b></out>""",
                "the streamed run shallow-copied <b> and dropped its children entirely");

    [Fact]
    public async Task TheUnstreamedRunAgrees()
        => (await RunAsync("yes")).Should().Be(await RunAsync("no"));
}
