using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// BUGS.md #174: in the template rule for the document node of a streamable mode, a literal result
/// element whose attribute reads the input (<c>&lt;out n="{count(books)}"/&gt;</c>) read the whole
/// input into memory, and
/// <c>--strict-streaming</c> gave phx:XTSE9430, although XSLT 3.0 §19.8 finds the body guaranteed
/// streamable and the same attribute written with <c>xsl:element</c> streamed. The attribute value
/// templates had no watcher, and the document-level check sent every navigating one to the whole
/// input. Each output is the one the transformation gives without streaming.
/// </summary>
public sealed class DocumentRuleAttributeStreamingTests
{
    private const string Books = """<books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book></books>""";

    // Rules for "/" that stream now: the watchers compute each attribute during the pass.
    public static TheoryData<string> Streamed => new()
    {
        """<xsl:template match="/"><out n="{count(books)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{count(books/book)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{sum(books/book/@price)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{name(books)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="a{count(books/book)}b{1 + 1}c"/></xsl:template>""",
        """<xsl:template match="/"><out n="{count(books/book)}" m="x"><inner/></out></xsl:template>""",
        """<xsl:template match="/"><wrap><out n="{count(books/book)}"/></wrap></xsl:template>""",
        """<xsl:template match="/"><out n="{books/book/title}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{string-join(books/book/@price, '-')}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{count(//title)}"/></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(Streamed))]
    public async Task An_attribute_that_reads_the_input_streams_in_the_rule_for_the_document_node(string rules)
    {
        var tree = await RunAsync(rules, t => t.DisableStreaming = true);
        var run = await RunAsync(rules);
        var strict = await RunAsync(rules, t => t.StrictStreamability = true);

        tree.Output.Should().Contain("<out");
        run.Output.Should().Be(tree.Output);
        run.Streamed.Should().BeTrue();
        strict.Output.Should().Be(tree.Output);
        strict.Streamed.Should().BeTrue();
    }

    // Attributes the watchers cannot compute: the input is still read into memory for them, and
    // the output is the one without streaming.
    public static TheoryData<string> ReadWhole => new()
    {
        """<xsl:template match="/"><out n="{books/book[2]/title}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{string(.)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{(books/book/title)[last()]}"/></xsl:template>""",
        """<xsl:template match="/"><out><xsl:attribute name="n" select="string(.)"/></out></xsl:template>""",
        // A path step that is not an axis step, or "!": a subscription would write the items as
        // text in place of the attribute, and a watcher gives the step no node.
        """<xsl:template match="/"><out n="{books/book/@price/number()[. &lt; 1000]}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{/books/book/title/data()[. != 'x']}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{books/book ! string(@price)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{for $b in books/book return $b/@price}"/></xsl:template>""",
        // An operator over input paths, the context item, a positional predicate: as for the
        // select of xsl:value-of.
        """<xsl:template match="/"><out n="{count(books/book | books)}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{(books/book/title) instance of element()+}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{count(books/book except books/book[1])}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{count(books/book[2])}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{string-join(books/book[position() &gt; 1]/title, ',')}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{.}"/></xsl:template>""",
        // A path to attributes.
        """<xsl:template match="/"><out n="{books/book/@price}" m="x"/></xsl:template>""",
        // xsl:attribute in the literal result element still reads the input whole.
        """<xsl:template match="/"><out><xsl:attribute name="n" select="count(books)"/></out></xsl:template>""",
        """<xsl:template match="/"><out><xsl:attribute name="n" select="books/book/@price/number()"/></out></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(ReadWhole))]
    public async Task An_attribute_the_watchers_cannot_compute_is_still_right(string rules)
    {
        var tree = await RunAsync(rules, t => t.DisableStreaming = true);
        var run = await RunAsync(rules);

        tree.Output.Should().Contain("<out n=\"");
        tree.Output.Should().NotContain("n=\"\"");
        run.Output.Should().Be(tree.Output);
    }

    // As before: a consuming attribute in a rule for an element streams, and so does a
    // motionless one in the rule for "/".
    public static TheoryData<string> AsBefore => new()
    {
        """<xsl:template match="books"><out n="{count(book)}"/></xsl:template>""",
        """<xsl:template match="book"><b t="{title}" p="{@price}"/></xsl:template>""",
        """<xsl:template match="/"><out n="{1 + 1}" b="{base-uri()}"/></xsl:template>""",
        """<xsl:template match="/"><xsl:element name="out"><xsl:attribute name="n" select="count(books)"/></xsl:element></xsl:template>""",
        """<xsl:template match="/"><out><xsl:value-of select="count(books/book)"/></out></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(AsBefore))]
    public async Task Other_attributes_stream_as_before(string rules)
    {
        var tree = await RunAsync(rules, t => t.DisableStreaming = true);
        var run = await RunAsync(rules);

        run.Output.Should().Be(tree.Output);
        run.Streamed.Should().BeTrue();
    }

    [Fact]
    public async Task An_attribute_and_an_apply_templates_both_reading_the_input_are_still_XTSE3430()
    {
        // Two consuming operands (§19.8): the input passes once.
        var act = () => RunAsync("""<xsl:template match="/"><out n="{count(books)}"><xsl:apply-templates/></out></xsl:template>""");

        (await act.Should().ThrowAsync<XsltException>()).Which.ErrorCode.Should().Be("XTSE3430");
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
        transformer.SetSourceDocumentUri(new Uri("file:///test/books.xml"));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Books));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed);
    }
}
