using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A static parameter that is not supplied takes its default; when the default cannot be
/// converted to the declared type, the parameter in effect had to be supplied: XTDE0050 at
/// compile time (W3C static-013; Saxon raises the same). The default was not checked, and the
/// value failed later in whatever used it ("Arithmetic operators are not defined for xs:date").
/// </summary>
public sealed class StaticParamDefaultTypeTests
{
    private static async Task<string> Run(string param, string use)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" expand-text="yes">
              <xsl:output method="text"/>
              {{param}}
              <xsl:template name="xsl:initial-template">{{{use}}}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:integer" select="xs:date('2014-03-03')"/>""", "$p * 2")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:boolean" select="xs:date('2014-03-03')"/>""", "$p")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:date*" select="(xs:date('2014-03-03'), 1)"/>""", "count($p)")]
    public async Task A_default_of_another_type_is_XTDE0050(string param, string use)
    {
        var act = () => Run(param, use);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0050");
    }

    [Theory]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:integer" select="3"/>""", "$p * 2", "6")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:double" select="3"/>""", "$p", "3")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:decimal" select="3"/>""", "$p", "3")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:string*" select="('x', 'y')"/>""", "$p", "x y")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:decimal?" select="()"/>""", "count($p)", "0")]
    [InlineData("""<xsl:param name="p" static="yes" as="xs:string" select="xs:anyURI('u:x')"/>""", "$p", "u:x")]
    public async Task A_default_of_the_type_is_used(string param, string use, string expected)
        => (await Run(param, use)).Should().Be(expected);
}
