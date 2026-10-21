using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// document-node(element(x:N)) as the node test of a step: the element test inside is matched
/// against the document element, with its prefix resolved against the stylesheet's namespaces as
/// for any element() test. The prefix was left unresolved, so /self::document-node(element(x:report))
/// matched nothing while `. instance of document-node(element(x:report))` was true (xquery#292).
/// </summary>
public sealed class DocumentNodeElementStepTests
{
    [Fact]
    public async Task The_self_axis_matches_the_document_by_its_document_element()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" xmlns:x="urn:x" xmlns:y="urn:y">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:value-of select="count(/self::document-node(element(x:report))), count(/self::document-node(element(x:other))),
                  count(/self::document-node(element(y:report))), count(/self::document-node(element(*))),
                  . instance of document-node(element(x:report))"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("<p:report xmlns:p='urn:x'/>")).Should().Be("1 0 0 1 true");
    }

    [Fact]
    public async Task An_undeclared_prefix_in_the_inner_test_is_XPST0081()
    {
        var t = new XsltTransformer();
        var act = () => t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/"><xsl:value-of select="count(/self::document-node(element(q:report)))"/></xsl:template>
            </xsl:stylesheet>
            """);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPST0081");
    }
}
