using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.LastTransformStreamed"/> tells whether the last transformation
/// streamed its input. A streamable stylesheet does not always stream: the engine reads the input
/// whole for an entry at a named template, and for a rule for the document node that the
/// streaming pass cannot answer. The CLI said "(streaming)" for those runs too (xslt#463).
/// </summary>
public class LastTransformStreamedTests
{
    private const string Input = "<root><item>1</item><item>2</item></root>";

    private const string StreamableSum = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:mode streamable="yes"/>
          <xsl:template match="/"><sum><xsl:value-of select="sum(root/item)"/></sum></xsl:template>
          <xsl:template name="go"><go/></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task A_streamed_transformation_says_so()
    {
        var transformer = await Load(StreamableSum);

        var result = await TransformStreamAsync(transformer);

        result.Should().Contain("<sum>3</sum>");
        transformer.LastTransformStreamed.Should().BeTrue();
    }

    [Fact]
    public async Task A_string_input_that_streams_says_so()
    {
        var transformer = await Load(StreamableSum);

        var result = await transformer.TransformAsync(Input);

        result.Should().Contain("<sum>3</sum>");
        transformer.LastTransformStreamed.Should().BeTrue();
    }

    [Fact]
    public async Task An_entry_at_a_named_template_is_not_streamed()
    {
        var transformer = await Load(StreamableSum);
        transformer.SetInitialTemplate("go");

        var result = await TransformStreamAsync(transformer);

        result.Should().Contain("<go/>");
        transformer.LastTransformStreamed.Should().BeFalse();
    }

    [Fact]
    public async Task A_document_rule_the_streaming_pass_cannot_answer_is_not_streamed()
    {
        // A for expression over the input is not streamed at the document level.
        var transformer = await Load("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:mode streamable="yes"/>
              <xsl:template match="/"><sum><xsl:value-of select="sum(for $i in root/item return xs:decimal($i))"/></sum></xsl:template>
            </xsl:stylesheet>
            """);

        var result = await TransformStreamAsync(transformer);

        result.Should().Contain("<sum>3</sum>");
        transformer.LastTransformStreamed.Should().BeFalse();
    }

    [Fact]
    public async Task With_streaming_disabled_it_is_not_streamed()
    {
        var transformer = await Load(StreamableSum);
        transformer.DisableStreaming = true;

        using var input = new StringReader(Input);
        using var output = new StringWriter();
        await transformer.TransformAsync(input, output);

        transformer.LastTransformStreamed.Should().BeFalse();
    }

    [Fact]
    public async Task A_stylesheet_with_no_streamable_mode_is_not_streamed()
    {
        var transformer = await Load("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><sum><xsl:value-of select="sum(root/item)"/></sum></xsl:template>
            </xsl:stylesheet>
            """);

        await transformer.TransformAsync(Input);

        transformer.LastTransformStreamed.Should().BeFalse();
    }

    [Fact]
    public async Task Each_transformation_sets_it_afresh()
    {
        var transformer = await Load(StreamableSum);
        await TransformStreamAsync(transformer);
        transformer.LastTransformStreamed.Should().BeTrue();

        transformer.DisableStreaming = true;
        await TransformStreamAsync(transformer);

        transformer.LastTransformStreamed.Should().BeFalse();
    }

    private static async Task<string> TransformStreamAsync(XsltTransformer transformer)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Input));
        using var output = new StringWriter();
        await transformer.TransformAsync(input, output);
        return output.ToString();
    }

    private static async Task<XsltTransformer> Load(string stylesheet)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return transformer;
    }
}
