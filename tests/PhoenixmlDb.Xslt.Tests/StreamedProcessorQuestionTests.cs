using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// system-property(), function-available(), element-available(), type-available() and
/// available-system-properties() ask the processor, not the input (XSLT §20.3, §20.4). The
/// streamability planner had no case for them, so a rule for the document node that printed
/// system-property('xsl:product-name') after its xsl:apply-templates read the whole input into
/// memory: 1.3 GiB for a million items, with the same output. Each case must stream and give
/// what the tree gives.
/// </summary>
public sealed class StreamedProcessorQuestionTests
{
    private const string Items = "<root><item>5</item><item>2.5</item><item>-1</item></root>";

    public static TheoryData<string> Streamed => new()
    {
        // The report that found it: an accumulator total, then a comment naming the processor.
        """<xsl:apply-templates/><sum><xsl:value-of select="accumulator-after('sum')"/></sum><xsl:comment>Run with {system-property('xsl:product-name')} {system-property('xsl:product-version')}</xsl:comment>""",
        // No accumulator: the question alone made the difference.
        """<xsl:apply-templates/><x>{system-property('xsl:version')}</x>""",
        """<xsl:apply-templates/><x><xsl:value-of select="system-property('xsl:version')"/></x>""",
        """<x>{system-property('xsl:vendor')}</x><xsl:apply-templates/>""",
        """<xsl:apply-templates/><x>{function-available('fn:sum')} {function-available('fn:sum', 1)}</x>""",
        """<xsl:apply-templates/><x>{element-available('xsl:iterate')}</x>""",
        """<xsl:apply-templates/><x>{type-available('xs:integer')}</x>""",
        """<xsl:apply-templates/><x>{count(available-system-properties()) gt 0}</x>""",
        """<xsl:if test="function-available('fn:sum')"><xsl:apply-templates/></xsl:if>""",
    };

    [Theory]
    [MemberData(nameof(Streamed))]
    public async Task A_question_to_the_processor_does_not_read_the_input_whole(string body)
    {
        var stylesheet = Stylesheet(body);

        var tree = await RunAsync(stylesheet, Items, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Items, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue("the question reads nothing of the input");
    }

    [Fact]
    public async Task A_question_whose_argument_reads_the_input_answers_as_on_a_tree()
    {
        // The argument is atomized, as for any atomizing function: here it reads the
        // document's content, so its posture is that of string(root).
        var stylesheet = Stylesheet("""<x>{function-available('x' || string(root/item[1]))}</x>""");

        var tree = await RunAsync(stylesheet, Items, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Items, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
    }

    private static string Stylesheet(string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
          <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="#all"/>
          <xsl:accumulator name="sum" as="xs:decimal" initial-value="0" streamable="yes">
            <xsl:accumulator-rule match="item/text()" select="$value + xs:decimal(.)"/>
          </xsl:accumulator>
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
