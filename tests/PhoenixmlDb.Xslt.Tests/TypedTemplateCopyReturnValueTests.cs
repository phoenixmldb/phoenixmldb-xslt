using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Reported by Martin Honnen (2026-08-25) against XSpec 4.0.3: report-sequence.xsl failed with
/// <c>XTTE0505: Template 'identity' return value does not match declared type Node: expected
/// exactly one item, got 0</c>.
///
/// The attribute was not lost — it was delivered to the WRONG ELEMENT. xsl:copy of an attribute
/// only joined the result sequence when <c>!_attributeCollecting</c>, but XSpec's identity
/// template is invoked while the CALLER's xsl:copy is still collecting its own attributes. So
/// @status was appended to the caller's element and the typed template returned nothing, and the
/// error blamed the template rather than naming where the item went.
///
/// No W3C conformance case covers this shape — the full XSLT census moved not at all when it was
/// fixed. These tests are the only thing standing between it and a silent return.
/// </summary>
public class TypedTemplateCopyReturnValueTests
{
    private const string Input = "<phrases><phrase status=\"same\">Hello!</phrase></phrases>";

    private static async Task<string> Run(string stylesheet, string input = Input)
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    private static string Identity(string select) => $"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:template name="ident" as="node()">
            <xsl:context-item as="node()" use="required"/>
            <xsl:copy/>
          </xsl:template>
          <xsl:template match="/">
            <out><xsl:for-each select="{select}"><xsl:call-template name="ident"/></xsl:for-each></out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    /// <summary>The reported case: an attribute must count as the typed template's return value.</summary>
    [Fact]
    public async Task Copy_of_attribute_is_the_typed_templates_return_value()
        => (await Run(Identity("/phrases/phrase/@status"))).Should().Contain("status=\"same\"");

    /// <summary>
    /// Found by testing all five node kinds rather than only the reported one: an empty
    /// xsl:copy of a document node produced nothing, because BOTH branches were gated on
    /// instruction.Content != null. A shallow copy with no content is legal.
    /// </summary>
    [Fact]
    public async Task Empty_copy_of_document_node_returns_a_node()
        => (await Run(Identity("/"))).Should().Contain("<out");

    [Theory]
    [InlineData("/phrases", "<phrases/>")]
    [InlineData("/phrases/phrase", "<phrase/>")]
    [InlineData("/phrases/phrase/text()", "Hello!")]
    public async Task Copy_of_other_node_kinds_still_works(string select, string expected)
        => (await Run(Identity(select))).Should().Contain(expected);

    /// <summary>
    /// Martin's stylesheet end to end, reduced. Saxon HE 13 emits the phrase with its attribute
    /// intact and whitespace-only text wrapped in x:ws; so must we.
    /// </summary>
    [Fact]
    public async Task Xspec_report_node_pattern_matches_saxon()
    {
        const string xslt = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:x="http://www.jenitennison.com/xslt/xspec"
              xmlns:local="urn:x-xspec:common:deep-equal:local"
              exclude-result-prefixes="#all">
              <xsl:variable as="xs:anyURI" name="x:xspec-namespace"
                            select="xs:anyURI('http://www.jenitennison.com/xslt/xspec')"/>
              <xsl:mode on-no-match="shallow-copy"/>
              <xsl:mode name="local:report-node" on-multiple-match="fail" on-no-match="fail"/>
              <xsl:template match="document-node() | attribute() | node()" as="node()"
                            mode="local:report-node">
                <xsl:call-template name="local:identity"/>
              </xsl:template>
              <xsl:template match="text()[normalize-space() =&gt; not()]" as="element(x:ws)"
                            mode="local:report-node">
                <xsl:element name="ws" namespace="{$x:xspec-namespace}">
                  <xsl:sequence select="."/>
                </xsl:element>
              </xsl:template>
              <xsl:template as="node()" name="local:identity">
                <xsl:context-item as="node()" use="required"/>
                <xsl:copy>
                  <xsl:apply-templates mode="#current" select="attribute() | node()"/>
                </xsl:copy>
              </xsl:template>
              <xsl:template match="/*">
                <xsl:apply-templates select="." mode="local:report-node"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Run(xslt, "<phrases>\n\t<phrase status=\"same\">Hello!</phrase>\n</phrases>");
        result.Should().Contain("status=\"same\"", "the attribute must survive the identity copy");
        result.Should().Contain("Hello!");
        result.Should().Contain("http://www.jenitennison.com/xslt/xspec",
            "whitespace-only text nodes are wrapped in x:ws");
    }
}
