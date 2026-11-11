using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The <see cref="TextReader"/> overloads read the input to its end before they transformed it,
/// so a streamable stylesheet held the whole input in memory: the CLI, which writes standard
/// output through them, grew from 152 MiB on a million items to 331 MiB on four million, where a
/// run written to a file through a <see cref="Stream"/> stayed at 100 MiB (xslt#464). With a
/// streamable initial mode they now stream from the reader.
/// </summary>
public class TextReaderStreamingTests
{
    private const int Items = 1_000;

    private const string StreamableSum = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:mode streamable="yes"/>
          <xsl:template match="/"><sum><xsl:value-of select="sum(root/item)"/></sum></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task A_streamable_stylesheet_streams_from_a_reader_that_returns_a_string()
    {
        var transformer = await Load(StreamableSum);
        using var input = new ItemsReader(Items);

        var result = await transformer.TransformAsync(input);

        result.Should().Contain($"<sum>{Items * 5}</sum>");
        input.ReadWhole.Should().BeFalse("a streamable stylesheet must not read the input to its end first");
    }

    [Fact]
    public async Task A_streamable_stylesheet_streams_from_a_reader_to_a_writer()
    {
        var transformer = await Load(StreamableSum);
        using var input = new ItemsReader(Items);
        using var output = new StringWriter();

        await transformer.TransformAsync(input, output);

        output.ToString().Should().Contain($"<sum>{Items * 5}</sum>");
        input.ReadWhole.Should().BeFalse("a streamable stylesheet must not read the input to its end first");
    }

    [Fact]
    public async Task With_streaming_disabled_the_input_is_read_whole()
    {
        var transformer = await Load(StreamableSum);
        transformer.DisableStreaming = true;
        using var input = new ItemsReader(Items);

        var result = await transformer.TransformAsync(input);

        result.Should().Contain($"<sum>{Items * 5}</sum>");
        input.ReadWhole.Should().BeTrue();
    }

    [Fact]
    public async Task A_stylesheet_that_does_not_stream_reads_the_input_whole()
    {
        var transformer = await Load("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><sum><xsl:value-of select="sum(root/item)"/></sum></xsl:template>
            </xsl:stylesheet>
            """);
        using var input = new ItemsReader(Items);

        var result = await transformer.TransformAsync(input);

        result.Should().Contain($"<sum>{Items * 5}</sum>");
        input.ReadWhole.Should().BeTrue();
    }

    private const string StreamableJson = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:mode streamable="yes"/>
          <xsl:output method="json" indent="no"/>
          <xsl:template match="/"><xsl:apply-templates select="root"/></xsl:template>
          <xsl:template match="root"><xsl:sequence select="array { item ! xs:integer(.) }"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"/></xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData("reader")]
    [InlineData("reader-to-writer")]
    [InlineData("stream")]
    public async Task A_streamed_json_result_reaches_the_caller(string overload)
    {
        // The JSON is serialized after the streaming pass, and with a writer it stayed in the
        // engine's buffer: the caller got no output at all (W3C si-fork-815).
        var transformer = await Load(StreamableJson);
        const string xml = "<root><item>1</item><item>2</item><item>3</item></root>";
        string result;
        switch (overload)
        {
            case "reader":
                result = await transformer.TransformAsync(new StringReader(xml), TestContext.Current.CancellationToken);
                break;
            case "reader-to-writer":
                using (var writer = new StringWriter())
                {
                    await transformer.TransformAsync(new StringReader(xml), writer, TestContext.Current.CancellationToken);
                    result = writer.ToString();
                }
                break;
            default:
                using (var input = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
                using (var writer = new StringWriter())
                {
                    await transformer.TransformAsync(input, writer, TestContext.Current.CancellationToken);
                    result = writer.ToString();
                }
                break;
        }
        result.Should().Be("[1,2,3]");
        transformer.LastTransformStreamed.Should().BeTrue();
    }

    private static async Task<XsltTransformer> Load(string stylesheet)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return transformer;
    }

    /// <summary>
    /// <c>&lt;root&gt;</c> with <c>&lt;item&gt;5&lt;/item&gt;</c> children, and a record of whether it was
    /// asked for all of it at once.
    /// </summary>
    private sealed class ItemsReader(int items) : TextReader
    {
        private const string Item = "<item>5</item>";
        private readonly StringReader _text = new("<root>" + new StringBuilder().Insert(0, Item, items) + "</root>");

        public bool ReadWhole { get; private set; }

        public override int Peek() => _text.Peek();

        public override int Read() => _text.Read();

        public override int Read(char[] buffer, int index, int count) => _text.Read(buffer, index, count);

        public override int Read(Span<char> buffer) => _text.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => _text.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(char[] buffer, int index, int count)
            => _text.ReadAsync(buffer, index, count);

        public override string ReadToEnd()
        {
            ReadWhole = true;
            return _text.ReadToEnd();
        }

        public override Task<string> ReadToEndAsync()
        {
            ReadWhole = true;
            return _text.ReadToEndAsync();
        }

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            ReadWhole = true;
            return _text.ReadToEndAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _text.Dispose();
            base.Dispose(disposing);
        }
    }
}
