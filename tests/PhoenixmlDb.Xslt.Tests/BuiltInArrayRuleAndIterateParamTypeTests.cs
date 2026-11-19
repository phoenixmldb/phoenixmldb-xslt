using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Two defects found by W3C cases.
/// <list type="bullet">
/// <item>The built-in rule for an array in the text-only-copy and shallow-skip modes applies
/// templates to the array's members (XSLT 3.0 §6.7.1, §6.7.5), as
/// <c>&lt;xsl:apply-templates select="?*" mode="#current"/&gt;</c>, and passes on its parameters.
/// It did nothing (arrays-301, arrays-302).</item>
/// <item>The <c>as</c> type of an <c>xsl:next-iteration/xsl:with-param</c> converts the value by
/// the function conversion rules. It was not applied (iterate-039). The streamed loop did not
/// apply the <c>as</c> type of the <c>xsl:iterate/xsl:param</c> either.</item>
/// </list>
/// </summary>
public sealed class BuiltInArrayRuleAndIterateParamTypeTests
{
    private static async Task<string> RunInitialTemplate(string declarations, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              {declarations}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    private const string Data = """
        <xsl:variable name="data" as="element()*"><a/><b/><c/></xsl:variable>
        <xsl:template match="*" mode="#all"><e n="{name()}"/></xsl:template>
        """;

    [Theory]
    [InlineData("")]
    [InlineData("""<xsl:mode on-no-match="text-only-copy"/>""")]
    [InlineData("""<xsl:mode on-no-match="shallow-skip"/>""")]
    public async Task Apply_templates_to_an_array_applies_them_to_its_members(string mode)
        => (await RunInitialTemplate(Data + mode, """<out><xsl:apply-templates select="array{$data}"/></out>"""))
            .Should().Be("""<out><e n="a"/><e n="b"/><e n="c"/></out>""");

    [Fact]
    public async Task A_member_that_is_a_sequence_gives_its_items_in_order_and_an_empty_member_gives_none()
        => (await RunInitialTemplate(Data, """<out><xsl:apply-templates select="[$data[3], (), ($data[1], $data[2])]"/></out>"""))
            .Should().Be("""<out><e n="c"/><e n="a"/><e n="b"/></out>""");

    [Fact]
    public async Task Text_only_copy_writes_the_atomic_members_of_an_array()
        => (await RunInitialTemplate("", """<out><xsl:apply-templates select="['x', 'y']"/></out>"""))
            .Should().Be("<out>xy</out>", "Saxon-HE 12.10 writes adjacent text nodes, as for a sequence of atomic values");

    [Fact]
    public async Task The_built_in_rule_passes_its_parameters_to_the_members()
        => (await RunInitialTemplate(
                """
                <xsl:variable name="data" as="element()*"><a/><b/></xsl:variable>
                <xsl:template match="*"><xsl:param name="p"/><e n="{name()}" p="{$p}"/></xsl:template>
                """,
                """<out><xsl:apply-templates select="array{$data}"><xsl:with-param name="p" select="7"/></xsl:apply-templates></out>"""))
            .Should().Be("""<out><e n="a" p="7"/><e n="b" p="7"/></out>""");

    [Fact]
    public async Task Deep_skip_does_not_apply_templates_to_the_members()
        => (await RunInitialTemplate(Data + """<xsl:mode on-no-match="deep-skip"/>""",
                """<out><xsl:apply-templates select="array{$data}"/></out>"""))
            .Should().Be("<out/>");

    private const string Books = """
        <xsl:variable name="in"><t><b len="82"/><b len="29.6"/><b len="1e0"/></t></xsl:variable>
        """;

    [Fact]
    public async Task The_as_type_of_a_next_iteration_with_param_converts_the_value()
        => (await RunInitialTemplate(Books, """
                <out>
                  <xsl:iterate select="$in/t/b">
                    <xsl:param name="total" select="0"/>
                    <xsl:on-completion><xsl:sequence select="$total instance of xs:double*, sum($total)"/></xsl:on-completion>
                    <xsl:next-iteration>
                      <xsl:with-param name="total" select="$total, @len" as="xs:double*"/>
                    </xsl:next-iteration>
                  </xsl:iterate>
                </out>
                """))
            .Should().Be("<out>true 112.6</out>");

    [Fact]
    public async Task A_value_that_the_with_param_type_does_not_allow_is_a_type_error()
    {
        var act = () => RunInitialTemplate(Books, """
                <out>
                  <xsl:iterate select="$in/t/b">
                    <xsl:param name="n" select="0"/>
                    <xsl:on-completion><xsl:value-of select="$n"/></xsl:on-completion>
                    <xsl:next-iteration>
                      <xsl:with-param name="n" select="@len" as="xs:date"/>
                    </xsl:next-iteration>
                  </xsl:iterate>
                </out>
                """);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0570");
    }

    [Theory]
    [InlineData("""as="xs:double*" """, "")]
    [InlineData("", """as="xs:double*" """)]
    public async Task A_streamed_iterate_applies_the_with_param_type_and_the_param_type(string withParamAs, string paramAs)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="/">
                <xsl:iterate select="t/b">
                  <xsl:param name="total" select="()" {paramAs}/>
                  <xsl:on-completion><xsl:sequence select="$total instance of xs:double*"/></xsl:on-completion>
                  <xsl:next-iteration>
                    <xsl:with-param name="total" select="$total, data(@len)" {withParamAs}/>
                  </xsl:next-iteration>
                </xsl:iterate>
              </xsl:template>
            </xsl:stylesheet>
            """);

        (await t.TransformAsync(new StringReader("""<t><b len="82"/><b len="29.6"/></t>""")))
            .Should().Be("true");
    }
}
