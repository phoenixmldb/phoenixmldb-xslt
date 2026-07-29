using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class DocumentNodeUsageTests
{
    private static async Task<string> Transform(string ss, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(input);
    }

    // §5.7.1: document nodes emitted directly into element content transmit their
    // children — adjacent documents concatenate with NO separator.
    [Fact]
    public async Task DocumentNodesInElementContent_Concatenate_NoSeparator()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:for-each select="r/v"><xsl:document><xsl:value-of select="."/></xsl:document></xsl:for-each></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await Transform(ss, "<r><v>-15.00</v><v>-5.00</v></r>"))
            .Should().Be("<out>-15.00-5.00</out>");
    }

    // §5.7.2: the SAME document nodes atomized via data() are simple content —
    // space-separated. Guards the si-document-003 regression the prior local fix caused.
    [Fact]
    public async Task DocumentNodesAtomizedByData_SpaceSeparated()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="docs" as="document-node()*">
                  <xsl:for-each select="r/v"><xsl:document><xsl:value-of select="."/></xsl:document></xsl:for-each>
                </xsl:variable>
                <out><xsl:value-of select="data($docs)"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await Transform(ss, "<r><v>-15.00</v><v>-5.00</v></r>"))
            .Should().Be("<out>-15.00 -5.00</out>");
    }
}
