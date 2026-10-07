using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An XSD <c>pattern</c> facet is matched inside System.Xml with no timeout, so
/// <see cref="XsltTransformer.RegexMatchTimeout"/> did not reach it: importing a schema and
/// validating a constructed element each ran a backtracking pattern for as
/// long as the value made it. 27 characters against <c>(a+)+b</c> take about five seconds
/// unbounded and end as an ordinary "not valid"; bounded, each stops at the limit.
/// </summary>
public sealed class PatternFacetLimitTests : IDisposable
{
    private static readonly string Value = new('a', 27);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"xslt-pattern-limit-{Guid.NewGuid():N}");

    public PatternFacetLimitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<XsltTransformer> LoadAsync(string schemaBody, string template)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "t.xsd"), $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns="urn:t"
                       elementFormDefault="qualified">
              <xs:simpleType name="slow"><xs:restriction base="xs:string"><xs:pattern value="(a+)+b"/></xs:restriction></xs:simpleType>
              <xs:element name="named" type="slow"/>
              {schemaBody}
            </xs:schema>
            """);
        var transformer = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(300) };
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:t="urn:t">
              <xsl:import-schema namespace="urn:t" schema-location="t.xsd"/>
              <xsl:template match="/">{template}</xsl:template>
            </xsl:stylesheet>
            """, baseUri: new Uri(Path.Combine(_dir, "main.xsl")));
        return transformer;
    }

    [Fact]
    public async Task Importing_a_schema_whose_own_values_run_past_the_limit_fails()
    {
        var load = async () => await LoadAsync(
            $"""<xs:element name="d" type="slow" default="{Value}"/>""", "<out/>");
        (await load.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }

    [Theory]
    [InlineData("<xsl:element name='t:named' validation='strict'><xsl:value-of select=\"string-join((1 to 27) ! 'a', '')\"/></xsl:element>")]
    [InlineData("<xsl:variable name='v'><t:named><xsl:value-of select=\"string-join((1 to 27) ! 'a', '')\"/></t:named></xsl:variable><xsl:copy-of select='$v/*' validation='strict'/>")]
    public async Task A_transformation_stops_at_the_regex_limit(string template)
    {
        var transformer = await LoadAsync("", template);
        var run = async () => await transformer.TransformAsync("<r/>");
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOER0000");
    }

    [Fact]
    public async Task A_value_the_pattern_accepts_is_still_accepted()
    {
        var transformer = await LoadAsync("", "<xsl:element name='t:named' validation='strict'>aab</xsl:element>");
        (await transformer.TransformAsync("<r/>")).Should().Contain("aab");
    }
}
