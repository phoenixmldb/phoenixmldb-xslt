using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A schema location of xsl:import-schema is resolved as a URI against the static base URI of
/// the stylesheet, and never against the process working directory. <c>file:/path</c> (one
/// slash) is an absolute URI (RFC 8089).
/// </summary>
public sealed class SchemaLocationBaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-schema-location").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string location, Uri? stylesheetBase)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "t.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t"><xs:element name="e" type="xs:integer"/></xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:t="urn:t" version="3.0" expand-text="yes" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:import-schema namespace="urn:t" schema-location="{location}"/>
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/"><xsl:variable name="v" as="element()"><t:e xsl:validation="strict">7</t:e></xsl:variable>{"{"}data($v) instance of xs:integer{"}"}</xsl:template>
            </xsl:stylesheet>
            """, stylesheetBase);
        return await t.TransformAsync("<x/>");
    }

    [Fact]
    public async Task A_one_slash_file_location_is_absolute_under_an_http_base()
        => (await RunAsync("file:" + new Uri(Path.Combine(_dir, "t.xsd")).AbsolutePath, new Uri("http://example.com/s/main.xsl")))
            .Should().Contain("true");

    [Fact]
    public async Task A_relative_location_without_a_base_is_refused_and_names_no_working_directory()
    {
        var act = () => RunAsync("nope/t.xsd", null);
        var failure = (await act.Should().ThrowAsync<XsltException>()).Which.Message;
        failure.Should().NotContain(Environment.CurrentDirectory);
    }
}
