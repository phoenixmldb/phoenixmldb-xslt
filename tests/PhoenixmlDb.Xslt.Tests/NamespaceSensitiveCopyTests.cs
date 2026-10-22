using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XTTE0950 (XSLT 3.0 §11.9.2): xsl:copy-of with validation="preserve" keeps type annotations, so a
/// value typed xs:QName needs its namespace bindings. Copying it under copy-namespaces="no", or
/// copying such an attribute without its parent element, is a type error. Both copies were made
/// without the error (W3C error-0950a/b).
/// </summary>
public sealed class NamespaceSensitiveCopyTests
{
    private static async Task<string> RunAsync(string copy)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:my="http://my.com/" version="3.0">
              <xsl:import-schema>
                <xs:schema>
                  <xs:element name="e"><xs:complexType><xs:attribute name="q" type="xs:QName"/></xs:complexType></xs:element>
                </xs:schema>
              </xsl:import-schema>
              <xsl:template name="xsl:initial-template">
                <xsl:variable name="x" as="element()"><e q="my:value" xsl:validation="strict"/></xsl:variable>
                <out>{copy}</out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("""<xsl:copy-of select="$x" copy-namespaces="no" validation="preserve"/>""")]
    [InlineData("""<xsl:copy-of select="$x/@q" validation="preserve"/>""")]
    public async Task A_namespace_sensitive_copy_without_its_bindings_is_XTTE0950(string copy)
    {
        var act = () => RunAsync(copy);
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTTE0950*");
    }

    [Theory]
    [InlineData("""<xsl:copy-of select="$x" validation="preserve"/>""")]
    [InlineData("""<xsl:copy-of select="$x" copy-namespaces="no" validation="strip"/>""")]
    [InlineData("""<xsl:copy-of select="$x/@q" validation="strip"/>""")]
    public async Task A_copy_that_keeps_the_bindings_or_drops_the_type_is_allowed(string copy)
    {
        (await RunAsync(copy)).Should().Contain("my:value");
    }
}
