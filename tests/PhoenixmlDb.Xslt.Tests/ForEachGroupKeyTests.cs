using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §18 grouping-key semantics that the insn/for-each-group W3C set
/// exercises: composite keys compared as tuples (not string-joined), a
/// per-item key SEQUENCE joining multiple groups but only once per group, and
/// dateTime grouping keys compared by their instant (implicit timezone).
/// </summary>
public class ForEachGroupKeyTests
{
    private const string Cities = """
        <cities>
          <city name="milan"  country="italy"   pop="5"/>
          <city name="paris"  country="france"  pop="7"/>
          <city name="bristol" country="england" pop="5.0"/>
          <city name="sheffield" country="england" pop="05"/>
        </cities>
        """;

    // Composite key (@country, xs:decimal(@pop)): current-grouping-key() must be
    // a two-item SEQUENCE, so [1]=country and [2]=pop and count()=2 — NOT a single
    // space-joined "italy 5" value. (for-each-group-043 / -072 / -087)
    [Fact]
    public async Task CompositeKey_CurrentGroupingKey_IsTuple()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:template match="/">
                <out>
                  <xsl:for-each-group select="/*/city" group-by="@country, xs:decimal(@pop)" composite="yes">
                    <group country="{current-grouping-key()[1]}" pop="{current-grouping-key()[2]}"
                           count="{count(current-grouping-key())}"/>
                  </xsl:for-each-group>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var result = await transformer.TransformAsync(Cities);

        result.Should().Contain("country=\"italy\"", $"actual:\n{result}");
        result.Should().Contain("pop=\"5\"", $"actual:\n{result}");
        result.Should().Contain("count=\"2\"", $"actual:\n{result}");
        result.Should().NotContain("italy 5", $"actual:\n{result}");
        // england/5 (bristol "5.0") and england/5 (sheffield "05") are the SAME
        // composite tuple → one group with two members.
        result.Should().Contain("country=\"england\"", $"actual:\n{result}");
    }

    // group-by returns a SEQUENCE of keys per item; the item joins one group per
    // DISTINCT key value. milan (pop=5, name-length=5) yields the sequence (5,5) —
    // it must appear ONCE in the pop/length=5 group, not twice. (for-each-group-033)
    [Fact]
    public async Task SequenceKey_ItemJoinsEachGroupOnce()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <g>
                  <xsl:for-each-group select="//city" group-by="number(@pop), string-length(@name)">
                    <group key="{current-grouping-key()}">
                      <xsl:copy-of select="current-group()"/>
                    </group>
                  </xsl:for-each-group>
                </g>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var result = await transformer.TransformAsync(Cities);

        // First group has key 5 (milan pop=5 & name-length=5, paris name-length=5,
        // bristol pop=5.0, sheffield pop=05). milan appears exactly once.
        var milanCount = System.Text.RegularExpressions.Regex.Count(result, "name=\"milan\"");
        milanCount.Should().Be(1, $"actual:\n{result}");
    }

    private const string TzCities = """
        <cities>
          <city name="milan"  date="2001-04-04T13:00:00+02:00"/>
          <city name="paris"  date="2001-04-04T11:00:00+00:00"/>
          <city name="munich" date="2001-04-04T12:00:00+01:00"/>
          <city name="london" date="2001-04-04T12:00:00+00:00"/>
        </cities>
        """;

    // dateTime grouping keys compare by instant: milan (13:00+02:00),
    // paris (11:00+00:00) and munich (12:00+01:00) are all the SAME instant
    // (11:00Z) → one group; london (12:00Z) is a separate group.
    // (for-each-group-061 / -062 / -064 / -065)
    [Fact]
    public async Task DateTimeKey_ComparesByInstant()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <out>
                  <xsl:for-each-group select="//city" group-by="xs:dateTime(@date)">
                    <group size="{count(current-group())}">
                      <xsl:value-of select="string-join(current-group()/@name, ',')"/>
                    </group>
                  </xsl:for-each-group>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var result = await transformer.TransformAsync(TzCities);

        result.Should().Contain("milan,paris,munich", $"actual:\n{result}");
        result.Should().Contain(">london<", $"actual:\n{result}");
        result.Should().Contain("size=\"3\"", $"actual:\n{result}");
    }
}
