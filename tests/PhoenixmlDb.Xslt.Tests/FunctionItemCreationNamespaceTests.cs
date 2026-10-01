using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A function item for system-property() or element-available() resolves a prefixed argument
/// against the namespaces in scope where the ITEM WAS CREATED (W3C system-property-101d and
/// siblings). Invoked inside xsl:evaluate, it used the evaluated expression's namespaces instead,
/// where the prefix may not be bound, and raised XTDE1390.
/// </summary>
public sealed class FunctionItemCreationNamespaceTests
{
    private static async Task<string> RunAsync(string create)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <!-- 'p' is bound only here, where the item is created. -->
              <xsl:variable name="f" select="{{create}}" xmlns:p="http://www.w3.org/1999/XSL/Transform"/>
              <xsl:template match="/">
                <xsl:evaluate xpath="'$f(''p:version'')'"><xsl:with-param name="f" select="$f"/></xsl:evaluate>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<x/>")).Trim();
    }

    [Theory]
    [InlineData("system-property#1")]
    [InlineData("system-property(?)")]
    [InlineData("function-lookup(QName('http://www.w3.org/2005/xpath-functions', 'system-property'), 1)")]
    public async Task A_system_property_item_resolves_prefixes_where_it_was_created(string create)
        => (await RunAsync(create)).Should().Be("3.0");
}
