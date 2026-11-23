using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A collection the host declared with no documents is the empty sequence, for
/// <c>collection()</c> and <c>collection($uri)</c> and the <c>uri-collection</c> pair; one it did
/// not declare is FODC0002 (xquery#418). XSLT had a workaround for the default collection only.
/// </summary>
public class EmptyCollectionTests
{
    private static async Task<string> Run(string expression, Action<XsltTransformer>? declare = null)
    {
        var transformer = new XsltTransformer();
        declare?.Invoke(transformer);
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:try>
                  <xsl:value-of select="{expression}"/>
                  <xsl:catch><xsl:value-of select="'error', local-name-from-QName($err:code)" xmlns:err="http://www.w3.org/2005/xqt-errors"/></xsl:catch>
                </xsl:try>
              </xsl:template>
            </xsl:stylesheet>
            """);
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await transformer.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("count(collection())")]
    [InlineData("count(collection(()))")]
    [InlineData("count(uri-collection())")]
    public async Task A_declared_empty_default_collection_is_the_empty_sequence(string expression)
        => (await Run(expression, t => t.SetCollection("", []))).Should().Be("0");

    [Theory]
    [InlineData("count(collection('urn:empty'))")]
    [InlineData("count(uri-collection('urn:empty'))")]
    public async Task A_declared_empty_named_collection_is_the_empty_sequence(string expression)
        => (await Run(expression, t => t.SetCollection("urn:empty", []))).Should().Be("0");

    [Theory]
    [InlineData("collection('urn:none')")]
    [InlineData("uri-collection('urn:none')")]
    [InlineData("collection()")]
    public async Task A_collection_the_host_did_not_declare_is_FODC0002(string expression)
        => (await Run(expression, t => t.SetCollection("urn:empty", []))).Should().Be("error FODC0002");
}
