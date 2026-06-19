using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Phase 2a of #143 "uniform consuming-expression streaming": a consuming
/// <c>xsl:apply-templates</c> lexically WRAPPED in constructed output (W2), and
/// a consuming <c>xsl:for-each</c>/<c>xsl:apply-templates</c> inside an
/// <c>xsl:if</c>/<c>xsl:choose</c> conditional wrapper (COND), under a streamable
/// <c>xsl:source-document</c> body. The surrounding construction must survive, the
/// conditional must be honoured, and the consuming child must drive the live reader
/// at its lexical position within linear body execution.
/// </summary>
public class StreamingWrappedApplyTemplatesTests
{
    private static async Task<string> TransformWithFile(
        string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-wrapped-at-{Guid.NewGuid():N}");
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

    /// <summary>
    /// W2 shape (si-choose-001/002 analog): an LRE (<c>&lt;out&gt;</c>) lexically
    /// wrapping a consuming default-select <c>xsl:apply-templates</c> that carries an
    /// EXPLICIT mode whose <c>on-no-match</c> is <c>deep-copy</c>. The wrapper must
    /// survive and the apply-templates must drive the live reader under THAT mode
    /// (deep-copying the streamed children), not the enclosing template's mode.
    /// Input has no inter-element whitespace so the mode-propagation win is isolated
    /// from streaming strip-space (a separable concern).
    /// </summary>
    [Fact]
    public async Task WrappedApplyTemplates_DeepCopyMode_StreamsUnderApplyTemplatesMode()
    {
        var inputXml = "<records><record id=\"a\"><v>1</v></record><record id=\"b\"><v>2</v></record></records>";
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:mode name="t" streamable="yes" on-no-match="deep-copy"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="recs.xml">
                  <out>
                    <xsl:apply-templates mode="t"/>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "recs.xml");
        result.Trim().Should().Be(
            "<out><records><record id=\"a\"><v>1</v></record><record id=\"b\"><v>2</v></record></records></out>",
            because: "the LRE wrapper must survive and apply-templates mode='t' must deep-copy the streamed input");
    }

    /// <summary>
    /// COND shape: a consuming <c>xsl:for-each</c> inside an <c>xsl:choose</c>
    /// (motionless test) under a streamable body. Phase-1 inline-driven for-each
    /// inside the conditional must stream correctly.
    /// </summary>
    [Fact]
    public async Task ForEachInsideChoose_StreamsMatchedRecords()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <records>
              <record id="a"/>
              <record id="b"/>
              <record id="c"/>
            </records>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="recs.xml">
                    <xsl:choose>
                      <xsl:when test="current-date() lt current-date()" xmlns:xs="http://www.w3.org/2001/XMLSchema">
                        <none/>
                      </xsl:when>
                      <xsl:otherwise>
                        <xsl:for-each select="records/record">
                          <r nr="{position()}"><xsl:value-of select="@id"/></r>
                        </xsl:for-each>
                      </xsl:otherwise>
                    </xsl:choose>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "recs.xml");
        result.Trim().Should().Be(
            "<out><r nr=\"1\">a</r><r nr=\"2\">b</r><r nr=\"3\">c</r></out>",
            because: "the otherwise branch's streamed for-each must run inside linear body execution");
    }

    /// <summary>
    /// COND shape: a consuming <c>xsl:for-each</c> inside an <c>xsl:if</c>
    /// (motionless test true) under a streamable body.
    /// </summary>
    [Fact]
    public async Task ForEachInsideIf_StreamsMatchedRecords()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <records>
              <record id="a"/>
              <record id="b"/>
            </records>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="recs.xml">
                    <in/>
                    <xsl:if test="current-date() le current-date()">
                      <xsl:for-each select="records/record">
                        <r><xsl:value-of select="@id"/></r>
                      </xsl:for-each>
                    </xsl:if>
                    <in/>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "recs.xml");
        result.Trim().Should().Be(
            "<out><in/><r>a</r><r>b</r><in/></out>",
            because: "the if-true branch's streamed for-each must run inline and the surrounding constants must survive");
    }

    /// <summary>
    /// COND + W2 shape (si-choose-001/002 analog): a consuming default-select
    /// <c>xsl:apply-templates mode="t"</c> (on-no-match deep-copy) inside an
    /// <c>xsl:choose</c> under a streamable body. The chosen branch's apply-templates
    /// must drive the live reader under mode "t". Input has no inter-element whitespace
    /// to isolate from streaming strip-space.
    /// </summary>
    [Fact]
    public async Task ApplyTemplatesInsideChoose_DeepCopyMode_StreamsUnderApplyTemplatesMode()
    {
        var inputXml = "<records><record id=\"a\"><v>1</v></record><record id=\"b\"><v>2</v></record></records>";
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:mode name="t" streamable="yes" on-no-match="deep-copy"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="recs.xml">
                    <xsl:choose>
                      <xsl:when test="current-date() lt current-date()" xmlns:xs="http://www.w3.org/2001/XMLSchema">
                        <none/>
                      </xsl:when>
                      <xsl:otherwise>
                        <xsl:apply-templates mode="t"/>
                      </xsl:otherwise>
                    </xsl:choose>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "recs.xml");
        result.Trim().Should().Be(
            "<out><records><record id=\"a\"><v>1</v></record><record id=\"b\"><v>2</v></record></records></out>",
            because: "the otherwise branch's streamed apply-templates mode='t' must deep-copy the streamed input");
    }
}
