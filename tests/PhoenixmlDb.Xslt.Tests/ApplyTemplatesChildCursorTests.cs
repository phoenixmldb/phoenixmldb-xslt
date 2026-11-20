using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:apply-templates with no select or a child-kind select, and the built-in rule, dispatch a
/// node's children as they read them, with no list. position() and last() must still be exact for
/// every selected child, and with-params must still be evaluated when no child is selected.
/// </summary>
public class ApplyTemplatesChildCursorTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    private const string Mixed = "<r>t<a/><!--c--><?p x?>u<b/></r>";

    [Fact]
    public async Task ChildKindSelect_PositionAndLastCountOnlyTheSelectedKinds()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="r"><xsl:apply-templates select="*|comment()|processing-instruction()"/></xsl:template>
              <xsl:template match="node()"><xsl:value-of select="name(), position(), last()" separator=":"/>;</xsl:template>
            </xsl:stylesheet>
            """;

        (await TransformAsync(stylesheet, Mixed)).Should().Be("a:1:4;:2:4;p:3:4;b:4:4;");
    }

    [Fact]
    public async Task NoSelect_PositionAndLastCountEveryChild()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="r"><xsl:apply-templates/></xsl:template>
              <xsl:template match="node()"><xsl:value-of select="position(), last()" separator=":"/>;</xsl:template>
            </xsl:stylesheet>
            """;

        (await TransformAsync(stylesheet, Mixed)).Should().Be("1:6;2:6;3:6;4:6;5:6;6:6;");
    }

    [Fact]
    public async Task BuiltInRule_GivesTheMatchedChildItsPositionAmongAllChildren()
    {
        // No rule matches w, so the built-in rule applies templates to its four children.
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="i"><xsl:value-of select="position(), last()" separator=":"/>;</xsl:template>
              <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;

        (await TransformAsync(stylesheet, "<r><w>x<i/>y<i/></w></r>")).Should().Be("2:4;4:4;");
    }

    [Fact]
    public async Task StreamedPass_BuiltInRuleAndApplyTemplates_KeepTheirPositions()
    {
        // In a streamed pass the children are taken as a list first (nodes can be released while
        // templates run); the result must be the same as in a pass that is not streamed. No text
        // children here: a streamed pass numbers position() without them, which is a separate defect.
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="i"><xsl:value-of select="position()"/>;</xsl:template>
              <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;

        (await TransformAsync(stylesheet, "<r><w><i/><i/></w><w><i/></w></r>")).Should().Be("1;2;1;");
    }

    [Fact]
    public async Task WithParam_IsEvaluatedEvenWhenNoChildIsSelected()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="r">
                <xsl:apply-templates select="*|comment()|processing-instruction()">
                  <xsl:with-param name="p" select="error(QName('urn:t', 'withParamEvaluated'))"/>
                </xsl:apply-templates>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var act = () => TransformAsync(stylesheet, "<r/>");
        (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("withParamEvaluated");
    }
}
