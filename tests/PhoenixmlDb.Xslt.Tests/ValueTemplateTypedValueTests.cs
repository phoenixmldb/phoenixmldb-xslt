using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An attribute or text value template atomizes a node to its typed value (XSLT 3.0 §5.6). For
/// a node with a schema type it used the string value: {$a} for an xs:integer attribute written
/// 0023 gave 0023, not 23 (W3C cvt-025).
/// </summary>
public sealed class ValueTemplateTypedValueTests
{
    [Fact]
    public async Task A_typed_attribute_gives_its_typed_value()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:template name="main">
                <xsl:variable name="a" as="attribute()"><xsl:attribute name="i" type="xs:integer">0023</xsl:attribute></xsl:variable>
                <xsl:variable name="e" as="element()"><e><xsl:attribute name="i" type="xs:integer">007</xsl:attribute></e></xsl:variable>
                <xsl:variable name="u" as="attribute()"><xsl:attribute name="u">0023</xsl:attribute></xsl:variable>
                <out tvt="{$a}|{$e/@i}|{$u}" xsl:expand-text="yes">{$a}|{$e/@i}|{$u}|{string($a)}</out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");

        (await t.TransformAsync((string?)null)).Should().Contain("""<out tvt="23|7|0023">23|7|0023|0023</out>""");
    }
}
