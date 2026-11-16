using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The xsl:output declarations of one output definition concatenate their use-character-maps
/// in order of increasing import precedence, and the last mapping of a character wins
/// (XSLT 3.0 §26, §26.1). The importing stylesheet's map must therefore beat the imported
/// one's; the imported map won (W3C output-0308).
/// </summary>
public sealed class OutputCharacterMapPrecedenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-cmprec-" + Guid.NewGuid().ToString("N"));

    public OutputCharacterMapPrecedenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Xsl = "xmlns:xsl='http://www.w3.org/1999/XSL/Transform'";

    private async Task<string> Run(string importedMaps, string mainMaps)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.xsl"), $"""
            <xsl:stylesheet version="3.0" {Xsl}>
              <xsl:output method="xml" omit-xml-declaration="yes" use-character-maps="{importedMaps}"/>
            </xsl:stylesheet>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" {Xsl}>
              <xsl:import href="a.xsl"/>
              <xsl:output method="xml" use-character-maps="{mainMaps}"/>
              <xsl:character-map name="hashB"><xsl:output-character character="#" string="B"/></xsl:character-map>
              <xsl:character-map name="hashA"><xsl:output-character character="#" string="A"/></xsl:character-map>
              <xsl:character-map name="dollar"><xsl:output-character character="$" string="D"/></xsl:character-map>
              <xsl:template name="xsl:initial-template"><p>#$</p></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("hashA", "hashB", "<p>B$</p>")]
    [InlineData("hashB", "hashA", "<p>A$</p>")]
    [InlineData("hashA dollar", "hashB", "<p>BD</p>")]
    [InlineData("hashB", "hashA hashB", "<p>B$</p>")]
    public async Task The_higher_precedence_declaration_maps_last(string importedMaps, string mainMaps, string expected)
        => (await Run(importedMaps, mainMaps)).Should().Be(expected);
}
