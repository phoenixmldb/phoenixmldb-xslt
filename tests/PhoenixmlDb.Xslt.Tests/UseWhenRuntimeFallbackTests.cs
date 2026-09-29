using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// use-when is evaluated by the hand-written static evaluator; an expression shape it does not
/// implement (castable, instance of, …) used to include the element unconditionally, so
/// use-when="not(X castable as T)" came out true (W3C system-property-021). It now falls back
/// to the runtime evaluator, as static variables already did (#156).
/// </summary>
public sealed class UseWhenRuntimeFallbackTests
{
    [Theory]
    [InlineData("'3.0' castable as xs:decimal", true)]
    [InlineData("not('3.0' castable as xs:decimal)", false)]
    [InlineData("not(system-property('xsl:version') castable as xs:decimal)", false)]
    [InlineData("not(3 instance of xs:integer)", false)]
    [InlineData("'x' instance of xs:integer", false)]
    public async Task Use_when_evaluates_shapes_the_static_evaluator_lacks(string condition, bool included)
    {
        var ss = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" exclude-result-prefixes="xs">
              <xsl:template name="xsl:initial-template">
                <r><hit xsl:use-when="{condition}"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Contains("<hit", System.StringComparison.Ordinal).Should().Be(included, r);
    }

    /// <summary>
    /// A static variable declared inside an external entity takes the entity's base URI
    /// (W3C use-when-0136). Before the fallback, the use-when that tests it was never evaluated
    /// (ends-with was unsupported, so the template was included regardless) and the wrong base
    /// went unnoticed.
    /// </summary>
    [Fact]
    public async Task Static_base_uri_inside_an_external_entity_is_the_entity()
    {
        var dir = Directory.CreateTempSubdirectory("usewhen-entity-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "dir"));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "dir", "decl.ent"),
                """<xsl:variable name="sbu" select="static-base-uri()" static="yes"/>""");
            const string ss = """
                <?xml version='1.0'?>
                <!DOCTYPE xsl:stylesheet [ <!ENTITY child SYSTEM "dir/decl.ent"> ]>
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                  &child;
                  <xsl:template name="xsl:initial-template" use-when="ends-with($sbu, 'dir/decl.ent')"><ok/></xsl:template>
                  <xsl:template name="xsl:initial-template" use-when="not(ends-with($sbu, 'dir/decl.ent'))"><wrong-base/></xsl:template>
                </xsl:stylesheet>
                """;
            var t = new XsltTransformer { AllowDtdProcessing = true };
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            await t.LoadStylesheetAsync(ss, new Uri(Path.Combine(dir.FullName, "main.xsl")));
            (await t.TransformAsync((string?)null)).Should().Contain("<ok/>");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
