using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.StreamingFallback"/>: a construct declared streamable that the
/// analysis of XSLT 3.0 §19.8 finds not guaranteed-streamable runs with a buffer, as it did
/// before XTSE3430 was reported by default, instead of failing. Off by default, and the
/// XTSE3430 message names it.
/// </summary>
public class StreamingFallbackTests
{
    // Martin Honnen's streaming-copyof report: two xsl:copy-of read the children of one record
    // (§19.8.3), so the body of xsl:for-each is not guaranteed-streamable.
    private const string CopyOfTwice = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:mode streamable="yes"/>
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template match="/">
            <xsl:for-each select="records/record">
              <xsl:copy-of select="record-name"/>
              <xsl:copy-of select="value"/>
            </xsl:for-each>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private const string Records =
        "<records><record><record-name>record1</record-name><value>value 1</value></record>"
        + "<record><record-name>record2</record-name><value>value 2</value></record></records>";

    // A validator that SchXslt2 1.11.1 (xspec/lib/schxslt2/transpile.xsl) generates with
    // schxslt:streamable=true for the schema <sch:rule context="m:r"><sch:assert test="exists(m:a)"/>:
    // each rule binds xsl:variable as="node()" select=".", which navigates the streamed node
    // (§19.8.4.41).
    private const string SchXsltValidator = """
<stylesheet xmlns="http://www.w3.org/1999/XSL/Transform" xmlns:svrl="http://purl.oclc.org/dsdl/svrl" xmlns:m="urn:phx:micro" version="3.0" expand-text="false"><variable name="Q{http://dmaus.name/ns/2023/schxslt}phase" as="Q{http://www.w3.org/2001/XMLSchema}string" select="'#ALL'"/><mode use-accumulators=""/><mode name="Q{http://dmaus.name/ns/2023/schxslt}validate" use-accumulators="" on-no-match="shallow-skip"/><mode name="group.d0n8d" on-no-match="shallow-skip" streamable="true" use-accumulators=""/><template match="* | root()" mode="group.d0n8d" priority="-10"><apply-templates select="@*" mode="#current"/><apply-templates select="node()" mode="#current"/></template><template match="m:r" mode="group.d0n8d" priority="0"><param name="Q{http://dmaus.name/ns/2023/schxslt}pattern" as="Q{http://www.w3.org/2001/XMLSchema}string*" select="()"/><variable name="Q{http://dmaus.name/ns/2023/schxslt}rule-context" as="node()" select="."/><choose><when test="'d0n8d' = $Q{http://dmaus.name/ns/2023/schxslt}pattern"><svrl:suppressed-rule context="m:r"><if test="(document-uri(.), base-uri(root()))[1]"><attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/></if></svrl:suppressed-rule><next-match/></when><otherwise><svrl:fired-rule context="m:r"><if test="(document-uri(.), base-uri(root()))[1]"><attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/></if></svrl:fired-rule><variable name="Q{http://dmaus.name/ns/2023/schxslt}severity" as="map(Q{http://www.w3.org/2001/XMLSchema}string, Q{http://www.w3.org/2001/XMLSchema}integer)"><map><map-entry key="'d0n92'"><variable name="severity" as="Q{http://www.w3.org/2001/XMLSchema}string" select="'fatal'"/><sequence select="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity($severity)"/></map-entry></map></variable><for-each select="."><choose><when test="Q{http://www.w3.org/2005/xpath-functions/map}get($Q{http://dmaus.name/ns/2023/schxslt}severity, 'd0n92') ge Q{http://dmaus.name/ns/2023/schxslt}numeric-severity('info')"><if test="not(exists(m:a))"><variable name="failed-assert" as="element(svrl:failed-assert)"><svrl:failed-assert id="exists" role="[ERROR]" test="exists(m:a)"><svrl:text>exists failed</svrl:text></svrl:failed-assert></variable><sequence select="$failed-assert"/></if></when><otherwise><svrl:skipped-assert severityThreshold="info" id="exists" role="[ERROR]" test="exists(m:a)"/></otherwise></choose></for-each><next-match><with-param name="Q{http://dmaus.name/ns/2023/schxslt}pattern" as="Q{http://www.w3.org/2001/XMLSchema}string*" select="('d0n8d', $Q{http://dmaus.name/ns/2023/schxslt}pattern)"/></next-match></otherwise></choose></template><template match="root()" as="element(svrl:schematron-output)"><svrl:schematron-output phase="#ALL"><svrl:ns-prefix-in-attribute-values prefix="m" uri="urn:phx:micro"/><!--SchXslt2 1.11.1--><try><apply-templates select="." mode="Q{http://dmaus.name/ns/2023/schxslt}validate"/><catch><svrl:error code="{$Q{http://www.w3.org/2005/xqt-errors}code}"><if test="(document-uri(.), base-uri(root()))[1]"><attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/></if><if test="$Q{http://www.w3.org/2005/xqt-errors}line-number"><attribute name="line-number" select="$Q{http://www.w3.org/2005/xqt-errors}line-number"/></if><if test="$Q{http://www.w3.org/2005/xqt-errors}column-number"><attribute name="column-number" select="$Q{http://www.w3.org/2005/xqt-errors}column-number"/></if><if test="$Q{http://www.w3.org/2005/xqt-errors}description"><value-of select="$Q{http://www.w3.org/2005/xqt-errors}description"/></if></svrl:error><variable name="message" as="Q{http://www.w3.org/2001/XMLSchema}string+" expand-text="yes">
                    Running the ISO Schematron validation failed with a dynamic error.
                    Error code: {$Q{http://www.w3.org/2005/xqt-errors}code} Reason: {$Q{http://www.w3.org/2005/xqt-errors}description}
                  </variable><message terminate="yes" error-code="Q{{http://dmaus.name/ns/2023/schxslt}}ValidationError"><text/><value-of select="normalize-space(string-join($message))"/></message></catch></try></svrl:schematron-output></template><template match="root()" as="element()*" mode="Q{http://dmaus.name/ns/2023/schxslt}validate"><svrl:active-pattern/><apply-templates select="root()" mode="group.d0n8d"/></template><function name="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity" as="Q{http://www.w3.org/2001/XMLSchema}integer"><param name="severity" as="Q{http://www.w3.org/2001/XMLSchema}string"/><sequence select="(index-of(('info', 'warning', 'error', 'fatal'), $severity), 4)[1]"/></function></stylesheet>
""";

    private static async Task<string> RunAsync(string stylesheet, string input, bool fallback = false, bool strict = false, bool tree = false)
    {
        var t = new XsltTransformer { StreamingFallback = fallback, StrictStreamability = strict, DisableStreaming = tree };
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(input)).Trim();
    }

    private static async Task<string> ErrorAsync(string stylesheet, string input)
    {
        var run = () => RunAsync(stylesheet, input);
        return (await run.Should().ThrowAsync<XsltException>()).Which.Message;
    }

    [Fact]
    public async Task Two_copies_of_a_record_are_XTSE3430_by_default_and_the_message_names_the_setting()
    {
        var message = await ErrorAsync(CopyOfTwice, Records);
        message.Should().StartWith("XTSE3430");
        message.Should().Contain("StreamingFallback").And.Contain("--streaming-fallback");
    }

    [Fact]
    public async Task Two_copies_of_a_record_run_with_the_setting_on()
        => (await RunAsync(CopyOfTwice, Records, fallback: true)).Should().Be(
            "<record-name>record1</record-name><value>value 1</value><record-name>record2</record-name><value>value 2</value>");

    [Fact]
    public async Task A_SchXslt2_streamable_validator_is_XTSE3430_by_default()
    {
        var message = await ErrorAsync(SchXsltValidator, """<r xmlns="urn:phx:micro"><a/></r>""");
        message.Should().StartWith("XTSE3430");
        message.Should().Contain("StreamingFallback");
    }

    [Theory]
    [InlineData("""<r xmlns="urn:phx:micro"><a/></r>""", false)]
    [InlineData("""<r xmlns="urn:phx:micro"><b/></r>""", true)]
    public async Task A_SchXslt2_streamable_validator_runs_with_the_setting_on(string instance, bool fails)
    {
        var report = await RunAsync(SchXsltValidator, instance, fallback: true);
        report.Should().Be(await RunAsync(SchXsltValidator, instance, tree: true));
        report.Should().Contain("<svrl:fired-rule context=\"m:r\"");
        report.Contains("<svrl:failed-assert", StringComparison.Ordinal).Should().Be(fails);
    }

    /// <summary>The setting asks for a buffer, which the strict option does not overrule.</summary>
    [Fact]
    public async Task The_setting_takes_precedence_over_the_strict_option()
        => (await RunAsync(CopyOfTwice, Records, fallback: true, strict: true)).Should().Be(
            await RunAsync(CopyOfTwice, Records, tree: true));

    /// <summary>A guaranteed-streamable stylesheet streams the same with the setting on.</summary>
    [Fact]
    public async Task A_streamable_stylesheet_is_not_changed_by_the_setting()
    {
        const string OneCopy = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/"><xsl:for-each select="records/record"><xsl:copy-of select="value"/></xsl:for-each></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(OneCopy, Records, fallback: true)).Should().Be(await RunAsync(OneCopy, Records));
    }
}
