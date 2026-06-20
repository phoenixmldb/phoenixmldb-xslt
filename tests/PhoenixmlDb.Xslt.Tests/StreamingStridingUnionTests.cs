using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// OP-bucket Phase 4 (Task A): a streamable select that unions two striding paths
/// and then takes a step (<c>(A|B)/step</c>) is crawling and must raise XTSE3430
/// at compile/load time. The <c>StridingUnionDetector</c> only saw a union at the
/// top of the select expression; when the union is the primary of a path/simple-map
/// (<c>(A|B)/PRICE</c> parses as a <c>PathExpression</c> with a
/// <c>BinaryExpression(Union)</c> initial-expression, or a <c>SimpleMapExpression</c>),
/// the existing both-operands-child-axis rule never fired. The canary guards against
/// over-rejection: a grounded union operand (<c>$var</c>) must still be accepted.
/// </summary>
public class StreamingStridingUnionTests
{
    private static async Task<string> Transform(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"striding-union-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Xml = "<BOOKLIST><ITEM><PRICE>1</PRICE></ITEM><MAGAZINE><PRICE>2</PRICE></MAGAZINE></BOOKLIST>";

    // (A|B)/step: union of two striding paths then a step → crawling → XTSE3430 at compile time.
    [Fact]
    public async Task StridingUnionThenStep_IsRejected_XTSE3430()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:apply-templates select="(/BOOKLIST/ITEM | /BOOKLIST/MAGAZINE)/PRICE"/></out>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="PRICE"><p><xsl:value-of select="."/></p></xsl:template>
            </xsl:stylesheet>
            """;
        // Compile-time streamability error: loading or transforming surfaces XTSE3430.
        Func<Task> act = async () => await Transform(sheet, Xml, "b.xml");
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3430");
    }

    // Canary: a grounded operand in the union must NOT be rejected (must run, not throw).
    [Fact]
    public async Task GroundedUnion_NotRejected()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:variable name="g" as="element()"><z/></xsl:variable>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:copy-of select="/BOOKLIST/ITEM[1] ! ($g union *)"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        Func<Task> act = async () => await Transform(sheet, Xml, "b.xml");
        await act.Should().NotThrowAsync();
    }
}
