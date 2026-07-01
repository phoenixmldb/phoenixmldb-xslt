using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// #143 (si-iterate-013): a streamable template match="/*" whose body shallow-copies
/// the matched root (xsl:copy) around a streaming xsl:iterate select="*" whose body
/// deep-copies each child (xsl:copy-of select=".") and breaks at a positional count.
/// Element structure — both the xsl:copy root wrapper and each per-child copy-of —
/// must be preserved, not collapsed to descendant text only.
/// </summary>
public class StreamingIterateCopyWrapperTests
{
    private static async Task<string> Run(string stylesheet, string inputXml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(inputXml);
    }

    // match="/*" -> xsl:copy (shallow copy of root) wrapping xsl:iterate select="*"
    // whose body does xsl:copy-of select="." per child. No break: all children copied.
    private const string CopyIterateSheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            exclude-result-prefixes="xs">
          <xsl:mode streamable="yes"/>
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template match="/*">
            <xsl:copy>
              <xsl:iterate select="*">
                <xsl:copy-of select="."/>
              </xsl:iterate>
            </xsl:copy>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task StreamingCopyWrapsIterateCopyOf_PreservesElementStructure()
    {
        var r = await Run(CopyIterateSheet, "<root><a>x</a><b>y</b></root>");
        r.Trim().Should().Be("<root><a>x</a><b>y</b></root>");
    }

    // Break variant: copy the first 2 of 4 children, break at position 2.
    private const string CopyIterateBreakSheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            exclude-result-prefixes="xs">
          <xsl:param name="elements-to-copy" as="xs:integer" select="2"/>
          <xsl:mode streamable="yes"/>
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template match="/*">
            <xsl:copy>
              <xsl:iterate select="*">
                <xsl:copy-of select="."/>
                <xsl:if test="position() eq $elements-to-copy">
                  <xsl:break/>
                </xsl:if>
              </xsl:iterate>
            </xsl:copy>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task StreamingCopyWrapsIterateCopyOfWithBreak_PreservesStructureAndStops()
    {
        var r = await Run(CopyIterateBreakSheet, "<root><a>1</a><b>2</b><c>3</c><d>4</d></root>");
        r.Trim().Should().Be("<root><a>1</a><b>2</b></root>");
    }

}
