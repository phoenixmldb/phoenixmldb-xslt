using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The value of a global xsl:variable or xsl:param with select= and as= is converted to the
/// declared type by the function conversion rules, and a value that then does not match it is a
/// type error: XTTE0570 for a variable (XSLT 3.0 §9.3), XTTE0590 for a parameter's default value,
/// which is its supplied value (§9.2.1). Only the annotation of element(N, T) and attribute(N, T)
/// was checked, so an element was accepted for attribute(*, T) (W3C error-0570a) and
/// as="xs:integer" select="'a'" bound the string. ('a', 'b') for as="xs:string?" was joined into
/// one string. Every expected outcome below is Saxon-HE 12.10's.
/// </summary>
public sealed class GlobalSelectAsTypeTests
{
    private static async Task<string> Run(string declaration, string body = """<o v="{count($g)}"/>""")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:variable name="tree"><e2 a="1">x</e2><f/></xsl:variable>
              {declaration}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("$tree/*", "element()")]
    [InlineData("$tree/e2", "element(f)")]
    [InlineData("'s'", "element()")]
    [InlineData("$tree/e2", "text()")]
    [InlineData("$tree/e2", "attribute()")]
    [InlineData("$tree/e2", "xs:integer")]
    [InlineData("1.5", "xs:integer")]
    [InlineData("'a', 'b'", "xs:string?")]
    [InlineData("()", "xs:string")]
    [InlineData("'a'", "xs:integer")]
    public async Task A_value_that_does_not_match_is_XTTE0570(string select, string type)
    {
        var act = () => Run($"""<xsl:variable name="g" select="{select}" as="{type}"/>""");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0570");
    }

    [Theory]
    [InlineData("$tree", "document-node()", "1")]
    [InlineData("$tree/e2/@a", "attribute(a)", "1")]
    [InlineData("$tree/e2", "xs:string", "1")]
    [InlineData("1", "xs:double", "1")]
    [InlineData("$tree/e2", "xs:untypedAtomic", "1")]
    [InlineData("$tree/*", "node()+", "2")]
    [InlineData("'a', 'b'", "xs:string*", "2")]
    public async Task A_value_that_matches_after_conversion_is_bound(string select, string type, string count)
        => (await Run($"""<xsl:variable name="g" select="{select}" as="{type}"/>"""))
            .Should().Be($"""<o v="{count}"/>""");

    [Fact]
    public async Task An_element_for_attribute_of_its_own_type_is_XTTE0570()
    {
        // error-0570a: the element's annotation matches xs:NMTOKEN, so the annotation check let it through.
        var act = () => Run("""
            <xsl:variable name="typed"><e2 xsl:type="xs:NMTOKEN">A002</e2></xsl:variable>
            <xsl:variable name="g" select="$typed/e2" as="attribute(*, xs:NMTOKEN)"/>
            """);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0570");
    }

    [Fact]
    public async Task A_parameter_default_that_does_not_match_is_XTTE0590()
    {
        var act = () => Run("""<xsl:param name="g" select="'a'" as="xs:integer"/>""");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0590");
    }

    [Fact]
    public async Task A_global_reached_from_another_global_is_checked_too()
    {
        // The second global is evaluated on demand while the first one is.
        var act = () => Run("""
            <xsl:variable name="h" select="count($g)"/>
            <xsl:variable name="g" select="'a'" as="xs:integer"/>
            """, """<o v="{$h}"/>""");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0570");
    }
}
