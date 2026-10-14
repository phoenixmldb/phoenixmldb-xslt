using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:iterate with the streamed document node as its context and a path of child steps as its
/// select reads the input one selected element at a time. It used to read the whole input
/// into memory first.
/// </summary>
public class StreamedIterateAtDocumentLevelTests
{
    /// <summary>
    /// A document of <c>count</c> records that is made as it is read, and that counts what has
    /// been read from it: &lt;root&gt;&lt;item id="1"&gt;&lt;v&gt;1&lt;/v&gt;&lt;/item&gt;…&lt;/root&gt;.
    /// </summary>
    private sealed class GeneratedRecords(int count) : Stream
    {
        private int _next;
        private byte[] _pending = Encoding.UTF8.GetBytes("<root>");
        private int _offset;
        private bool _closed;

        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int length)
        {
            if (_offset == _pending.Length)
            {
                if (_next < count)
                {
                    _next++;
                    _pending = Encoding.UTF8.GetBytes($"<item id=\"{_next}\"><v>{_next}</v></item>");
                }
                else if (!_closed)
                {
                    _closed = true;
                    _pending = Encoding.UTF8.GetBytes("</root>");
                }
                else
                {
                    return 0;
                }
                _offset = 0;
            }
            var n = Math.Min(length, _pending.Length - _offset);
            Array.Copy(_pending, _offset, buffer, offset, n);
            _offset += n;
            BytesRead += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(string Output, long BytesRead)> RunAsync(string body, int records, bool tree = false)
    {
        var t = new XsltTransformer { DisableStreaming = tree };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        using var input = new GeneratedRecords(records);
        if (tree)
            return (await t.TransformAsync(input), input.BytesRead);
        // The overload that reads the input as it goes; the others read it all first.
        using var output = new StringWriter();
        await t.TransformAsync(input, output);
        return (output.ToString(), input.BytesRead);
    }

    [Fact]
    public async Task It_stops_reading_the_input_when_it_breaks()
    {
        // Two million records, about sixty megabytes. The loop ends at the third.
        var (output, read) = await RunAsync(
            "<out><xsl:iterate select=\"root/item\"><xsl:choose><xsl:when test=\"v = 3\"><xsl:break/></xsl:when><xsl:otherwise>{v};</xsl:otherwise></xsl:choose></xsl:iterate></out>",
            records: 2_000_000);

        output.Should().Be("<out>1;2;</out>");
        read.Should().BeLessThan(1_000_000, "the rest of the input is not needed and is not read");
    }

    [Theory]
    // a running total, with xsl:on-completion
    [InlineData("<out><xsl:iterate select=\"root/item\"><xsl:param name=\"sum\" select=\"0\"/><xsl:on-completion>{$sum}</xsl:on-completion><xsl:next-iteration><xsl:with-param name=\"sum\" select=\"$sum + v\"/></xsl:next-iteration></xsl:iterate></out>")]
    // output for each record, its position and its attribute
    [InlineData("<out><xsl:iterate select=\"root/item\"><i n=\"{position()}\" id=\"{@id}\">{v}</i></xsl:iterate></out>")]
    // a break with what came before it kept, and xsl:on-completion after a break
    [InlineData("<out><xsl:iterate select=\"root/item\"><xsl:on-completion>end</xsl:on-completion><xsl:choose><xsl:when test=\"@id = 4\"><xsl:break/></xsl:when><xsl:otherwise>{v},</xsl:otherwise></xsl:choose></xsl:iterate></out>")]
    // the ancestors of a record, and a deeper path
    [InlineData("<out><xsl:iterate select=\"root/item\">{name(..)}/{name()};</xsl:iterate></out>")]
    [InlineData("<out><xsl:iterate select=\"root/item/v\">{.}{count(ancestor::*)};</xsl:iterate></out>")]
    // nothing selected
    [InlineData("<out><xsl:iterate select=\"root/none\">x</xsl:iterate>.</out>")]
    [InlineData("<out><xsl:iterate select=\"other/item\">x</xsl:iterate>.</out>")]
    // not wrapped, and after other output
    [InlineData("<xsl:iterate select=\"root/item\">{v} </xsl:iterate>")]
    [InlineData("<a/><b><xsl:iterate select=\"root/item\">{v}</xsl:iterate></b><c/>")]
    public async Task It_gives_the_tree_result(string body)
    {
        var streamed = await RunAsync(body, records: 7);
        var tree = await RunAsync(body, records: 7, tree: true);
        streamed.Output.Should().Be(tree.Output);
    }

    [Theory]
    // adjacent records with the same key, the key and the size of each group
    [InlineData("<out><xsl:for-each-group select=\"root/item\" group-adjacent=\"@id idiv 3\"><g k=\"{current-grouping-key()}\" n=\"{count(current-group())}\">{current-group()/v}</g></xsl:for-each-group></out>")]
    // a group starts at a record that matches, and ends at one that matches
    [InlineData("<out><xsl:for-each-group select=\"root/item\" group-starting-with=\"item[@id mod 3 = 1]\"><g>{current-group()/@id}</g></xsl:for-each-group></out>")]
    [InlineData("<out><xsl:for-each-group select=\"root/item\" group-ending-with=\"item[@id mod 3 = 0]\"><g first=\"{@id}\">{sum(current-group()/v)}</g></xsl:for-each-group></out>")]
    // the members themselves, their ancestors, and nothing selected
    [InlineData("<out><xsl:for-each-group select=\"root/item\" group-adjacent=\"number(@id) le 4\"><g><xsl:copy-of select=\"current-group()[1]\"/>{name(..)}</g></xsl:for-each-group></out>")]
    [InlineData("<out><xsl:for-each-group select=\"root/none\" group-adjacent=\"@id\">x</xsl:for-each-group>.</out>")]
    [InlineData("<out><xsl:for-each-group select=\"root/item/v\" group-adjacent=\". idiv 4\">{current-group()};</xsl:for-each-group></out>")]
    public async Task A_grouping_gives_the_tree_result(string body)
    {
        var streamed = await RunAsync(body, records: 8);
        var tree = await RunAsync(body, records: 8, tree: true);
        streamed.Output.Should().Be(tree.Output);
    }

    [Fact]
    public async Task A_grouping_writes_its_first_groups_before_it_has_read_the_whole_input()
    {
        // Each group is one record. With the whole input read first, nothing is written until
        // the last record has been read.
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="/">
                <xsl:for-each-group select="root/item" group-adjacent="@id">
                  <xsl:message select="string(@id)"/>
                </xsl:for-each-group>
              </xsl:template>
            </xsl:stylesheet>
            """);
        using var input = new GeneratedRecords(200_000);
        long? readAtFirstGroup = null;
        t.MessageListener = (_, _) => readAtFirstGroup ??= input.BytesRead;
        using var output = new StringWriter();
        await t.TransformAsync(input, output);

        readAtFirstGroup.Should().NotBeNull();
        readAtFirstGroup!.Value.Should().BeLessThan(input.BytesRead / 10, "the first group is complete long before the input ends");
    }
}
