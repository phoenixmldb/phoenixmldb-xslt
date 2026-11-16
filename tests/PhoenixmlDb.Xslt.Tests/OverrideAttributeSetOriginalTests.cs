using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An overriding attribute set with use-attribute-sets="xsl:original" includes the attributes of
/// the set it replaces, and of the sets that one uses: they resolve in the used package, where its
/// private sets are. The replaced set was not stamped with its package, its use-attribute-sets
/// were looked up in the using package, and their attributes were lost (W3C override-as-002).
/// Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class OverrideAttributeSetOriginalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-asorig-" + Guid.NewGuid().ToString("N"));

    public OverrideAttributeSetOriginalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:attribute-set name="pub" visibility="public" use-attribute-sets="priv"><xsl:attribute name="p" select="'lib'"/><xsl:attribute name="q" select="'q'"/></xsl:attribute-set>
          <xsl:attribute-set name="priv" visibility="private" use-attribute-sets="base"><xsl:attribute name="r" select="'r'"/></xsl:attribute-set>
          <xsl:attribute-set name="base" visibility="public"><xsl:attribute name="s" select="'s'"/></xsl:attribute-set>
        </xsl:package>
        """;

    [Fact]
    public async Task The_original_brings_the_sets_it_uses()
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:lib">
                <xsl:override>
                  <xsl:attribute-set name="pub" visibility="public" use-attribute-sets="xsl:original"><xsl:attribute name="p" select="'mine'"/></xsl:attribute-set>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="xsl:initial-template"><x xsl:use-attribute-sets="pub"/></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        (await t.TransformAsync((string?)null)).Trim().Should().Be("<x s=\"s\" r=\"r\" p=\"mine\" q=\"q\"/>");
    }
}
