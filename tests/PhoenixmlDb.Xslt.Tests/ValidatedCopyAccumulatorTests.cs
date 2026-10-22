using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:copy-of with copy-accumulators="yes" and validation="strict": the validated copy has the
/// accumulator values of its source (XSLT 3.0 §18.2.10). Validation builds a new tree, and the
/// values went with the tree it replaced, so accumulator-before on the copy was computed from the
/// copy alone (W3C accumulator-070: 1 where the source has 3).
/// </summary>
public sealed class ValidatedCopyAccumulatorTests
{
    [Theory]
    [InlineData("strict", "3 3 true")]
    [InlineData("lax", "3 3 true")]
    [InlineData("strip", "3 3 false")]
    public async Task A_validated_copy_keeps_the_values_of_its_source(string validation, string expected)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:import-schema>
                <xs:schema>
                  <xs:element name="b" type="b-type"/>
                  <xs:complexType name="b-type"><xs:sequence><xs:element name="i" minOccurs="0"/></xs:sequence></xs:complexType>
                </xs:schema>
              </xsl:import-schema>
              <xsl:output method="text"/>
              <xsl:variable name="data"><doc><a/><b><i/></b><c/></doc></xsl:variable>
              <xsl:accumulator name="n" as="xs:integer" initial-value="0">
                <xsl:accumulator-rule match="*" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:template name="xsl:initial-template">
                <xsl:variable name="x" as="element()">
                  <xsl:copy-of select="$data/doc/b" copy-accumulators="yes" validation="{validation}"/>
                </xsl:variable>
                <xsl:value-of select="$x/accumulator-before('n'), $x/i/accumulator-after('n') - 1, $x instance of element(*, b-type)"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync((string?)null)).Should().Be(expected);
    }
}
