using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §19.8.9.18: where the context item is a document node, <c>root()</c> is that node. In a
/// template rule for the document node, <c>root()/books</c> is <c>books</c>, and the rule is
/// guaranteed-streamable; it was refused with XTSE3430, also without the strict check, because the
/// check took <c>root()</c> as a climb in every rule (xslt#655). It runs now, with the input read into
/// memory: the streaming pass does not evaluate a path from <c>root()</c> (it does <c>books</c>), so
/// the strict check gives phx:XTSE9430 for it (BUGS.md #165). In a rule for an element,
/// <c>root()/x</c> and <c>root()//x</c> are roaming and free-ranging (§19.8), and XTSE3430 stays.
/// </summary>
public sealed class RootOfDocumentRuleTests
{
    private const string Books = """<books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book></books>""";

    public static TheoryData<string> DocumentRules => new()
    {
        """<xsl:template match="/"><out><xsl:value-of select="count(root()/books)"/></out></xsl:template>""",
        """<xsl:template match="/"><out><xsl:value-of select="count(root()//title)"/></out></xsl:template>""",
        """<xsl:template match="/"><out><xsl:value-of select="count(root(.)/books/book)"/></out></xsl:template>""",
        """<xsl:template match="/"><out n="{count(root()/books)}"/></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(DocumentRules))]
    public async Task Root_in_a_rule_for_the_document_node_is_the_document_node(string rules)
    {
        var tree = await RunAsync(rules, t => t.DisableStreaming = true);
        var run = await RunAsync(rules);

        tree.Output.Should().Contain("<out");
        run.Output.Should().Be(tree.Output);
    }

    [Theory]
    [MemberData(nameof(DocumentRules))]
    public async Task The_strict_check_accepts_root_in_a_rule_for_the_document_node(string rules)
    {
        // Accepted by §19.8, so not XTSE3430; the input is read into memory for the path from root().
        var act = () => RunAsync(rules, t => t.StrictStreamability = true);

        var error = (await act.Should().ThrowAsync<XsltException>()).Which;
        error.ErrorCode.Should().Be("phx:XTSE9430");
        error.Message.Should().Contain("this processor cannot evaluate root(");
    }

    // These must stay errors: in a rule for an element, root() is the document node above it; and
    // in a rule whose pattern also matches elements, or in a loop of a rule for "/", the context
    // item is not known to be a document node.
    public static TheoryData<string> ElementRules => new()
    {
        """<xsl:template match="/ | book"><b n="{count(root()/books)}"/><xsl:apply-templates/></xsl:template>""",
        """<xsl:template match="/"><out><xsl:for-each select="books/book"><b n="{count(root()/books)}"/></xsl:for-each></out></xsl:template>""",
        """<xsl:template match="book"><b n="{count(root()/books)}"/></xsl:template>""",
        """<xsl:template match="book"><b n="{count(root()//title)}"/></xsl:template>""",
        """<xsl:template match="book"><b><xsl:value-of select="count(root()/books)"/></b></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(ElementRules))]
    public async Task Root_in_a_rule_for_an_element_is_XTSE3430(string rules)
    {
        foreach (var strict in new[] { false, true })
        {
            var act = () => RunAsync(rules, t => t.StrictStreamability = strict);
            (await act.Should().ThrowAsync<XsltException>()).Which.ErrorCode.Should().Be("XTSE3430");
        }
    }

    private static async Task<(string Output, bool Streamed)> RunAsync(string rules, Action<XsltTransformer>? configure = null)
    {
        var transformer = new XsltTransformer();
        configure?.Invoke(transformer);
        await transformer.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
              {{rules}}
            </xsl:stylesheet>
            """);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Books));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed);
    }
}
