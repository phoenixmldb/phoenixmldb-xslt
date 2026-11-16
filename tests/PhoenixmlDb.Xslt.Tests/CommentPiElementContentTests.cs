using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An element in the content of xsl:comment or xsl:processing-instruction contributes its string
/// value (XSLT 3.0 §5.7.2). It also took the text written before the comment or PI, so
/// <c>+&lt;xsl:comment&gt;C &lt;v&gt;!!&lt;/v&gt;&lt;/xsl:comment&gt;</c> moved the + into the
/// comment (W3C bug-0305). Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class CommentPiElementContentTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template"><d>{{body}}</d></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("+<xsl:comment>C <v>!!</v> t</xsl:comment>+", "<d>+<!--C  !!  t-->+</d>")]
    [InlineData("+<xsl:comment><v>!!</v></xsl:comment>+", "<d>+<!--!!-->+</d>")]
    [InlineData("+<xsl:processing-instruction name='p'>a <v>!!</v> b</xsl:processing-instruction>+", "<d>+<?p a  !!  b?>+</d>")]
    [InlineData("<xsl:comment>C <v>!!</v> t</xsl:comment>", "<d><!--C  !!  t--></d>")]
    public async Task Text_before_the_comment_or_pi_stays_outside_it(string body, string expected)
        => (await Run(body)).Should().Be(expected);
}
