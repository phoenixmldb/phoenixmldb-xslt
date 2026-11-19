using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// When the source is the streamed input of a streamable initial mode, there is no global context
/// item (XSLT 3.0 §2.3.2; §2.3.3 Note), so a global variable or parameter that reads the context
/// item raises XPDY0002, as Saxon-EE does. The engine read such an input into a tree when the
/// streaming pass could not answer the stylesheet, and then gave the globals the whole document as
/// their context item: Martin Honnen's global param select="*/item" summed to 50 where Saxon-EE
/// raises XPDY0002. <see cref="XsltTransformer.DisableStreaming"/> (the CLI's --no-stream) keeps
/// the document as the global context item.
/// </summary>
public class StreamedInputGlobalContextItemTests
{
    private const string Input = "<items><item>5</item><item>5</item><item>5</item><item>5</item></items>";

    private const string Martin = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:param name="items" select="*/item"/>
          <xsl:mode on-no-match="deep-skip" streamable="yes" use-accumulators="#all"/>
          <xsl:template match="/"><r><sum>{sum($items)}</sum><avg>{sum($items) div count($items)}</avg></r></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<XsltTransformer> Load(string stylesheet, bool disableStreaming = false)
    {
        var t = new XsltTransformer { DisableStreaming = disableStreaming };
        await t.LoadStylesheetAsync(stylesheet);
        return t;
    }

    private static MemoryStream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    public static TheoryData<string> Overloads => ["string", "TextReader", "Stream", "TextReader to TextWriter", "Stream to Stream"];

    private static async Task<string> Run(XsltTransformer t, string overload)
    {
        switch (overload)
        {
            case "string":
                return await t.TransformAsync(Input);
            case "TextReader":
            {
                using var r = new StringReader(Input);
                return await t.TransformAsync(r);
            }
            case "Stream":
            {
                using var i = Bytes(Input);
                return await t.TransformAsync(i);
            }
            case "TextReader to TextWriter":
            {
                using var r = new StringReader(Input);
                using var w = new StringWriter();
                await t.TransformAsync(r, w);
                return w.ToString();
            }
            default:
            {
                using var i = Bytes(Input);
                using var o = new MemoryStream();
                await t.TransformAsync(i, o);
                return Encoding.UTF8.GetString(o.ToArray());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task A_global_that_reads_the_context_item_of_a_streamed_input_is_XPDY0002(string overload)
    {
        var t = await Load(Martin);

        var act = () => Run(t, overload);

        (await act.Should().ThrowAsync<PhoenixmlDb.XQuery.Execution.XQueryRuntimeException>()).Which.ErrorCode.Should().Be("XPDY0002");
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task With_streaming_disabled_the_document_is_the_global_context_item(string overload)
        => (await Run(await Load(Martin, disableStreaming: true), overload)).Trim()
            .Should().Be("<r><sum>20</sum><avg>5</avg></r>");

    [Fact]
    public async Task A_mode_that_is_not_streamable_keeps_the_document_as_the_global_context_item()
        => (await (await Load(Martin.Replace(" streamable=\"yes\"", "", StringComparison.Ordinal))).TransformAsync(Input)).Trim()
            .Should().Be("<r><sum>20</sum><avg>5</avg></r>");

    [Fact]
    public async Task A_global_that_does_not_read_the_context_item_still_works_with_a_streamed_input()
        => (await (await Load(Martin.Replace("select=\"*/item\"", "select=\"(5, 5)\"", StringComparison.Ordinal))).TransformAsync(Input)).Trim()
            .Should().Be("<r><sum>10</sum><avg>5</avg></r>");

    [Fact]
    public async Task An_initial_template_entry_keeps_the_source_as_the_global_context_item()
    {
        var t = await Load(Martin.Replace(
            "<xsl:template match=\"/\">", "<xsl:template name=\"start\">", StringComparison.Ordinal));
        t.SetInitialTemplate("start");

        (await t.TransformAsync(Input)).Trim().Should().Be("<r><sum>20</sum><avg>5</avg></r>");
    }
}
