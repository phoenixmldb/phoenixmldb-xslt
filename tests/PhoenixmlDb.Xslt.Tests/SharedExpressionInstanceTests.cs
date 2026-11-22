using AwesomeAssertions;
using PhoenixmlDb.XQuery.Ast;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The XQuery parser returns one shared instance for every <c>()</c> and every <c>.</c> in the
/// process. The stylesheet parser stamped each parsed expression with the module it was read from,
/// those shared instances included, so every module of every stylesheet wrote to them, at compile and
/// at run time: the CompiledStylesheet freeze run saw xslTNG's compiled tree change under its
/// transformations (two module URIs flipping). They are no longer stamped.
/// </summary>
public class SharedExpressionInstanceTests
{
    [Fact]
    public async Task Parsing_a_stylesheet_does_not_write_to_the_shared_instances()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="e" select="()"/>
              <xsl:template match="/"><xsl:sequence select="."/></xsl:template>
            </xsl:stylesheet>
            """, new Uri("file:///phx/shared-instance-test.xsl"));

        EmptySequence.Instance.EmbeddedInModule.Should().NotBe("file:///phx/shared-instance-test.xsl");
        ContextItemExpression.Instance.EmbeddedInModule.Should().NotBe("file:///phx/shared-instance-test.xsl");
    }
}
