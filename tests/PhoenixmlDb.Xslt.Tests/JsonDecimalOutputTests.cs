using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The json output method writes a number as its xs:string cast (Serialization 3.1 §10).
/// An xs:decimal keeps the scale of the lexical form it came from, and that must not reach the
/// output: W3C si-fork-815 (Saxon bug 3601) wrote -15.00 where -15 is required.
/// </summary>
public sealed class JsonDecimalOutputTests
{
    [Theory]
    [InlineData("xs:decimal('15.00')", "15")]
    [InlineData("xs:decimal('-0.50')", "-0.5")]
    [InlineData("[xs:decimal('6.00'), xs:decimal('12.20')]", "[6,12.2]")]
    public async Task Decimal_is_written_in_canonical_form(string select, string expected)
    {
        var ss = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="json" indent="no"/>
              <xsl:template name="xsl:initial-template">
                <xsl:sequence select="{select}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Replace(" ", "", System.StringComparison.Ordinal).Should().Be(expected);
    }
}
