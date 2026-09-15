using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §23.1: an xsl:message is built from its select result followed by its content. The
/// engine used only the select's string value and never ran the content when select was present, so
/// a message lost its content and a selected element lost its markup (W3C message-0202, -0302, -0305,
/// xsl-document-0603). The conformance runner passed every assert-message unchecked, so nothing
/// showed (phoenixmldb-xslt#100).
/// </summary>
public sealed class XslMessageSelectContentTests
{
    private static async Task<(string Output, List<string> Messages)> RunAsync(string body, string declarations = "")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{declarations}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var messages = new List<string>();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.MessageListener = (text, _) => messages.Add(text);
        return ((await t.TransformAsync("<doc/>")).Trim(), messages);
    }

    [Fact]
    public async Task Select_result_is_followed_by_the_content()
    {
        var (_, messages) = await RunAsync("""<xsl:message select="'Error Message:'">This is an error</xsl:message>""");
        messages.Should().ContainSingle().Which.Should().Be("Error Message:This is an error");
    }

    [Fact]
    public async Task A_selected_element_keeps_its_markup()
    {
        var (_, messages) = await RunAsync(
            """<xsl:message select="$v/a"/>""",
            """<xsl:variable name="v"><a>This is an error message.</a></xsl:variable>""");
        messages.Should().ContainSingle().Which.Should().Be("<a>This is an error message.</a>");
    }

    [Fact]
    public async Task A_selected_document_is_followed_by_a_constructed_document()
    {
        var (_, messages) = await RunAsync(
            """<xsl:message select="$v"><xsl:document><xsl:text>* Message two *</xsl:text></xsl:document></xsl:message>""",
            """<xsl:variable name="v" as="document-node()"><xsl:document><xsl:text>* Message one *</xsl:text></xsl:document></xsl:variable>""");
        messages.Should().ContainSingle().Which.Should().Be("* Message one ** Message two *");
    }

    [Fact]
    public async Task A_terminating_message_reports_select_and_content()
    {
        var messages = new List<string>();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><xsl:message terminate="yes" select="'stop:'">now</xsl:message></xsl:template>
            </xsl:stylesheet>
            """);
        t.MessageListener = (text, _) => messages.Add(text);

        var act = () => t.TransformAsync("<doc/>");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTMM9000");
        messages.Should().ContainSingle().Which.Should().Be("stop:now");
    }

    // Guards: behaviour that was already right stays right.
    [Fact]
    public async Task A_content_only_message_is_unchanged()
    {
        var (_, messages) = await RunAsync("""<xsl:message>plain <xsl:value-of select="1 + 1"/></xsl:message>""");
        messages.Should().ContainSingle().Which.Should().Be("plain 2");
    }

    [Fact]
    public async Task A_select_only_message_is_unchanged()
    {
        var (_, messages) = await RunAsync("""<xsl:message select="'just select'"/>""");
        messages.Should().ContainSingle().Which.Should().Be("just select");
    }

    [Fact]
    public async Task A_selected_atomic_sequence_is_space_separated()
    {
        var (_, messages) = await RunAsync("""<xsl:message select="(1, 2, 3)"/>""");
        messages.Should().ContainSingle().Which.Should().Be("1 2 3");
    }

    // W3C si-message-002 builds its message from atomic values produced in content; they are
    // separated like adjacent atomic values anywhere else.
    [Fact]
    public async Task Atomic_values_produced_in_content_are_space_separated()
    {
        var (_, messages) = await RunAsync(
            """<xsl:message><xsl:for-each select="(-1.5, 2, 3)"><xsl:sequence select="."/></xsl:for-each></xsl:message>""");
        messages.Should().ContainSingle().Which.Should().Be("-1.5 2 3");
    }

    // W3C message-0001/-0001a: a selected free-standing attribute cannot be a child of the message's
    // document node. XSLT 3.0 says the transformation does not fail; above all the attribute must not
    // land on the element open in the RESULT, which writing the message through the result writer did.
    [Fact]
    public async Task A_selected_attribute_does_not_attach_to_the_result_element()
    {
        var messages = new List<string>();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:variable name="v"><a att="An attribute">boo</a></xsl:variable>
              <xsl:template match="/"><out><xsl:message select="$v//@att"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        t.MessageListener = (text, _) => messages.Add(text);

        (await t.TransformAsync("<doc/>")).Trim().Should().Be("<out/>");
        messages.Should().ContainSingle().Which.Should().Be("An attribute");
    }

    [Fact]
    public async Task A_message_does_not_leak_into_the_result()
    {
        var (output, _) = await RunAsync("""<xsl:message select="'m'">x</xsl:message><xsl:value-of select="'after'"/>""");
        output.Should().Be("after");
    }
}
