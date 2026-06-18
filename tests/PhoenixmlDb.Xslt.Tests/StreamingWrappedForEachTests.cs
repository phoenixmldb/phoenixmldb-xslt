using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Phase 1 of #143 "uniform consuming-expression streaming": a streamable
/// <c>xsl:for-each</c> whose downward/striding select is lexically WRAPPED in
/// constructed output (an LRE) must keep the surrounding construction in the
/// output, run linearly, and hand off to the live reader at its lexical
/// position. Before the fix the entry point chose subscription-dispatch-only,
/// cleared the live reader, drained per-event, and dropped the wrapper.
/// </summary>
public class StreamingWrappedForEachTests
{
    private static async Task<string> TransformWithFile(
        string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-wrapped-foreach-{Guid.NewGuid():N}");
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
    /// W1 shape: an LRE (<c>&lt;out&gt;</c>) lexically wrapping a streamable for-each
    /// INSIDE the source-document body. The wrapper must survive and position()
    /// numbering must be correct.
    /// </summary>
    [Fact]
    public async Task WrappedForEach_LreWrapper_KeepsWrapperAndNumbersPositions()
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
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="recs.xml">
                  <out>
                    <xsl:for-each select="records/record">
                      <r nr="{position()}"><xsl:copy-of select="@*"/></r>
                    </xsl:for-each>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "recs.xml");
        result.Trim().Should().Be(
            "<out><r nr=\"1\" id=\"a\"/><r nr=\"2\" id=\"b\"/><r nr=\"3\" id=\"c\"/></out>",
            because: "the LRE wrapper must survive and position() must number the matched records 1..3");
    }

    /// <summary>si-for-each-002 shape: position() numbering, attribute predicate.</summary>
    [Fact]
    public async Task SiForEach002_Shape_PositionNumberingWithPredicate()
    {
        var inputXml = """
            <account nr="76543210">
              <account-number>01234567</account-number>
              <transaction value="13.24" date="2006-02-13"/>
              <transaction value="-15.00" date="2006-02-15"/>
              <transaction value="-5.00" date="2006-02-20"/>
              <transaction value="-2.33" date="2006-02-23"/>
              <transaction value="-248.05" date="2006-02-24"/>
              <transaction value="12.00" date="2006-02-25"/>
            </account>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="txns.xml">
                    <xsl:for-each select="account/transaction[@value &lt; 0]">
                      <transaction nr="{position()}">
                        <xsl:copy-of select="@*"/>
                      </transaction>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "txns.xml");
        result.Trim().Should().Be(
            "<out><transaction nr=\"1\" value=\"-15.00\" date=\"2006-02-15\"/>" +
            "<transaction nr=\"2\" value=\"-5.00\" date=\"2006-02-20\"/>" +
            "<transaction nr=\"3\" value=\"-2.33\" date=\"2006-02-23\"/>" +
            "<transaction nr=\"4\" value=\"-248.05\" date=\"2006-02-24\"/></out>");
    }

    /// <summary>si-for-each-008 shape: parent axis inside the body.</summary>
    [Fact]
    public async Task SiForEach008_Shape_ParentAxisInBody()
    {
        var inputXml = """
            <account nr="76543210">
              <transaction value="13.24"/>
              <transaction value="8.12"/>
              <transaction value="-15.00"/>
              <transaction value="6.00"/>
              <transaction value="0.50"/>
            </account>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="txns.xml">
                    <xsl:for-each select="account/transaction[position() lt 5]">
                      <xsl:sequence select="name(..)"/>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "txns.xml");
        result.Trim().Should().Be("<out>account account account account</out>");
    }

    /// <summary>si-for-each-004 shape: subsequence(data(path/@attr), start, len) — atomized,
    /// sliced window, body position() numbers the windowed items.</summary>
    [Fact]
    public async Task SiForEach004_Shape_SubsequenceOfAtomizedAttributes()
    {
        var inputXml = """
            <account>
              <transaction value="13.24"/>
              <transaction value="8.12"/>
              <transaction value="-15.00"/>
              <transaction value="6.00"/>
              <transaction value="0.50"/>
              <transaction value="2.33"/>
              <transaction value="4.44"/>
              <transaction value="8.99"/>
            </account>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="txns.xml">
                    <xsl:for-each select="subsequence(data(account/transaction/@value), 5, 3)">
                      <transaction nr="{position()}"><xsl:value-of select="."/></transaction>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(stylesheet, inputXml, "txns.xml");
        result.Trim().Should().Be("<out><transaction nr=\"1\">0.50</transaction><transaction nr=\"2\">2.33</transaction><transaction nr=\"3\">4.44</transaction></out>");
    }

    /// <summary>si-for-each-009 shape: subsequence(path, 1, 4) element slice; body uses
    /// ancestor::*[1] (synthesized ancestor chain on the snapshot).</summary>
    [Fact]
    public async Task SiForEach009_Shape_SubsequenceWithAncestorAxis()
    {
        var inputXml = """
            <account nr="76543210">
              <transaction value="13.24"/>
              <transaction value="8.12"/>
              <transaction value="-15.00"/>
              <transaction value="6.00"/>
            </account>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="txns.xml">
                    <xsl:for-each select="subsequence(account/transaction, 1, 4)">
                      <xsl:sequence select="name(ancestor::*[1])"/>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(stylesheet, inputXml, "txns.xml");
        result.Trim().Should().Be("<out>account account account account</out>");
    }
}
