using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A static variable's value is fixed when the stylesheet is compiled, when only static variables
/// are in scope (XSLT 3.0 §9.7). A static $q that reads $p took $p's run-time value, so a
/// non-static global $p of higher import precedence changed $q (W3C static-027: q="11", as
/// Saxon-HE 12.10 gives, was q="10").
/// </summary>
public sealed class StaticVariableCompileTimeValueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-static-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> Run(string imported, string principal)
    {
        var importedPath = Path.Combine(_dir, "imported.xsl");
        await File.WriteAllTextAsync(importedPath, $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">{imported}</xsl:stylesheet>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:import href="{new Uri(importedPath).AbsoluteUri}"/>
              {principal}
              <xsl:template name="xsl:initial-template"><r p="{"{$p}"}" q="{"{$q}"}"/></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "principal.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task A_non_static_global_of_higher_precedence_does_not_change_a_static_variable_that_reads_the_name()
        => (await Run("""<xsl:variable name="p" static="yes" select="1"/>""", """
                <xsl:variable name="p" static="no" select="0"/>
                <xsl:variable name="q" static="yes" select="$p + 10"/>
                """))
            .Should().Be("""<r p="0" q="11"/>""");

    [Fact]
    public async Task A_static_variable_that_reads_a_static_variable_keeps_working()
        => (await Run("""<xsl:variable name="p" static="yes" select="2"/>""", """
                <xsl:variable name="q" static="yes" select="$p * 10"/>
                """))
            .Should().Be("""<r p="2" q="20"/>""");
}
