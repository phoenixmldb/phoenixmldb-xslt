using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="Engine.XsltTransformOptions.MaxStringLength"/>. A stylesheet, or a Schematron schema
/// compiled to one, can build a very large string without reading the source document, for
/// example <c>concat($v, $v, …)</c> of one variable. The XPath functions and
/// operators that build strings, and the XSLT instructions that join values into one string,
/// fail with FOER0000 before the string goes past the limit.
/// </summary>
public sealed class MaxStringLengthTests
{
    private const int Limit = 1_000;

    // 100 characters of text, so string(/) is 100 characters long.
    private static readonly string Source = "<doc>" + new string('x', 100) + "</doc>";

    private static async Task<string> TransformAsync(string body, int? maxStringLength)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <xsl:variable name="v" select="string(/)"/>
                {body}
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer { MaxStringLength = maxStringLength };
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(Source);
    }

    private const string V20 = "$v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v, $v";

    // Each body builds a string of about 2,000 characters.
    public static TheoryData<string> Builders => new()
    {
        // A variable from the document, then concat of it.
        $"<out><xsl:value-of select=\"string-length(concat({V20}))\"/></out>",
        "<out><xsl:value-of select=\"string-length(string-join((1 to 2000) ! 'x'))\"/></out>",
        $"<out><xsl:value-of select=\"string-length({V20.Replace(", ", " || ", StringComparison.Ordinal)})\"/></out>",
        "<out><xsl:value-of select=\"(1 to 2000) ! 'x'\" separator=\"\"/></out>",
        "<out><xsl:value-of select=\"(1 to 1000) ! 'x'\"/></out>",
        "<out><xsl:value-of><xsl:for-each select=\"1 to 2000\"><xsl:text>x</xsl:text></xsl:for-each></xsl:value-of></out>",
        "<out a=\"{(1 to 1000) ! 'x'}\"/>",
        "<out a=\"{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}{$v}\"/>",
        "<out><xsl:attribute name=\"a\" select=\"(1 to 1000) ! 'x'\"/></out>",
        "<out><xsl:comment select=\"(1 to 1000) ! 'x'\"/></out>",
        "<out><xsl:processing-instruction name=\"p\" select=\"(1 to 1000) ! 'x'\"/></out>",
    };

    /// <summary>The error code a caller sees, whichever engine raised the error.</summary>
    private static string? CodeOf(Exception ex) => ex switch
    {
        Engine.XsltException x => x.ErrorCode,
        PhoenixmlDb.XQuery.Execution.XQueryRuntimeException q => q.ErrorCode,
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task A_string_over_the_limit_is_FOER0000(string body)
    {
        var act = () => TransformAsync(body, Limit);
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        CodeOf(ex).Should().Be("FOER0000");
        ex.Message.Should().Contain("MaxStringLength");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task Without_a_limit_the_same_stylesheet_runs(string body)
        => (await TransformAsync(body, null)).Should().Contain("<out");

    [Fact]
    public async Task A_string_under_the_limit_is_built()
        => (await TransformAsync("<out><xsl:value-of select=\"string-length(concat($v, $v, $v, $v, $v))\"/></out>", Limit))
            .Should().Contain("<out>500</out>");

    /// <summary>fn:transform passes the calling transformation's limit to the one it starts.</summary>
    [Fact]
    public async Task A_nested_transform_has_the_same_limit()
    {
        var inner = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="xsl:initial-template"><out><xsl:value-of select="string-join((1 to 2000) ! 'x')"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        var body = $"<xsl:variable name=\"r\" select=\"transform(map{{'stylesheet-text': '{inner.Replace("'", "''", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)}', 'initial-template': QName('http://www.w3.org/1999/XSL/Transform', 'initial-template'), 'delivery-format': 'serialized'}})\"/><out><xsl:value-of select=\"string-length($r?output)\"/></out>";
        var act = () => TransformAsync(body, Limit);
        CodeOf((await act.Should().ThrowAsync<Exception>()).Which).Should().Be("FOER0000");
    }
}
