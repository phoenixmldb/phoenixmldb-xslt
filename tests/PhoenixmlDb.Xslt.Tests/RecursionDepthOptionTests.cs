using AwesomeAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformOptions.MaxRecursionDepth"/> and <see cref="XsltTransformer.MaxRecursionDepth"/>:
/// how deep xsl:apply-templates, xsl:call-template (not a tail call) and stylesheet functions may
/// nest. It was a constant, 1,200, so an identity transform of a document 2,000 elements deep
/// failed with XTDE0000 and a host could not allow more. Default 2,500; a value less than 1 is refused.
/// </summary>
public sealed class RecursionDepthOptionTests
{
    private const string Identity = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/"><xsl:variable name="c"><xsl:apply-templates/></xsl:variable><xsl:value-of select="count($c//*)"/></xsl:template>
          <xsl:template match="@*|node()"><xsl:copy><xsl:apply-templates select="@*|node()"/></xsl:copy></xsl:template>
        </xsl:stylesheet>
        """;

    // A named template that is not a tail call (its call is inside an element), and a function whose
    // recursive call is not in tail position.
    private const string Recursive = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f">
          <xsl:output method="text"/>
          <xsl:param name="n" as="xs:integer"/>
          <xsl:param name="kind" as="xs:string"/>
          <xsl:template name="t"><xsl:param name="n"/><xsl:if test="$n gt 0"><x><xsl:call-template name="t"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template></x></xsl:if></xsl:template>
          <xsl:function name="f:d" as="xs:integer"><xsl:param name="n" as="xs:integer"/><xsl:sequence select="if ($n le 0) then 0 else 1 + f:d($n - 1)"/></xsl:function>
          <xsl:template match="/">
            <xsl:choose>
              <xsl:when test="$kind = 'template'"><xsl:variable name="c"><xsl:call-template name="t"><xsl:with-param name="n" select="$n"/></xsl:call-template></xsl:variable><xsl:value-of select="count($c//x)"/></xsl:when>
              <xsl:otherwise><xsl:value-of select="f:d($n)"/></xsl:otherwise>
            </xsl:choose>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static string Deep(int depth) => string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

    private static async Task<string> RecurseAsync(XsltTransformer transformer, string kind, int n)
    {
        await transformer.LoadStylesheetAsync(Recursive);
        transformer.SetParameter("n", (long)n);
        transformer.SetParameter("kind", kind);
        return (await transformer.TransformAsync("<x/>")).Trim();
    }

    [Fact]
    public void The_default_is_2500()
    {
        new XsltTransformer().MaxRecursionDepth.Should().Be(2_500);
        new XsltTransformOptions().MaxRecursionDepth.Should().Be(2_500);
    }

    [Fact]
    public async Task By_default_an_identity_transform_of_a_document_2000_elements_deep_runs()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Identity);

        (await transformer.TransformAsync(Deep(2_000))).Trim().Should().Be("2000");
    }

    [Theory]
    [InlineData("template")]
    [InlineData("function")]
    public async Task Nesting_past_the_default_is_XTDE0000_and_names_the_option(string kind)
    {
        var act = () => RecurseAsync(new XsltTransformer(), kind, 3_000);

        var message = (await act.Should().ThrowAsync<Exception>()).Which.Message;
        message.Should().Contain("XTDE0000").And.Contain("2500").And.Contain("MaxRecursionDepth");
    }

    [Theory]
    [InlineData("template")]
    [InlineData("function")]
    public async Task A_lower_limit_stops_the_recursion_there(string kind)
    {
        (await RecurseAsync(new XsltTransformer { MaxRecursionDepth = 100 }, kind, 90)).Should().Be("90");

        var act = () => RecurseAsync(new XsltTransformer { MaxRecursionDepth = 100 }, kind, 150);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0000").And.Contain("100");
    }

    [Fact]
    public async Task A_higher_limit_allows_deeper_recursion()
    {
        var transformer = new XsltTransformer { MaxRecursionDepth = 5_000 };
        await transformer.LoadStylesheetAsync(Identity);

        (await transformer.TransformAsync(Deep(4_000))).Trim().Should().Be("4000");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_value_less_than_1_is_refused(int value)
    {
        var onTransformer = () => new XsltTransformer { MaxRecursionDepth = value };
        onTransformer.Should().Throw<ArgumentOutOfRangeException>();

        var onOptions = () => new XsltTransformOptions { MaxRecursionDepth = value };
        onOptions.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_nested_fn_transform_uses_the_callers_limit()
    {
        var transformer = new XsltTransformer { MaxRecursionDepth = 100 };
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:variable name="inner" as="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema"><![CDATA[
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:template name="xsl:initial-template"><xsl:call-template name="t"><xsl:with-param name="n" select="150"/></xsl:call-template></xsl:template>
                  <xsl:template name="t"><xsl:param name="n"/><xsl:if test="$n gt 0"><x><xsl:call-template name="t"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template></x></xsl:if></xsl:template>
                </xsl:stylesheet>]]></xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count(transform(map { 'stylesheet-text': $inner, 'initial-template': QName('http://www.w3.org/1999/XSL/Transform', 'initial-template') })?output//*)"/></xsl:template>
            </xsl:stylesheet>
            """);

        var act = () => transformer.TransformAsync("<x/>");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0000").And.Contain("100");
    }
}
