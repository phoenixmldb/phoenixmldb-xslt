using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:iterate defects found by W3C cases.
/// <para>
/// A local <c>xsl:variable</c> in an <c>xsl:iterate</c> body with the name of an iterate parameter
/// shadows the parameter for the rest of that iteration only. A parameter that
/// <c>xsl:next-iteration</c> does not set keeps its value from the iteration before (XSLT 3.0
/// §8.3). The local replaced the parameter, so the next iteration started from the local's value
/// (W3C iterate-018).
/// </para>
/// <para>
/// <c>xsl:break</c> and <c>xsl:next-iteration</c> inside <c>xsl:try</c> are not dynamic errors.
/// xsl:try took them as errors: it rolled back the output written before them (W3C iterate-035),
/// and a catch-all <c>xsl:catch</c> stopped them, so the loop went on after a break and the
/// parameters kept their old values. Expected values are Saxon-HE 12.10's.
/// </para>
/// </summary>
public sealed class IterateScopeAndTryTests
{
    private const string Body = """
        <xsl:iterate select="x">
          <xsl:param name="n" select="0"/>
          <xsl:param name="k" select="0"/>
          <xsl:on-completion><xsl:value-of select="'end', $n, $k"/></xsl:on-completion>
          <xsl:variable name="n" select="$n + 100"/>
          <xsl:value-of select="$n"/><xsl:text>;</xsl:text>
          <xsl:next-iteration><xsl:with-param name="k" select="$k + $n"/></xsl:next-iteration>
        </xsl:iterate>
        """;

    private const string Source = "<r><x/><x/><x/></r>";

    [Fact]
    public async Task The_local_shadows_the_parameter_in_one_iteration_only()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/r">{Body}</xsl:template>
            </xsl:stylesheet>
            """);

        // The with-param sees the local ($n = 100), and $n is 0 again in every iteration.
        (await t.TransformAsync(new StringReader(Source))).Should().Be("100;100;100;end 0 300");
    }

    [Fact]
    public async Task The_streamed_loop_gives_the_same_result()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="/r">{Body}</xsl:template>
            </xsl:stylesheet>
            """);

        (await t.TransformAsync(new StringReader(Source))).Should().Be("100;100;100;end 0 300");
    }

    private static async Task<string> RunTry(string iterate)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r"><out>{iterate}</out></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync(new StringReader("""<r><x v="1"/><x v="2"/><x v="3"/></r>"""));
    }

    [Fact]
    public async Task A_break_inside_try_keeps_the_output_before_it_and_ends_the_loop()
        => (await RunTry("""
                <xsl:iterate select="x"><i><xsl:value-of select="@v"/></i>
                  <xsl:if test="@v = 2"><xsl:try><b/><xsl:break/><xsl:catch><c/></xsl:catch></xsl:try></xsl:if>
                </xsl:iterate>
                """))
            .Should().Be("<out><i>1</i><i>2</i><b/></out>");

    [Fact]
    public async Task A_next_iteration_inside_try_sets_the_parameters()
        => (await RunTry("""
                <xsl:iterate select="x">
                  <xsl:param name="s" select="0"/>
                  <xsl:on-completion><s><xsl:value-of select="$s"/></s></xsl:on-completion>
                  <xsl:try>
                    <t><xsl:value-of select="@v"/></t>
                    <xsl:next-iteration><xsl:with-param name="s" select="$s + @v"/></xsl:next-iteration>
                    <xsl:catch><c/></xsl:catch>
                  </xsl:try>
                </xsl:iterate>
                """))
            .Should().Be("<out><t>1</t><t>2</t><t>3</t><s>6</s></out>");

    [Fact]
    public async Task An_error_inside_try_in_an_iterate_is_still_caught()
        => (await RunTry("""
                <xsl:iterate select="x">
                  <xsl:try><t/><xsl:value-of select="1 idiv (xs:integer(@v) - 2)"/><xsl:catch><c/></xsl:catch></xsl:try>
                </xsl:iterate>
                """))
            .Should().Be("<out><t/>-1<c/><t/>1</out>");
}
