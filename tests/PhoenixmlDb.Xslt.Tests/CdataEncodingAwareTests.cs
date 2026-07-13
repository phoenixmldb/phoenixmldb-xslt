using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A character inside a <c>cdata-section-elements</c> CDATA section that the target output
/// encoding cannot represent must be emitted as a numeric character reference OUTSIDE the CDATA
/// section — the section splits around it (<c>&lt;![CDATA[foo ]]&gt;&amp;#170;&lt;![CDATA[ bar]]&gt;</c>).
/// Mirrors PhoenixmlDb.XQuery's encoding-aware CDATA splitting. Covers W3C XSLT decl/output
/// output-0115b/c/d/e.
/// </summary>
public class CdataEncodingAwareTests
{
    private static async System.Threading.Tasks.Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    // output-0115b: non-ASCII (U+00AA) in a CDATA-section element under US-ASCII → split + NCR.
    [Fact]
    public async System.Threading.Tasks.Task NonAsciiInCdataUnderUsAscii_SplitsAndEmitsNcr()
    {
        var xsl = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="example" indent="no" encoding="US-ASCII"/>
              <t:template match="/"><html><out><example>foo &#xaa; bar</example></out></html></t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);
        result.Should().Contain("<![CDATA[foo ]]>");
        result.Should().Contain("<![CDATA[ bar]]>");
        result.Should().MatchRegex(">&#(xaa|xAA|170);<");
        result.Should().NotContain("foo ª bar");
    }

    // output-0115c: character maps must NOT apply inside CDATA — the char is still NCR'd, not mapped.
    [Fact]
    public async System.Threading.Tasks.Task CharacterMapDoesNotApplyInsideCdata_StillNcr()
    {
        var xsl = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="example" indent="no" encoding="US-ASCII"
                        use-character-maps="format1"/>
              <t:character-map name="format1"><t:output-character character="&#170;" string="A"/></t:character-map>
              <t:template match="/"><html><out><example>foo &#xaa; bar</example></out></html></t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);
        result.Should().Contain("<![CDATA[foo ]]>");
        result.Should().Contain("<![CDATA[ bar]]>");
        result.Should().MatchRegex(">&#(xaa|xAA|170);<");
        result.Should().NotContain("foo A bar");
    }

    // output-0115d: NFD normalization decomposes ç → c + U+0327; c stays in CDATA, combining char NCR'd.
    [Fact]
    public async System.Threading.Tasks.Task CedillaNfd_KeepsBaseInCdata_SplitsCombiningMark()
    {
        var xsl = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="example" indent="no" encoding="US-ASCII"
                        normalization-form="NFD"/>
              <t:template match="/"><html><out><example>foo ç bar</example></out></html></t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);
        result.Should().Contain("<![CDATA[foo c]]>");
        result.Should().Contain("<![CDATA[ bar]]>");
        result.Should().MatchRegex(">&#(807|x327);<");
    }

    // output-0115e: NFC keeps ç as U+00E7 → base CDATA ends before it, NCR emitted.
    [Fact]
    public async System.Threading.Tasks.Task CedillaNfc_SplitsAndEmitsNcr()
    {
        var xsl = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="example" indent="no" encoding="US-ASCII"
                        normalization-form="NFC"/>
              <t:template match="/"><html><out><example>foo ç bar</example></out></html></t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);
        result.Should().Contain("<![CDATA[foo ]]>");
        result.Should().Contain("<![CDATA[ bar]]>");
        result.Should().MatchRegex(">&#(xe7|xE7|231);<");
    }

    // Guard: a representable character stays INSIDE the CDATA unchanged (no spurious splitting).
    [Fact]
    public async System.Threading.Tasks.Task RepresentableChar_StaysInsideCdata_NoSplit()
    {
        var xsl = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="example" indent="no" encoding="US-ASCII"/>
              <t:template match="/"><html><out><example>foo bar baz</example></out></html></t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);
        result.Should().Contain("<![CDATA[foo bar baz]]>");
    }

    // Guard: under UTF-8 the char is representable → stays raw in CDATA, no splitting/NCR.
    [Fact]
    public async System.Threading.Tasks.Task NonAsciiUnderUtf8_StaysRawInsideCdata()
    {
        var xsl = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="example" indent="no" encoding="UTF-8"/>
              <t:template match="/"><html><out><example>foo &#xaa; bar</example></out></html></t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);
        result.Should().Contain("<![CDATA[foo ª bar]]>");
        result.Should().NotContain("&#170;");
    }
}
