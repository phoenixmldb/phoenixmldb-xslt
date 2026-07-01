using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streamability of xsl:iterate whose select produces GROUNDED atomic items
/// (e.g. .//*/name()). The per-item context item is a string, not a streaming
/// node, so a body that reads '.' (as a map-lookup key) or accumulates a grounded
/// map param must NOT be rejected as XTSE3430. Covers si-iterate-134/135.
/// A genuinely-consuming iterate body (child/descendant navigation of a streaming
/// per-item node) must still be rejected.
/// </summary>
public class StreamingIterateGroundedSelectTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-iter-grounded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("main");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Small streamed doc with a repeating element-name histogram:
    //   a x2, b x3, c x1  → histogram counts.
    private const string Doc = """
        <root>
          <a><b/><b/></a>
          <a><b/><c/></a>
        </root>
        """;

    // Canonical streaming histogram: crawling select (.//*) grounded per-item to a
    // string via name(); body accumulates a grounded map param; on-completion emits it.
    private const string HistogramSheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            xmlns:map="http://www.w3.org/2005/xpath-functions/map"
            exclude-result-prefixes="xs map">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="main">
            <xsl:source-document streamable="yes" href="d.xml">
              <elements>
                <xsl:variable name="histogram" as="map(xs:string, xs:integer)">
                  <xsl:iterate select=".//*/name()">
                    <xsl:param name="m" as="map(xs:string, xs:integer)" select="map{}"/>
                    <xsl:on-completion><xsl:sequence select="$m"/></xsl:on-completion>
                    <xsl:variable name="count" as="xs:integer?" select="($m(.), 0)[1]"/>
                    <xsl:next-iteration>
                      <xsl:with-param name="m" select="map:merge((map:entry(., $count+1), $m))"/>
                    </xsl:next-iteration>
                  </xsl:iterate>
                </xsl:variable>
                <xsl:for-each select="map:keys($histogram)">
                  <xsl:sort select="."/>
                  <e name="{.}" count="{$histogram(.)}"/>
                </xsl:for-each>
              </elements>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task GroundedCrawlingSelect_HistogramIterate_Streams()
    {
        var r = await Run(HistogramSheet, Doc, "d.xml");
        // .//* from the document node includes the root element plus all descendants:
        //   root x1, a x2, b x3, c x1. Sorted by name.
        r.Trim().Should().Be(
            """<elements><e name="a" count="2"/><e name="b" count="3"/><e name="c" count="1"/><e name="root" count="1"/></elements>""");
    }

    // NEGATIVE: a genuinely-consuming iterate body over a CRAWLING STREAMING-NODE
    // select (.//* — items are streaming nodes) that navigates children of the
    // per-item node must STILL raise XTSE3430.
    private const string ConsumingSheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="main">
            <xsl:source-document streamable="yes" href="d.xml">
              <out>
                <xsl:iterate select=".//*">
                  <xsl:param name="n" as="xs:integer" select="0"/>
                  <xsl:on-completion><total n="{$n}"/></xsl:on-completion>
                  <kids><xsl:value-of select="count(child::*)"/></kids>
                  <xsl:next-iteration>
                    <xsl:with-param name="n" select="$n + 1"/>
                  </xsl:next-iteration>
                </xsl:iterate>
              </out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task ConsumingCrawlingSelect_StreamingNodeBody_StillRejected()
    {
        var act = async () => await Run(ConsumingSheet, Doc, "d.xml");
        (await act.Should().ThrowAsync<XsltException>())
            .Which.Message.Should().Contain("XTSE3430");
    }
}
