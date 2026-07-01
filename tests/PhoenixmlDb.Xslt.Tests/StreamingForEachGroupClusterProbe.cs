using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression replicas of the W3C strm/si-for-each-group cluster, exercising the
/// streaming group-adjacent accumulator against the real corpus inputs: 035
/// (node-name key + value-of separator), 029 (copy-of current-group), and 007
/// (composite key + aggregate over a striding select with a non-selected sibling).
/// Also covers streaming attribute-key access and streaming result-document-per-group.
/// The composite-key + xsl:iterate cases (008/013) flip on the same mechanism and are
/// confirmed in the conformance harness. si-group-032 (result-document under
/// match="/*" with an &lt;xsl:copy/&gt; root variable) and si-group-033
/// (group-starting-with over select="*/text()") are DEFERRED — see the note at the
/// end of this class.
/// </summary>
public class StreamingForEachGroupClusterProbe
{
    private static async Task<string> RunPrincipal(string stylesheet, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    private static async Task<(string primary, IReadOnlyDictionary<string, string> secondary)> RunFromFile(
        string stylesheet, (string name, string xml)[] files, string? initialTemplate)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"strm-feg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        foreach (var (n, x) in files)
            await File.WriteAllTextAsync(Path.Combine(tempDir, n), x);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            if (initialTemplate != null)
                t.SetInitialTemplate(initialTemplate);
            var primary = await t.TransformAsync((string?)null);
            return (primary, t.SecondaryResultDocuments);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    private static string StripDecl(string s)
    {
        const string decl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        s = s.StartsWith(decl, StringComparison.Ordinal) ? s[decl.Length..] : s;
        return s.Trim();
    }

    private const string Doc035 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <doc>
            <a>a</a><b>1</b><b>2</b><b>3</b><c>c</c><d>d</d><b>4</b><b>5</b><f>f</f><g>g1</g><g>g2</g><h>h</h>
        </doc>
        """;

    private const string Doc029 = """
        <root>
          <record><foo>a</foo><bar>1</bar></record>
          <record><foo>a</foo><bar>2</bar></record>
          <record><foo>b</foo><bar>3</bar></record>
          <record><foo>a</foo><bar>4</bar></record>
          <record><foo>a</foo><bar>5</bar></record>
        </root>
        """;

    private const string Transactions = """
        <account nr="76543210">
          <account-number>01234567</account-number>
          <transaction value="13.24" date="2006-02-13"/>
          <transaction value="8.12" date="2006-02-13"/>
          <transaction value="-15.00" date="2006-02-15"/>
          <transaction value="6.00" date="2006-02-16"/>
          <transaction value="0.50" date="2006-02-17"/>
          <transaction value="2.33" date="2006-02-17"/>
          <transaction value="4.44" date="2006-02-17"/>
          <transaction value="-5.00" date="2006-02-20"/>
          <transaction value="8.99" date="2006-02-21"/>
          <transaction value="16.00" date="2006-02-22"/>
          <transaction value="-2.33" date="2006-02-23"/>
          <transaction value="5.60" date="2006-02-23"/>
          <transaction value="4.32" date="2006-02-23"/>
          <transaction value="6.78" date="2006-02-24"/>
          <transaction value="12.20" date="2006-02-24"/>
          <transaction value="-248.05" date="2006-02-24"/>
          <transaction value="12.00" date="2006-02-25"/>
          <transaction value="13.99" date="2006-02-25"/>
          <transaction value="14.20" date="2006-02-26"/>
        </account>
        """;

    [Fact]
    public async Task Group035_adjacent_nodeName_valueOf_separator()
    {
        var ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" version="3.0">
                <xsl:strip-space elements="*"/>
                <xsl:mode streamable="yes"/>
                <xsl:template match="doc">
                    <xsl:copy>
                        <xsl:for-each-group select="*" group-adjacent="node-name(.)">
                            <xsl:copy>
                                <xsl:value-of select="current-group()" separator=","/>
                            </xsl:copy>
                        </xsl:for-each-group>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunPrincipal(ss, Doc035);
        StripDecl(result).Should().Be(
            "<doc><a>a</a><b>1,2,3</b><c>c</c><d>d</d><b>4,5</b><f>f</f><g>g1,g2</g><h>h</h></doc>",
            $"actual:\n{result}");
    }

    [Fact]
    public async Task Group029_adjacent_copyOf_currentGroup()
    {
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:mode streamable="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template match="root">
                <xsl:copy>
                  <xsl:for-each-group select="record/copy-of()" group-adjacent="foo">
                    <group key="{current-grouping-key()}">
                      <xsl:copy-of select="current-group()"/>
                    </group>
                  </xsl:for-each-group>
                </xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunPrincipal(ss, Doc029);
        StripDecl(result).Should().Be(
            "<root><group key=\"a\"><record><foo>a</foo><bar>1</bar></record><record><foo>a</foo><bar>2</bar></record></group>"
            + "<group key=\"b\"><record><foo>b</foo><bar>3</bar></record></group>"
            + "<group key=\"a\"><record><foo>a</foo><bar>4</bar></record><record><foo>a</foo><bar>5</bar></record></group></root>",
            $"actual:\n{result}");
    }

    [Fact]
    public async Task Group007_nonstreaming_composite_key_aggregate()
    {
        var ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" version="3.0">
              <xsl:template match="account">
                <out>
                  <xsl:for-each-group select="transaction"
                     group-adjacent="year-from-date(xs:date(@date)), format-date(xs:date(@date), '[W]')"
                     composite="yes">
                     <batch year="{current-grouping-key()[1]}" week="{current-grouping-key()[2]}">
                        <total><xsl:value-of select="sum(current-group()/xs:decimal(@value))"/></total>
                     </batch>
                  </xsl:for-each-group>
                </out>
              </xsl:template>
            </xsl:transform>
            """;
        var result = await RunPrincipal(ss, Transactions);
        StripDecl(result).Should().Be(
            "<out><batch year=\"2006\" week=\"7\"><total>19.63</total></batch>"
            + "<batch year=\"2006\" week=\"8\"><total>-161.3</total></batch></out>",
            $"actual:\n{result}");
    }

    [Fact]
    public async Task GroupAttr_streaming_adjacent_reads_attribute_key()
    {
        var ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:template match="account">
                  <out>
                    <xsl:for-each-group select="t" group-adjacent="@k">
                      <g key="{current-grouping-key()}" sum="{sum(current-group()/xs:decimal(@v))}"/>
                    </xsl:for-each-group>
                  </out>
                </xsl:template>
            </xsl:stylesheet>
            """;
        var input = "<account><t k=\"a\" v=\"1\"/><t k=\"a\" v=\"2\"/><t k=\"b\" v=\"5\"/></account>";
        var result = await RunPrincipal(ss, input);
        StripDecl(result).Should().Be(
            "<out><g key=\"a\" sum=\"3\"/><g key=\"b\" sum=\"5\"/></out>", $"actual:\n{result}");
    }

    [Fact]
    public async Task Group007_composite_key_aggregate()
    {
        var ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" version="3.0">
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template name="main">
                <xsl:source-document streamable="yes" href="transactions.xml">
                  <xsl:apply-templates select="account" mode="s"/>
                </xsl:source-document>
              </xsl:template>
              <xsl:template match="account" mode="s">
                <out>
                  <xsl:for-each-group select="transaction"
                     group-adjacent="year-from-date(xs:date(@date)), format-date(xs:date(@date), '[W]')"
                     composite="yes">
                     <batch year="{current-grouping-key()[1]}" week="{current-grouping-key()[2]}">
                        <total><xsl:value-of select="sum(current-group()/xs:decimal(@value))"/></total>
                     </batch>
                  </xsl:for-each-group>
                </out>
              </xsl:template>
            </xsl:transform>
            """;
        var (primary, _) = await RunFromFile(ss, [("transactions.xml", Transactions)], "main");
        StripDecl(primary).Should().Be(
            "<out><batch year=\"2006\" week=\"7\"><total>19.63</total></batch>"
            + "<batch year=\"2006\" week=\"8\"><total>-161.3</total></batch></out>",
            $"actual:\n{primary}");
    }

    [Fact]
    public async Task GroupRD_streaming_resultDocument_per_group_simple()
    {
        var ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" exclude-result-prefixes="xs">
                <xsl:mode streamable="yes"/>
                <xsl:template match="root">
                  <out>
                    <xsl:for-each-group select="p" group-adjacent="@g">
                      <xsl:result-document href="g{current-grouping-key()}.xml">
                        <grp><xsl:copy-of select="current-group()"/></grp>
                      </xsl:result-document>
                    </xsl:for-each-group>
                  </out>
                </xsl:template>
            </xsl:stylesheet>
            """;
        var input = "<root><p g=\"0\">a</p><p g=\"0\">b</p><p g=\"1\">c</p></root>";
        var tempDir = Path.Combine(Path.GetTempPath(), $"strm-feg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(ss, new Uri(tempDir + "/"));
            await t.TransformAsync(input);
            var docs = t.SecondaryResultDocuments;
            docs.Keys.Should().Contain(k => k.EndsWith("g0.xml", StringComparison.Ordinal),
                "keys=[" + string.Join(",", docs.Keys) + "]");
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // NOTE: si-group-032 (result-document per group under match="/*" with an
    // <xsl:variable><xsl:copy/></xsl:variable> shallow-copy of the streamed root) and
    // si-group-033 (group-starting-with over select="*/text()", a text-node member
    // sequence) are DEFERRED — they need a larger mechanism than the child-element
    // group-adjacent/group-starting-with accumulator implemented here (root-template
    // <xsl:copy/> reader interaction, and text-node selection respectively) and remain
    // in the streaming-known-failures baseline.
}
