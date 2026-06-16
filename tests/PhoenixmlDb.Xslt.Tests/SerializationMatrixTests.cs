using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// GOLDEN MATRIX: asserts serialized output across (output method x delivery path x indent on/off).
/// This is the safety net for the upcoming serialization-pipeline reroute, which consolidates every
/// "delivery path" (the distinct ways a transform result becomes final text) through one
/// post-processing pipeline. Recurring indentation bugs come from each path applying a different
/// subset of post-processing; this matrix makes future gaps build-time failures.
///
/// Task 1 (this file): the test harness plus the reference (node-source) cells that PASS today.
/// The node-source path is the already-correct reference path. The ViaXdmSequence /
/// ViaResultDocument helpers are included (and proven to compile via one smoke cell) so later
/// tasks can populate their assertion cells as those paths are brought into line.
/// </summary>
public class SerializationMatrixTests
{
    // ---------------------------------------------------------------------
    // Delivery-path helpers (mirroring MartinJsonSequenceRoundTripTests patterns)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Node-source delivery path: load <paramref name="stylesheet"/> and transform a trivial
    /// document node (<c>&lt;in/&gt;</c>) to a string. This is the reference path.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> NodeSource(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    /// <summary>
    /// Parse-json stylesheet used to turn a JSON string into an XdmSequence for the
    /// XdmSequence delivery path.
    /// </summary>
    private const string ParseStylesheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:param name="j" as="xs:string"/>
          <xsl:template match="/"><xsl:sequence select="parse-json($j)"/></xsl:template>
        </xsl:stylesheet>
        """;

    /// <summary>
    /// XdmSequence delivery path: parse <paramref name="jsonInput"/> into an XdmSequence via a
    /// parse-json stylesheet (TransformToSequenceAsync), then feed that sequence to
    /// <paramref name="stylesheet"/> via TransformAsync(XdmSequence).
    /// </summary>
    private static async System.Threading.Tasks.Task<string> ViaXdmSequence(string jsonInput, string stylesheet)
    {
        var parser = new XsltTransformer();
        await parser.LoadStylesheetAsync(ParseStylesheet);
        parser.SetParameter("j", jsonInput);
        var seq = await parser.TransformToSequenceAsync(null);

        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(seq);
    }

    /// <summary>
    /// Result-document delivery path: a node-source transform whose stylesheet body wraps output in
    /// <c>&lt;xsl:result-document href=""&gt;...&lt;/xsl:result-document&gt;</c>. The caller supplies
    /// the inner body (the content the result-document wraps). The result-document is embedded in the
    /// stylesheet, so this is just a node-source transform of <c>&lt;in/&gt;</c>.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> ViaResultDocument(string outputDecl, string innerBody)
    {
        var stylesheet = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" expand-text="yes">
              {{outputDecl}}
              <xsl:template match="/">
                <xsl:result-document href="">{{innerBody}}</xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, baseUri: new System.Uri("file:///tmp/serialization-matrix/"));
        return await t.TransformAsync("<in/>");
    }

    // ---------------------------------------------------------------------
    // Reference cells: NODE-SOURCE path (correct today)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NodeSource_Xml_IndentYes()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="yes"/>
              <xsl:template match="/"><a><b>1</b><b>2</b></a></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await NodeSource(stylesheet);

        result.Should().Contain("<b>1</b>");
        result.Should().Contain("<b>2</b>");
        // Prove multi-line indentation: <a> is followed by a newline and indentation before <b>.
        result.Should().MatchRegex(@"<a>\s*\n\s+<b>");
    }

    [Fact]
    public async Task NodeSource_Xml_IndentNo()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/"><a><b>1</b><b>2</b></a></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await NodeSource(stylesheet);

        result.Should().Contain("<a><b>1</b><b>2</b></a>");
        // Prove NOT indented: no newline + indentation before <b>.
        result.Should().NotMatchRegex(@"\n\s+<b>");
    }

    [Fact]
    public async Task NodeSource_Html_IndentDefault()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html"/>
              <xsl:template match="/"><html><body><p>x</p></body></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await NodeSource(stylesheet);

        result.Should().Contain("<html>");
        result.Should().Contain("<p>");
        result.Should().Contain("x");
        // HTML method indents by default: nested elements appear on their own indented lines.
        result.Should().MatchRegex(@"<body>\s*\n\s+<p>");
    }

    [Fact]
    public async Task NodeSource_Text()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">Hello <wrap>world</wrap></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await NodeSource(stylesheet);

        // text method strips markup; only the text value content survives.
        result.Should().Contain("Hello");
        result.Should().Contain("world");
        result.Should().NotContain("<wrap>");
        result.Should().NotContain("</wrap>");
    }

    [Fact]
    public async Task NodeSource_Json_IndentYes()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="json" indent="yes"/>
              <xsl:template match="/"><xsl:sequence select="map{'a':1,'b':2}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await NodeSource(stylesheet);

        result.Should().Contain("\"a\"");
        result.Should().Contain("\"b\"");
        // indent="yes" JSON is multi-line.
        result.Should().Contain("\n");
    }

    // ---------------------------------------------------------------------
    // Smoke cell proving the ViaXdmSequence helper compiles and works (1.4.8+)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ViaXdmSequence_Json_RoundTrips()
    {
        const string jsonIdentity = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="json" indent="yes"/>
              <xsl:template match="."><xsl:sequence select="."/></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await ViaXdmSequence("""[{"x":1}]""", jsonIdentity);

        result.Should().Contain("\"x\"");
    }
}
