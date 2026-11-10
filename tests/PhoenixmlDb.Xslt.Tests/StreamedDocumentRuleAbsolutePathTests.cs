using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a rule for the document node, a leading "/" is that document node (XSLT 3.0 §19.8.8.8,
/// §19.8.9.18), so <c>sum(/root/item)</c> and <c>sum(//item)</c> read the matched node's own
/// subtree, as <c>sum(root/item)</c> does. The check that keeps a rule which reads outside its
/// match off the streaming pass (xslt#298) counted every absolute path as reading outside, so these
/// rules read the whole input into a tree: 1 GiB per million items (xslt#468). Where the focus
/// is another node (a step, a predicate, the right of "!", the body of xsl:for-each) "/" is the
/// root of that node, and the rule must still be answered from a tree.
/// </summary>
public sealed class StreamedDocumentRuleAbsolutePathTests
{
    private const string Items = "<root a='x'><item>5</item><item>2</item><item>4</item></root>";
    private const string Nested = "<root><item>1<item>2</item></item><item>4</item></root>";

    // The body of a rule with match="/" and the input; each must stream.
    public static TheoryData<string, string> Streamed => new()
    {
        { """<out><xsl:value-of select="sum(/root/item)"/></out>""", Items },
        { """<out><xsl:value-of select="sum(//item)"/></out>""", Items },
        { """<out><xsl:value-of select="sum(//item)"/></out>""", Nested },
        { """<out><xsl:value-of select="count(/root/item)"/></out>""", Items },
        { """<out><xsl:value-of select="max(/root/item/xs:integer(.))"/></out>""", Items },
        { """<out><xsl:value-of select="count(//item)"/></out>""", Nested },
        { """<out><xsl:value-of select="string-join(/root/item, ',')"/></out>""", Items },
        { """<out>{sum(/root/item)}</out>""", Items },
        { """<out><xsl:for-each select="/root/item"><i><xsl:value-of select="."/></i></xsl:for-each></out>""", Items },
        { """<out><xsl:apply-templates select="/root/item"/></out>""", Items },
        { """<out><xsl:apply-templates select="/root/item"/></out>""", Nested },
        { """<out><xsl:iterate select="/root/item"><xsl:param name="t" select="0"/><xsl:on-completion>{$t}</xsl:on-completion><xsl:next-iteration><xsl:with-param name="t" select="$t + ."/></xsl:next-iteration></xsl:iterate></out>""", Items },
        { """<out><xsl:copy-of select="/root/item"/></out>""", Items },
        { """<out><xsl:if test="exists(/root/item)">yes</xsl:if></out>""", Items },
    };

    // A leading "/" where the focus is not the document node: the rule reads outside the node
    // it is on, and must be answered from a tree, as before.
    public static TheoryData<string, string> NotStreamed => new()
    {
        { """<out><xsl:for-each select="root/item"><i><xsl:value-of select="count(/root/item)"/></i></xsl:for-each></out>""", Items },
        { """<out><xsl:value-of select="root/item ! count(/root/item)"/></out>""", Items },
        { """<out><xsl:value-of select="root/item/count(/root/item)"/></out>""", Items },
        { """<out><xsl:apply-templates select="root/item"><xsl:sort select="count(/root/item)"/></xsl:apply-templates></out>""", Items },
    };

    [Theory]
    [MemberData(nameof(Streamed))]
    public async Task A_leading_slash_in_the_document_rule_streams(string body, string input)
    {
        var stylesheet = Stylesheet(body);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue("a leading slash in a rule for the document node is that node");
    }

    [Theory]
    [MemberData(nameof(NotStreamed))]
    public async Task A_leading_slash_under_another_focus_is_answered_from_a_tree(string body, string input)
    {
        var stylesheet = Stylesheet(body);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeFalse("there the leading slash is the root of another node");
    }

    [Fact]
    public async Task A_leading_slash_in_an_element_rule_is_answered_from_a_tree()
    {
        // In the rule of an element, "/" is the root above it, outside the match.
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template match="root"><out><xsl:value-of select="count(/root/item)"/></out></xsl:template>
            </xsl:stylesheet>
            """;

        var tree = await RunAsync(stylesheet, Items, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Items, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output).And.Contain("<out>3</out>");
        streamed.Streamed.Should().BeFalse();
    }

    private static string Stylesheet(string body) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
          <xsl:mode streamable="yes"/>
          <xsl:template match="/">{body}</xsl:template>
          <xsl:template match="item"><i><xsl:value-of select="."/></i></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<(string Output, bool Streamed)> RunAsync(string stylesheet, string input, bool disableStreaming)
    {
        var transformer = new XsltTransformer { DisableStreaming = disableStreaming };
        await transformer.LoadStylesheetAsync(stylesheet);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed);
    }
}
