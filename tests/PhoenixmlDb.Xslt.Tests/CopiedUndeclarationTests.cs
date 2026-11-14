using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A default-namespace undeclaration (xmlns="") on the element a copy starts from is not copied:
/// the copy is added to a new parent and inherits its default namespace (W3C namespace-3004,
/// spec bug 5857). Within a deep copy the parent is copied too, and the undeclaration is kept;
/// an unprefixed element in no namespace still gets one.
/// </summary>
public sealed class CopiedUndeclarationTests
{
    private static async Task<string> Run(string select, string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/"><out xmlns="http://t.com/"><xsl:copy-of select="{{select}}"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(source)).Trim();
    }

    private const string Prefixed = """<s:c xmlns:s="urn:s" xmlns="http://t.com/"><s:e xmlns="" ref="x"/></s:c>""";

    [Fact]
    public async Task The_copied_element_inherits_the_new_parents_default_namespace()
        => (await Run("/*/*", Prefixed)).Should().Be("""<out xmlns="http://t.com/"><s:e xmlns:s="urn:s" ref="x"/></out>""");

    [Fact]
    public async Task Within_a_deep_copy_the_undeclaration_is_kept()
        => (await Run("/*", Prefixed)).Should().Contain("""<s:e xmlns="" ref="x"/>""");

    [Fact]
    public async Task An_unprefixed_element_in_no_namespace_keeps_its_undeclaration()
        => (await Run("/*/*", """<r xmlns="http://u.com/"><a xmlns=""/></r>""")).Should().Be("""<out xmlns="http://t.com/"><a xmlns=""/></out>""");
}
