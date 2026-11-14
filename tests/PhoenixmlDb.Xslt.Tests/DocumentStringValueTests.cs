using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The string value of a document node that xsl:document builds is that of its text
/// descendants: a comment or processing-instruction child adds nothing. Its content was added,
/// so xsl:comment over an xsl:document wrote the comment's text too (W3C xsl-document-0601).
/// </summary>
public sealed class DocumentStringValueTests
{
    [Fact]
    public async Task A_comment_or_processing_instruction_child_adds_nothing()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">
                <out>
                  <xsl:attribute name="v">
                    <xsl:document><a>A</a><xsl:comment>C</xsl:comment>T</xsl:document>
                  </xsl:attribute>
                  <xsl:comment>
                    <xsl:document><a>A</a><xsl:comment>C</xsl:comment><xsl:processing-instruction name="p">P</xsl:processing-instruction>T</xsl:document>
                  </xsl:comment>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");

        (await t.TransformAsync((string?)null)).Should().Contain("<out v=\"AT\"><!--AT--></out>");
    }
}
