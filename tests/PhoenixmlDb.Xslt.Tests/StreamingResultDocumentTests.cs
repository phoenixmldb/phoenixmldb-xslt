using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streaming <c>xsl:result-document</c> capture (W3C strm/si-result-document cluster).
/// Each case drives a streamable source (via xsl:source-document or a streamable mode)
/// whose secondary output is redirected by xsl:result-document, and asserts the captured
/// <see cref="XsltTransformer.SecondaryResultDocuments"/> entry equals the expected
/// document — the same contract the conformance harness verifies via
/// &lt;assert-result-document&gt;.
/// </summary>
public class StreamingResultDocumentTests
{
    private static async Task<IReadOnlyDictionary<string, string>> Run(
        string stylesheet, (string name, string xml)[] files, string? initialTemplate)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"strm-rd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        foreach (var (n, x) in files)
            await File.WriteAllTextAsync(Path.Combine(tempDir, n), x);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            if (initialTemplate != null)
                t.SetInitialTemplate(initialTemplate);
            await t.TransformAsync((string?)null);
            return t.SecondaryResultDocuments;
        }
        finally { Directory.Delete(tempDir, true); }
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> secondary, string uriSuffix)
    {
        foreach (var kv in secondary)
            if (kv.Key.EndsWith(uriSuffix, StringComparison.Ordinal))
                return kv.Value;
        return null;
    }

    private static string StripDecl(string s)
    {
        const string decl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        return s.StartsWith(decl, StringComparison.Ordinal) ? s[decl.Length..] : s;
    }

    private const string Books = """
        <?xml version="1.0"?>
        <BOOKLIST>
          <BOOKS>
            <ITEM><PRICE>4.95</PRICE></ITEM>
            <ITEM><PRICE>6.58</PRICE></ITEM>
            <ITEM><PRICE>4.95</PRICE></ITEM>
            <ITEM><PRICE>4.95</PRICE></ITEM>
            <ITEM><PRICE>16.47</PRICE></ITEM>
            <ITEM><PRICE>16.47</PRICE></ITEM>
          </BOOKS>
        </BOOKLIST>
        """;

    // account with nr="76543210" and 20 element children (account-number + 19 transactions),
    // mirroring strm/docs/transactions.xml so count(*) = 20.
    private const string Txns = """
        <account nr="76543210">
          <account-number>01234567</account-number>
          <transaction value="1"/><transaction value="2"/><transaction value="3"/>
          <transaction value="4"/><transaction value="5"/><transaction value="6"/>
          <transaction value="7"/><transaction value="8"/><transaction value="9"/>
          <transaction value="10"/><transaction value="11"/><transaction value="12"/>
          <transaction value="13"/><transaction value="14"/><transaction value="15"/>
          <transaction value="16"/><transaction value="17"/><transaction value="18"/>
          <transaction value="19"/>
        </account>
        """;

    /// <summary>
    /// si-result-document-005: xsl:result-document WRAPPING a streamable xsl:for-each over
    /// a striding path. The scanner must descend into the result-document to register the
    /// for-each subscription so the streamed PRICE elements are captured into the redirect.
    /// </summary>
    [Fact]
    public async Task Rd005_ResultDocumentWrappingStreamingForEach_CapturesElements()
    {
        var ss = """
            <xsl:transform version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <xsl:result-document href="d-005.xml">
                    <out>
                      <xsl:for-each select="/BOOKLIST/BOOKS/ITEM/PRICE">
                        <xsl:copy-of select="."/>
                      </xsl:for-each>
                    </out>
                  </xsl:result-document>
                </xsl:source-document>
              </xsl:template>
            </xsl:transform>
            """;
        var sec = await Run(ss, [("books.xml", Books)], null);
        StripDecl(Lookup(sec, "d-005.xml")!).Should().Be(
            "<out><PRICE>4.95</PRICE><PRICE>6.58</PRICE><PRICE>4.95</PRICE>"
            + "<PRICE>4.95</PRICE><PRICE>16.47</PRICE><PRICE>16.47</PRICE></out>");
    }

    /// <summary>
    /// si-result-document-009 canary: a streamable for-each DIRECTLY wrapping the
    /// result-document (per-match redirect). Must remain correct after the 005 change.
    /// </summary>
    [Fact]
    public async Task Rd009_ForEachWrappingResultDocument_CapturesPerMatch()
    {
        var ss = """
            <xsl:transform version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <xsl:for-each select="outermost(//PRICE)">
                    <xsl:result-document href="d-009-{position()+2}.xml">
                      <PRICE><xsl:sequence select="text()"/></PRICE>
                    </xsl:result-document>
                  </xsl:for-each>
                </xsl:source-document>
              </xsl:template>
            </xsl:transform>
            """;
        var sec = await Run(ss, [("books.xml", Books)], null);
        StripDecl(Lookup(sec, "d-009-3.xml")!).Should().Be("<PRICE>4.95</PRICE>");
        StripDecl(Lookup(sec, "d-009-8.xml")!).Should().Be("<PRICE>16.47</PRICE>");
        sec.Count.Should().Be(6);
    }

    /// <summary>
    /// si-result-document-301: streamable xsl:apply-templates with a STRIDING select
    /// (<c>./account</c>) whose matched template writes an xsl:result-document with a
    /// motionless AVT href (<c>{@nr}</c>). The striding select must route through the
    /// streaming processor so the matched template dispatches.
    /// </summary>
    [Fact]
    public async Task Rd301_StreamingApplyTemplatesStridingSelect_MotionlessHref()
    {
        var ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template name="main">
                <xsl:source-document streamable="yes" href="txns.xml">
                  <xsl:apply-templates select="./account" mode="s"/>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="account" mode="s">
                <xsl:result-document href="{@nr}.xml">
                  <root><xsl:copy-of select="name()"/></root>
                </xsl:result-document>
              </xsl:template>
            </xsl:transform>
            """;
        var sec = await Run(ss, [("txns.xml", Txns)], "main");
        StripDecl(Lookup(sec, "76543210.xml")!).Should().Be("<root>account</root>");
    }

    /// <summary>
    /// si-result-document-303: as 301 but the result-document href is CONSUMING
    /// (<c>{count(*)}</c>) — it counts the matched element's children, which requires the
    /// matched subtree be buffered before the body runs. count(*) must resolve to 20.
    /// </summary>
    [Fact]
    public async Task Rd303_StreamingApplyTemplates_ConsumingHref_CountsChildren()
    {
        var ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template name="main">
                <xsl:source-document streamable="yes" href="txns.xml">
                  <xsl:apply-templates select="./account" mode="s"/>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="account" mode="s">
                <xsl:result-document href="{count(*)}.xml">
                  <root><xsl:copy-of select="name()"/></root>
                </xsl:result-document>
              </xsl:template>
            </xsl:transform>
            """;
        var sec = await Run(ss, [("txns.xml", Txns)], "main");
        StripDecl(Lookup(sec, "20.xml")!).Should().Be("<root>account</root>");
    }

    /// <summary>
    /// si-result-document-304: as 301 but the result-document method AVT is consuming
    /// (<c>{replace(string(.), ...)}</c>) with a fixed href. The matched template must
    /// dispatch and the redirect captured under <c>304.xml</c>.
    /// </summary>
    [Fact]
    public async Task Rd304_StreamingApplyTemplates_ConsumingMethod()
    {
        var ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template name="main">
                <xsl:source-document streamable="yes" href="txns.xml">
                  <xsl:apply-templates select="./account" mode="s"/>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="account" mode="s">
                <xsl:result-document href="304.xml" method="{replace(string(.), '^.+$', 'xml', 's')}">
                  <root><xsl:copy-of select="name()"/></root>
                </xsl:result-document>
              </xsl:template>
            </xsl:transform>
            """;
        var sec = await Run(ss, [("txns.xml", Txns)], "main");
        StripDecl(Lookup(sec, "304.xml")!).Should().Be("<root>account</root>");
    }
}
