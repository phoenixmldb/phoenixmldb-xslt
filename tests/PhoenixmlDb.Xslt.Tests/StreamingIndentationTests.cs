using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// indent="yes" for the xml method is done in one streaming pass, with the indentation an
/// indenting XmlWriter gives (#400). suppress-indentation names elements by expanded QName
/// (Serialization 3.1 §7.1); it matched them by local name only, so suppress-indentation="p"
/// also suppressed an x:p in another namespace.
/// </summary>
public sealed class StreamingIndentationTests
{
    private static async Task<string> RunAsync(string output, string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:x="urn:x" version="3.0" exclude-result-prefixes="x">
              <xsl:output method="xml" indent="yes" omit-xml-declaration="yes" {output}/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync("<x/>");
    }

    [Fact]
    public async Task Elements_are_indented_and_mixed_content_is_not()
        => (await RunAsync("", "<r><a><b/></a><m>text<c><d/></c></m></r>"))
            .Should().Be("<r>\n  <a>\n    <b />\n  </a>\n  <m>text<c><d /></c></m>\n</r>");

    [Fact]
    public async Task Suppress_indentation_matches_the_expanded_name()
        => (await RunAsync("suppress-indentation='p'", "<r><p><a/></p><x:p xmlns:x='urn:x'><a/></x:p></r>"))
            .Should().Be("<r>\n  <p><a /></p>\n  <x:p xmlns:x=\"urn:x\">\n    <a />\n  </x:p>\n</r>");

    [Fact]
    public async Task Suppress_indentation_names_a_namespaced_element_by_its_namespace()
        => (await RunAsync("suppress-indentation='x:p'", "<r><p><a/></p><x:p xmlns:x='urn:x'><a/></x:p></r>"))
            .Should().Be("<r>\n  <p>\n    <a />\n  </p>\n  <x:p xmlns:x=\"urn:x\"><a /></x:p>\n</r>");
}
