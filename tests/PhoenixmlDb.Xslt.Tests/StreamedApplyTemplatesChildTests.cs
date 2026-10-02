using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a streamable mode, children reached by apply-templates inside a matched template have their
/// streamed parent: a parent-step pattern (match="doc/*") matches them, and an unmatched child gets
/// the built-in text-only rule (its text), not a deep copy.
/// </summary>
public sealed class StreamedApplyTemplatesChildTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:mode streamable="true"/>
          <xsl:template match="doc"><out><xsl:apply-templates select="*"/></out></xsl:template>
          <xsl:template match="doc/a"><v d="{count(ancestor::node())}"/></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task Streamed_children_have_their_parent_and_unmatched_ones_yield_text()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        (await t.TransformAsync("<doc><a>1</a><b><c>2</c><d>3</d></b></doc>"))
            .Should().Be("<out><v d=\"2\"/>23</out>");
    }
}
