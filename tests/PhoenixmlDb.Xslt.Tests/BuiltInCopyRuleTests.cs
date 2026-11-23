using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xslt#624: the built-in rules <c>on-no-match="shallow-copy"</c> and <c>"deep-copy"</c> write the
/// bytes xsl:copy writes. They repeated on every copied element the namespace declarations it
/// inherits, and wrote an element with no content as <c>&lt;b&gt;&lt;/b&gt;</c>. Expected results:
/// Saxon-HE 10.9 for the same stylesheets.
/// </summary>
public class BuiltInCopyRuleTests
{
    private static async Task<string> Run(string mode, string templates, string source)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode on-no-match="{mode}"/>
              {templates}
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync(source);
    }

    [Theory]
    [InlineData("shallow-copy", "", """<doc xmlns:x="urn:p"><a>t</a><d><b/></d></doc>""", """<doc xmlns:x="urn:p"><a>t</a><d><b/></d></doc>""")]
    [InlineData("deep-copy", "", """<doc xmlns:x="urn:p"><a>t</a><d><b/></d></doc>""", """<doc xmlns:x="urn:p"><a>t</a><d><b/></d></doc>""")]
    [InlineData("shallow-copy", "", """<doc xmlns="urn:d"><n xmlns=""><m/></n><e/></doc>""", """<doc xmlns="urn:d"><n xmlns=""><m/></n><e/></doc>""")]
    [InlineData("deep-copy", "", """<doc xmlns="urn:d"><n xmlns=""><m/></n><e/></doc>""", """<doc xmlns="urn:d"><n xmlns=""><m/></n><e/></doc>""")]
    [InlineData("shallow-copy", """<xsl:template match="skip"/>""",
        """<doc xmlns:x="urn:p"><a><skip/></a><c>t<skip/></c><x:e><skip/><!--k--></x:e></doc>""",
        """<doc xmlns:x="urn:p"><a/><c>t</c><x:e><!--k--></x:e></doc>""")]
    public async Task A_built_in_copy_declares_a_namespace_once_and_writes_an_empty_element_as_empty_element_tag(
        string mode, string templates, string source, string expected)
        => (await Run(mode, templates, source)).Should().Be(expected);

    /// <summary>The built-in copies and xsl:copy, on the same source: the same bytes.</summary>
    [Theory]
    [InlineData("shallow-copy")]
    [InlineData("deep-copy")]
    public async Task A_built_in_copy_writes_what_xsl_copy_writes(string mode)
    {
        const string source = """<doc xmlns="urn:d" xmlns:x="urn:p"><a>t</a><n xmlns=""><m/></n><x:b/><d><e/></d></doc>""";
        var builtIn = await Run(mode, "", source);
        var copy = await Run("shallow-skip", """<xsl:template match="node()"><xsl:copy><xsl:apply-templates/></xsl:copy></xsl:template>""", source);
        builtIn.Should().Be(copy);
    }
}
