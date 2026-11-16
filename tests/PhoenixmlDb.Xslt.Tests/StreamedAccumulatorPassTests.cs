using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The accumulators of a streamed pass have the values the tree walk gives (W3C accumulator-077s,
/// accumulator-079s). Three things made them differ: an end rule that reads a later-declared
/// accumulator's after-value read it before that accumulator's own rule ran; an element in a
/// subtree a template rule skipped had no parent, so a pattern of more than one step never
/// matched it; and a nested xsl:apply-templates, which reads each child whole, computed the
/// child's values from the initial value instead of the pass's running ones. Every case is
/// checked against the same stylesheet run unstreamed, and the expected values are Saxon-HE
/// 12.10's.
/// </summary>
public sealed class StreamedAccumulatorPassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-accpass-" + Guid.NewGuid().ToString("N"));

    private const string Fields =
        "<root><fields><data><id1>1</id1><id2>9</id2></data></fields>"
        + "<fields><data><id1>2</id1><id2>9</id2></data><data><id1>3</id1><id2>9</id2></data></fields></root>";

    private const string Headers =
        "<root><header><header-item><id>1</id><value>one</value></header-item>"
        + "<header-item><id>2</id><value>two</value></header-item></header>"
        + "<body><ref idref='1'/><ref idref='2'/></body></root>";

    public StreamedAccumulatorPassTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string declarations, string source, bool streamable)
    {
        var path = Path.Combine(_dir, "d.xml");
        await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);
        var yes = streamable ? "yes" : "no";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:map="http://www.w3.org/2005/xpath-functions/map"
                exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode streamable="{{yes}}" on-no-match="shallow-skip" use-accumulators="#all"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="{{yes}}" href="d.xml" use-accumulators="#all"><xsl:apply-templates/></xsl:source-document>
              </xsl:template>
              {{declarations.Replace("STREAMABLE", yes, StringComparison.Ordinal)}}
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    // The header-map of accumulator-077: an end rule reads two accumulators declared after it.
    private const string HeaderMap = """
        <xsl:accumulator name="map" as="map(xs:integer, xs:string)" initial-value="map{}" streamable="STREAMABLE">
          <xsl:accumulator-rule match="header-item" phase="end"
              select="map:put($value, accumulator-after('id'), accumulator-after('value'))"/>
        </xsl:accumulator>
        <xsl:accumulator name="id" as="xs:integer?" initial-value="()" streamable="STREAMABLE">
          <xsl:accumulator-rule match="header-item/id/text()" select="xs:integer(.)"/>
        </xsl:accumulator>
        <xsl:accumulator name="value" as="xs:string?" initial-value="()" streamable="STREAMABLE">
          <xsl:accumulator-rule match="header-item/value/text()" select="string()"/>
        </xsl:accumulator>
        <xsl:template match="ref"><v><xsl:value-of select="accumulator-before('map')(xs:integer(@idref))"/></v></xsl:template>
        """;

    // A counter read after the children of each fields element, and a chain of end rules on one node.
    private const string Counter = """
        <xsl:accumulator name="a" as="xs:integer" initial-value="0" streamable="STREAMABLE">
          <xsl:accumulator-rule match="id1" phase="end" select="$value + accumulator-after('b')"/>
        </xsl:accumulator>
        <xsl:accumulator name="b" as="xs:integer" initial-value="0" streamable="STREAMABLE">
          <xsl:accumulator-rule match="id1" phase="end" select="$value + 10"/>
        </xsl:accumulator>
        <xsl:template match="fields"><xsl:apply-templates/><z><xsl:value-of select="accumulator-after('a'), accumulator-after('b')"/></z></xsl:template>
        """;

    public static TheoryData<string, string, string> Cases => new()
    {
        // The header subtree is visited.
        { HeaderMap, Headers, "<v>one</v><v>two</v>" },
        // The header subtree is skipped by a template rule; its accumulators still run.
        { HeaderMap + "<xsl:template match='header'/>", Headers, "<v>one</v><v>two</v>" },
        // fields is reached by the pass.
        { Counter, Fields, "<z>10 10</z><z>60 30</z>" },
        // fields is reached by a nested apply-templates, which reads each one whole.
        { Counter + "<xsl:template match='root'><out><xsl:apply-templates/></out></xsl:template>", Fields,
            "<out><z>10 10</z><z>60 30</z></out>" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Streamed_values_are_the_tree_walks(string declarations, string source, string expected)
    {
        (await Run(declarations, source, streamable: false)).Should().Be(expected, "the tree walk");
        (await Run(declarations, source, streamable: true)).Should().Be(expected, "the streamed pass");
    }
}
