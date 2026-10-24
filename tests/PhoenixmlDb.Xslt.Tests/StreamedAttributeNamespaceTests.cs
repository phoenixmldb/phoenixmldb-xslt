using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An attribute in a namespace keeps it when the streaming pass builds nodes for a copy. The
/// per-element attribute maps were keyed by local name alone, so xml:lang="de" in a streamed copy
/// came out as lang="de" in no namespace, and validating the copy against a schema that declares
/// xml:lang failed (W3C si-copy-of-111, -115). Each result must equal the unstreamed one.
/// </summary>
public sealed class StreamedAttributeNamespaceTests
{
    private const string InputXml = """
        <r xmlns:p="urn:p"><e xml:lang="de" p:x="1" x="2"/><e xml:lang="en"/></r>
        """;

    private static async Task<string> RunAsync(string select, bool streamable)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streamed-attr-ns-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "in.xml"), InputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync($$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
                  <xsl:output method="text"/>
                  <xsl:template name="xsl:initial-template">
                    <xsl:source-document streamable="{{(streamable ? "yes" : "no")}}" href="in.xml">
                      <xsl:variable name="copy" select="copy-of(/r/e)"/>
                      <xsl:value-of select="{{select}}"/>
                    </xsl:source-document>
                  </xsl:template>
                </xsl:stylesheet>
                """, new Uri(dir + "/"));
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("$copy/@xml:lang", "de en")]
    [InlineData("$copy[1]/@p:x", "1")]
    [InlineData("$copy[1]/@x", "2")]
    [InlineData("count($copy[1]/@*)", "3")]
    [InlineData("$copy[1]/@*[local-name() = 'lang']/namespace-uri()", "http://www.w3.org/XML/1998/namespace")]
    public async Task A_streamed_copy_keeps_attribute_namespaces(string select, string expected)
    {
        (await RunAsync(select, streamable: false)).Should().Be(expected);
        (await RunAsync(select, streamable: true)).Should().Be(expected);
    }
}
