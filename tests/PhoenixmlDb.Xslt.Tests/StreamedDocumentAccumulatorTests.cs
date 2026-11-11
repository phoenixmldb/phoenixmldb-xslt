using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// accumulator-after() of the document node, after xsl:apply-templates has read its children,
/// is the streamed sum of XSLT 3.0 (§19.8.9.1: motionless once the node has been consumed). The
/// streaming pass recorded no value for the document node, so every such rule was read into a
/// tree, and the pass kept the values of every node it had passed: 1.3 GiB per million items
/// either way (xslt#461). The pass now records the document node's values at its start and end,
/// and drops a node's values once it has ended. Each case must give what the tree gives.
/// </summary>
public sealed class StreamedDocumentAccumulatorTests
{
    private const string Items = "<root><item>5</item><item>2.5</item><item>-1</item></root>";
    private const string Nested = "<root><group><item>1</item><item>2</item></group><item>4</item><group/></root>";

    private const string Accumulators = """
        <xsl:accumulator name="sum" as="xs:decimal" initial-value="0" streamable="yes">
          <xsl:accumulator-rule match="item/text()" select="$value + xs:decimal(.)"/>
        </xsl:accumulator>
        <xsl:accumulator name="count" as="xs:integer" initial-value="0" streamable="yes">
          <xsl:accumulator-rule match="item" select="$value + 1"/>
        </xsl:accumulator>
        <xsl:accumulator name="doc" as="xs:string*" initial-value="'init'" streamable="yes">
          <xsl:accumulator-rule match="/" phase="start" select="($value, 'start')"/>
          <xsl:accumulator-rule match="item" select="($value, 'item')"/>
          <xsl:accumulator-rule match="/" phase="end" select="($value, 'end')"/>
        </xsl:accumulator>
        """;

    // The rule for the document node, and the input; each must stream.
    public static TheoryData<string, string> Streamed => new()
    {
        // The shape of xslt#461.
        { """<xsl:apply-templates/><sum><xsl:value-of select="accumulator-after('sum')"/></sum>""", Items },
        { """<xsl:apply-templates/><n>{accumulator-after('count')}</n>""", Nested },
        { """<xsl:apply-templates/><r sum="{accumulator-after('sum')}" n="{accumulator-after('count')}"/>""", Items },
        { """<out><xsl:apply-templates/></out><sum><xsl:value-of select="accumulator-after('sum')"/></sum>""", Items },
        { """<out><xsl:apply-templates/><sum><xsl:value-of select="accumulator-after('sum') * 2"/></sum></out>""", Items },
        { """<xsl:apply-templates select="root"/><xsl:sequence select="accumulator-after('sum')"/>""", Items },
        // The rules for "/" fire at the start and the end of the pass.
        { """<xsl:apply-templates/><d><xsl:value-of select="accumulator-after('doc')"/></d>""", Nested },
        // The values of each element, read in its own rule while the pass drops those it has passed.
        { """<xsl:apply-templates/><total><xsl:value-of select="accumulator-after('sum')"/></total>""", Nested },
        // No instruction reads the document: the call does, so the pass runs before the body.
        { """<sum><xsl:value-of select="accumulator-after('sum')"/></sum>""", Items },
        { """<r n="{accumulator-after('count')}">{accumulator-after('sum')}</r>""", Nested },
        { """<d>{accumulator-after('doc')}</d>""", Nested },
        // The value before it is the one recorded at its start.
        { """<b>{accumulator-before('doc')}</b><a>{accumulator-after('sum')}</a>""", Items },
    };

    // Where the value after the document is not yet known, or not known to have been reached.
    // These are read into a tree, as before; each must still give what the tree gives.
    public static TheoryData<string, string> NotStreamed => new()
    {
        { """<xsl:if test="exists(.)"><xsl:apply-templates/></xsl:if><sum><xsl:value-of select="accumulator-after('sum')"/></sum>""", Items },
        { """<xsl:apply-templates/><b><xsl:value-of select="accumulator-before('doc')"/></b>""", Items },
        { """<sum>{accumulator-after('sum')}</sum><n>{count(root/item)}</n>""", Items },
    };

    [Theory]
    [MemberData(nameof(Streamed))]
    public async Task The_document_value_after_its_children_streams(string body, string input)
    {
        var stylesheet = Stylesheet(body);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue("the pass records the document node's value at its end");
    }

    [Theory]
    [MemberData(nameof(NotStreamed))]
    public async Task Other_document_values_are_answered_from_a_tree(string body, string input)
    {
        var stylesheet = Stylesheet(body);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeFalse();
    }

    [Fact]
    public async Task The_report_gives_the_sum()
    {
        var input = new StringBuilder("<root>");
        for (var i = 0; i < 10_000; i++)
            input.Append("<item>5</item>");
        input.Append("</root>");

        var streamed = await RunAsync(Stylesheet("""<xsl:apply-templates/><sum><xsl:value-of select="accumulator-after('sum')"/></sum>"""),
            input.ToString(), disableStreaming: false);

        streamed.Output.Should().Contain("<sum>50000</sum>");
        streamed.Streamed.Should().BeTrue();
    }

    [Theory]
    [InlineData(Items)]
    [InlineData(Nested)]
    public async Task Each_element_still_has_its_own_values(string input)
    {
        // The values of the node in hand, read in its own rule, before and after its children,
        // while the pass drops the values of the nodes it has passed.
        var stylesheet = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
              <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="#all"/>
              {{Accumulators}}
              <xsl:template match="/"><xsl:apply-templates/><total>{accumulator-after('sum')}</total></xsl:template>
              <xsl:template match="item|group"><e b="{accumulator-before('sum')}" n="{accumulator-before('count')}"><xsl:apply-templates/><a>{accumulator-after('sum')}</a></e></xsl:template>
            </xsl:stylesheet>
            """;

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
    }

    [Theory]
    [MemberData(nameof(Streamed))]
    [MemberData(nameof(NotStreamed))]
    public async Task Source_document_answers_as_on_a_tree(string body, string input)
    {
        // The same document-level decision serves xsl:source-document.
        var dir = Directory.CreateTempSubdirectory("phx-docacc").FullName;
        try
        {
            var inputPath = Path.Combine(dir, "in.xml");
            await File.WriteAllTextAsync(inputPath, input);
            string Stylesheet(string streamable) => $$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                    xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
                  <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="#all"/>
                  {{Accumulators}}
                  <xsl:template name="main"><xsl:source-document streamable="{{streamable}}" href="in.xml" use-accumulators="#all">{{body}}</xsl:source-document></xsl:template>
                </xsl:stylesheet>
                """;

            var tree = await RunNamedAsync(Stylesheet("no"), dir);
            var streamed = await RunNamedAsync(Stylesheet("yes"), dir);

            streamed.Should().Be(tree);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Use_accumulators_all_on_a_streamed_source_document_applies_every_accumulator()
    {
        // With #all, the streaming pass ran no accumulator at all, and a streamed node, with no
        // subtree to compute from afterwards, reported the values of an empty tree.
        var dir = Directory.CreateTempSubdirectory("phx-docacc").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "in.xml"), Nested);
            string Stylesheet(string streamable) => $$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                    xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
                  <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
                  {{Accumulators}}
                  <xsl:template name="main"><xsl:source-document streamable="{{streamable}}" href="in.xml" use-accumulators="#all"><xsl:apply-templates/></xsl:source-document></xsl:template>
                  <xsl:template match="item"><i n="{accumulator-before('count')}"><xsl:apply-templates/><a>{accumulator-after('sum')}</a></i></xsl:template>
                </xsl:stylesheet>
                """;

            var tree = await RunNamedAsync(Stylesheet("no"), dir);
            var streamed = await RunNamedAsync(Stylesheet("yes"), dir);

            tree.Should().Contain("""<i n="3"><a>7</a></i>""");
            streamed.Should().Be(tree);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<string> RunNamedAsync(string stylesheet, string dir)
    {
        var path = Path.Combine(dir, "s.xsl");
        await File.WriteAllTextAsync(path, stylesheet);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet, new Uri(path));
        transformer.SetInitialTemplate("main");
        return await transformer.TransformAsync("<dummy/>");
    }

    private static string Stylesheet(string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
          <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="#all"/>
          {{Accumulators}}
          <xsl:template match="/">{{body}}</xsl:template>
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
