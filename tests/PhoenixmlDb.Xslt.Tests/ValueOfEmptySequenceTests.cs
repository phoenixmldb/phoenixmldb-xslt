using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests that xsl:value-of select="()" produces nothing per XSLT 3.0 §11.4.2 + §5.7.2.
/// </summary>
public class ValueOfEmptySequenceTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task ValueOf_EmptySequence_ProducesNothing()
    {
        // Per XSLT 3.0 §11.4.2 + §5.7.2: xsl:value-of select="()" produces a
        // zero-length text node which is then deleted during complex content construction.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:value-of select="()"/>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Be("<out/>");
    }

    [Fact]
    public async Task ValueOf_EmptySequence_DoesNotAffectSiblings()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <before/>
                        <xsl:value-of select="()"/>
                        <after/>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Be("<out><before/><after/></out>");
    }

    [Fact]
    public async Task ValueOf_EmptyString_ProducesNothing()
    {
        // xsl:value-of select="''" also produces a zero-length text node → deleted
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:value-of select="''"/>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Be("<out/>");
    }
}
