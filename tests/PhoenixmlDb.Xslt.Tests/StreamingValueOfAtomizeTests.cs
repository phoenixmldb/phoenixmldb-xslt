using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Phase 3 (bucket B3) — atomization / separator under streaming. When a watched
/// streamed sequence (mixed element + atomic, or elements) feeds
/// <c>xsl:value-of</c> / <c>xsl:attribute</c> / <c>data()</c>, the executor must
/// ATOMIZE each item (element → its string value) and join with the correct
/// separator (default single space for value-of; an explicit <c>separator</c>
/// attribute where present; empty string for attribute-content-without-separator),
/// instead of serializing the raw element markup.
///
/// Before the fix the SM-ctx / for-expression streaming handoff on
/// <c>xsl:value-of</c> emitted the raw serialized elements
/// (<c>&lt;a&gt;A&lt;/a&gt;&lt;b&gt;B&lt;/b&gt;</c>) rather than the atomized,
/// space-joined string value (<c>A B</c>).
/// </summary>
public class StreamingValueOfAtomizeTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-valueof-atomize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, inputFileName);
        await File.WriteAllTextAsync(inputPath, inputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // Source: a set of DIMENSIONS elements, each a space-separated numeric run.
    private const string DimsXml = """
        <BOOKLIST><BOOKS>
          <ITEM><DIMENSIONS>8.3 5.7 1.1</DIMENSIONS></ITEM>
          <ITEM><DIMENSIONS>1.0 5.2 7.8</DIMENSIONS></ITEM>
        </BOOKS></BOOKLIST>
        """;

    private static string ValueOfSheet(string select) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:variable name="insertion" as="element()*"><a>A</a><b>B</b></xsl:variable>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="b.xml">
              <out><xsl:value-of select="{{select}}"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task ValueOf_MixedElementAndAtomic_AtomizesAndSpaceJoins()
    {
        // LEFT ! (tokenize(., ' '), $insertion) — per DIMENSIONS: three atomic
        // strings then two grounded elements. value-of must atomize the elements
        // to 'A' and 'B' and space-join everything.
        var result = await TransformWithFile(
            ValueOfSheet("/BOOKLIST/BOOKS/ITEM/DIMENSIONS ! (tokenize(., ' '), $insertion)"),
            DimsXml, "b.xml");
        result.Trim().Should().Be("<out>8.3 5.7 1.1 A B 1.0 5.2 7.8 A B</out>");
    }

    [Fact]
    public async Task ValueOf_GroundedThenStreamed_AtomizesAndSpaceJoins()
    {
        var result = await TransformWithFile(
            ValueOfSheet("/BOOKLIST/BOOKS/ITEM/DIMENSIONS ! ($insertion, tokenize(., ' '))"),
            DimsXml, "b.xml");
        result.Trim().Should().Be("<out>A B 8.3 5.7 1.1 A B 1.0 5.2 7.8</out>");
    }

    [Fact]
    public async Task ValueOf_NoRawElementMarkupLeaks()
    {
        var result = await TransformWithFile(
            ValueOfSheet("/BOOKLIST/BOOKS/ITEM/DIMENSIONS ! (tokenize(., ' '), $insertion)"),
            DimsXml, "b.xml");
        // NEGATIVE: raw element markup must never appear in a value-of result.
        result.Should().NotContain("<a>");
        result.Should().NotContain("<b>");
    }
}
