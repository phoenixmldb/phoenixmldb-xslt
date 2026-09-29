using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A match="/" template in a streamable mode that wraps its apply-templates in a variable (or
/// xsl:element, xsl:fork, xsl:where-populated) was not recognised as driving the stream, so the
/// apply-templates ran against the empty synthetic document and the variable held nothing,
/// whatever the templates returned (W3C si-fork-812/-813, si-apply-templates-013). With the stream
/// driven, document-level whitespace (outside the root element, not an XDM node) must not be
/// dispatched either: it put a "\n" string into the variable ahead of the real result.
/// </summary>
public sealed class StreamedApplyTemplatesInVariableTests
{
    private const string Source = "<?xml version=\"1.0\"?>\n<cities>\n  <city name=\"Milano\" country=\"Italia\"/>\n  <city name=\"Paris\" country=\"France\"/>\n  <city name=\"Venezia\" country=\"Italia\"/>\n</cities>\n";

    private static async Task<string> RunAsync(string rootBody, string citiesBody)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:mode streamable="yes"/>
              <xsl:template match="/">{{rootBody}}</xsl:template>
              <xsl:template match="cities">{{citiesBody}}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(Source)).Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "", StringComparison.Ordinal).Trim();
    }

    [Fact]
    public async Task A_typed_variable_receives_an_atomic_result()
        => (await RunAsync(
                """<xsl:variable name="v" as="xs:integer*"><xsl:apply-templates select="cities"/></xsl:variable><out n="{count($v)}" v="{$v}"/>""",
                """<xsl:sequence select="count(city)"/>"""))
            .Should().Be("""<out n="1" v="3"/>""");

    [Fact]
    public async Task A_typed_variable_receives_maps_from_a_streamed_group()
        => (await RunAsync(
                """<xsl:variable name="v" as="map(*)*"><xsl:apply-templates/></xsl:variable><out><xsl:for-each select="$v"><c k="{?k}" n="{?n}"/></xsl:for-each></out>""",
                """<xsl:fork><xsl:for-each-group select="city" group-by="@country"><xsl:map><xsl:map-entry key="'k'" select="current-grouping-key()"/><xsl:map-entry key="'n'" select="count(current-group())"/></xsl:map></xsl:for-each-group></xsl:fork>"""))
            .Should().Be("""<out><c k="Italia" n="2"/><c k="France" n="1"/></out>""");

    [Fact]
    public async Task A_typed_variable_receives_elements()
        => (await RunAsync(
                """<xsl:variable name="v" as="element()*"><xsl:apply-templates/></xsl:variable><out n="{count($v)}"/>""",
                """<g/><g/>"""))
            .Should().Be("""<out n="2"/>""");
}
