using AwesomeAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The xhtml method with html-version 5.0 normalizes prefixes (Serialization 3.1 §6): the XHTML (SVG,
/// MathML) namespace is the default namespace of the elements in it, and it is dropped from the
/// namespaces of an element outside it, the default namespace too, as Saxon-HE 10.9 writes it. A
/// prefixed element inside a normalized XHTML element gets xmlns="", and its XHTML children declare
/// the namespace again; a declaration that repeats the default in force is not written; under a
/// document element outside the XHTML namespace too. Below 5.0 nothing changes. The declarations are
/// compared here; the other byte differences from Saxon are BUGS.md #152.
/// </summary>
public sealed class XhtmlPrefixNormalizationTests
{
    private static async Task<string> RunAsync(string htmlVersion, string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xhtml" indent="no" omit-xml-declaration="yes" html-version="{htmlVersion}"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync("<x/>");
    }

    private const string ForeignInXhtml = """<html xmlns="http://www.w3.org/1999/xhtml"><body><p:x xmlns:p="urn:p"><img/><b xmlns="">t</b></p:x></body></html>""";
    private const string ForeignRoot = """<p:r xmlns:p="urn:p" xmlns="http://www.w3.org/1999/xhtml"><div/></p:r>""";

    [Fact]
    public async Task With_html_version_5_a_prefixed_element_outside_XHTML_has_no_XHTML_default()
    {
        var result = await RunAsync("5", ForeignInXhtml);

        // As Saxon-HE 10.9 writes it; b is in no namespace, the default in force: no xmlns="" again.
        result.Should().Contain("""<p:x xmlns="" xmlns:p="urn:p"><img xmlns="http://www.w3.org/1999/xhtml"/><b>t</b></p:x>""");
    }

    [Fact]
    public async Task With_html_version_5_a_document_element_outside_XHTML_has_no_XHTML_default()
        => (await RunAsync("5", ForeignRoot)).Should().StartWith("""<p:r xmlns:p="urn:p"><div xmlns="http://www.w3.org/1999/xhtml""");

    [Theory]
    [InlineData(ForeignInXhtml, """<p:x xmlns:p="urn:p"><img /><b xmlns="">t</b></p:x>""")]
    [InlineData(ForeignRoot, """<p:r xmlns:p="urn:p" xmlns="http://www.w3.org/1999/xhtml"><div/></p:r>""")]
    public async Task Below_html_version_5_nothing_changes(string body, string expected)
        => (await RunAsync("1.0", body)).Should().Contain(expected);
}
