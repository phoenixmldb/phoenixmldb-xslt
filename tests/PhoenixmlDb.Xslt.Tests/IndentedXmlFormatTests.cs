using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// indent="yes" with the XML method, as Saxon-HE 12.10 writes it: an empty element is
/// <c>&lt;e/&gt;</c>, as unindented output writes it (it was <c>&lt;e /&gt;</c>, the form .NET's
/// XmlWriter uses), and the document element starts on the line after the XML declaration (it
/// followed it on the same line). The XHTML method keeps <c>&lt;br /&gt;</c> for HTML user agents.
/// </summary>
public sealed class IndentedXmlFormatTests
{
    private static async Task<string> Run(string output, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              {output}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    private const string Body = """<root><missing/><a x="1"/><b>t</b></root>""";

    [Fact]
    public async Task An_empty_element_has_no_space_before_the_slash()
    {
        var result = await Run("""<xsl:output method="xml" indent="yes"/>""", Body);

        result.Should().Contain("<missing/>").And.Contain("""<a x="1"/>""").And.NotContain(" />");
    }

    [Fact]
    public async Task The_document_element_starts_on_the_line_after_the_declaration()
    {
        var result = await Run("""<xsl:output method="xml" indent="yes"/>""", Body);

        result.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root>");
    }

    [Fact]
    public async Task Without_indent_the_declaration_and_the_root_stay_on_one_line()
    {
        var result = await Run("""<xsl:output method="xml" indent="no"/>""", Body);

        result.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?><root><missing/>");
    }

    [Fact]
    public async Task Text_after_the_declaration_is_not_given_a_newline()
    {
        // Not a well-formed document (W3C whitespace-022): a newline there would be content.
        var result = await Run("""<xsl:output method="xml" indent="yes"/>""", "<xsl:text>test</xsl:text>");

        result.Should().Be("<?xml version=\"1.0\" encoding=\"UTF-8\"?>test");
    }

    [Fact]
    public async Task The_xhtml_method_keeps_the_space_before_the_slash()
    {
        var result = await Run("""<xsl:output method="xhtml" indent="yes"/>""",
            """<html xmlns="http://www.w3.org/1999/xhtml"><body><p>a<br/>b</p></body></html>""");

        result.Should().Contain("<br />");
    }
}
