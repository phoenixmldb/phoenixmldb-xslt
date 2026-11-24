using System.Xml.Linq;
using AwesomeAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Serialization 3.1 §5 (the XML output method, which the XHTML method follows): "CR, NEL and LINE
/// SEPARATOR characters in text nodes MUST be output respectively as "&amp;#xD;", "&amp;#x85;", and
/// "&amp;#x2028;" ... while CR, NL, TAB, NEL and LINE SEPARATOR characters in attribute nodes MUST be
/// output respectively as "&amp;#xD;", "&amp;#xA;", "&amp;#x9;", "&amp;#x85;", and "&amp;#x2028;" ...
/// In addition, the non-whitespace control characters #x1 through #x1F and #x7F through #x9F in text
/// nodes and attribute nodes MUST be output as character references." They survive a parse only so:
/// a parser turns a CR into a newline, and an XML 1.1 parser a NEL or a LINE SEPARATOR too. Text
/// wrote them as they were; indented, a CR in text became a newline, and NEL and LINE SEPARATOR in
/// attributes lost their references.
/// </summary>
public sealed class LineEndCharacterSerializationTests
{
    // A, CR, B, NEL, C, LINE SEPARATOR, D, PADDING CHARACTER (#x80, a C1 control), E
    private const string Value = "A\rB\u0085C\u2028D\u0080E";
    private const string Codepoints = "65,13,66,133,67,8232,68,128,69";
    private const string Refs = "A&#xD;B&#x85;C&#x2028;D&#x80;E";
    private const string Source = "<s a=\"" + Refs + "\">" + Refs + "</s>";

    public static TheoryData<string> Outputs => new()
    {
        "method='xml' indent='no'",
        "method='xml' indent='yes'",
        "method='xml' indent='yes' phx:indent-style='saxon-he-10'",
        "method='xhtml' indent='no'",
        "method='xhtml' indent='yes'",
        "method='xhtml' indent='yes' phx:indent-style='saxon-he-10'",
    };

    private static async Task<string> RunAsync(string output, string body, string mode = "")
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:phx="https://schemas.phoenixml.dev/2026/functions" exclude-result-prefixes="phx">
              <xsl:output {output}/>
              {mode}
              <xsl:variable name="v" select="codepoints-to-string(({Codepoints}))"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync(Source);
    }

    /// <summary>No raw CR, NEL, LINE SEPARATOR or C1 control in the result, and each value parses back as it was.</summary>
    private static void ShouldRoundTrip(string result, IEnumerable<string> elements, IEnumerable<string> attributes)
    {
        result.Should().NotContainAny(["\r", "\u0085", "\u2028", "\u0080"], "the characters are written as character references");
        var root = XDocument.Parse(result).Root!;
        foreach (var name in elements)
            root.Descendants().Single(e => e.Name.LocalName == name).Value.Should().Be(Value, name);
        foreach (var name in attributes)
            ((string?)root.Attribute(name)).Should().Be(Value, "@" + name);
    }

    [Theory]
    [MemberData(nameof(Outputs))]
    public async Task Text_and_attributes_from_every_source_keep_the_characters(string output)
    {
        var result = await RunAsync(output, """
            <xsl:variable name="tree"><x><xsl:value-of select="$v"/></x></xsl:variable>
            <r a1="{$v}" a3="A&#xD;B&#x85;C&#x2028;D&#x80;E">
              <xsl:attribute name="a2" select="$v"/>
              <xsl:copy-of select="/s/@a"/>
              <t1><xsl:value-of select="$v"/></t1>
              <t2>A&#xD;B&#x85;C&#x2028;D&#x80;E</t2>
              <t3><xsl:copy-of select="/s/text()"/></t3>
              <t4><xsl:copy-of select="$tree/x/text()"/></t4>
              <t5><xsl:sequence select="$v"/></t5>
              <xsl:copy-of select="/s"/>
            </r>
            """);

        ShouldRoundTrip(result, ["t1", "t2", "t3", "t4", "t5", "s"], ["a1", "a2", "a3", "a"]);
    }

    // The text method writes the characters themselves: there is no markup to escape them in.
    [Fact]
    public async Task The_text_method_writes_the_characters_themselves()
    {
        var result = await RunAsync("method='text'", """<xsl:value-of select="$v"/>|<xsl:value-of select="/s"/>|<xsl:variable name="t"><x><xsl:value-of select="$v"/></x></xsl:variable><xsl:value-of select="$t"/>""");

        result.Should().Be(Value + "|" + Value + "|" + Value);
    }

    [Theory]
    [InlineData("method='xml' indent='no'")]
    [InlineData("method='xml' indent='yes'")]
    public async Task A_streamed_source_keeps_the_characters(string output)
    {
        var result = await RunAsync(output,
            """<r a1="{$v}"><t1><xsl:value-of select="$v"/></t1><xsl:copy-of select="s"/></r>""",
            mode: """<xsl:mode streamable="yes"/>""");

        ShouldRoundTrip(result, ["t1", "s"], ["a1"]);
    }
}
