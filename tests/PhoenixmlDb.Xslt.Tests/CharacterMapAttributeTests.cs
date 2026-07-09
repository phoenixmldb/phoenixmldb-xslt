using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Focused coverage for character-map application to attribute values (XSLT 3.0 §20):
/// maps apply to attribute VALUES as well as text content, EXCEPT URI-valued attributes
/// in HTML/XHTML output where escape-uri-attributes escaping applies instead. Mirrors the
/// W3C character-map-001/009 conformance cases.
/// </summary>
public class CharacterMapAttributeTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<in/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task CharacterMap_AppliesToAttributeValues_InXmlOutput()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:character-map name="m"><xsl:output-character character="x" string="[X]"/></xsl:character-map>
              <xsl:output method="xml" use-character-maps="m"/>
              <xsl:template match="/"><a value="axb">hello x</a></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("value=\"a[X]b\"");
        result.Should().Contain(">hello [X]<");
    }

    [Fact]
    public async Task CharacterMap_NotAppliedToUriAttributes_InHtmlOutput()
    {
        // 'z' is mapped in text but must NOT be mapped inside a URI-valued href in HTML output.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:character-map name="m"><xsl:output-character character="z" string="[Z]"/></xsl:character-map>
              <xsl:output method="html" indent="no" use-character-maps="m"/>
              <xsl:template match="/"><a href="z-link.html">A z link</a></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("href=\"z-link.html\"");
        result.Should().Contain("A [Z] link");
    }
}
