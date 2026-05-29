using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public sealed class StreamingOutputDrainTests
{
    /// <summary>
    /// On a streaming identity transform over a moderately large input, the engine's
    /// internal output buffer must not retain the entire serialized result. After the
    /// first hundred input elements have been processed, the StringBuilder peak
    /// observable from outside should be a small fraction of the cumulative output
    /// size. We verify by writing through a TextWriter sink and observing that the
    /// caller's TextWriter receives content INCREMENTALLY rather than all at once at
    /// the end.
    /// </summary>
    [Fact]
    public async Task StreamingIdentity_DrainsOutputIncrementally()
    {
        const int itemCount = 10_000;
        var inputXml = BuildItemsDocument(itemCount);
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
              <xsl:output method="xml" indent="no"/>
            </xsl:stylesheet>
            """;

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);

        using var sink = new IncrementalObservingWriter();
        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(inputXml));
        await transformer.TransformAsync(inputStream, sink);
        await sink.FlushAsync();

        sink.WriteCount.Should().BeGreaterThan(1,
            "incremental draining should cause more than one Write to the sink");
        sink.MaxBufferedChars.Should().BeLessThan(inputXml.Length,
            "no single Write should carry the entire result document");
        sink.TotalChars.Should().BeGreaterThan(itemCount * 10,
            "the full output (3 lines per item) should have been written to the sink");
    }

    private static string BuildItemsDocument(int n)
    {
        var sb = new StringBuilder(64 * n);
        sb.Append("<items>");
        for (int i = 0; i < n; i++)
            sb.Append("<item id=\"").Append(i).Append("\">v").Append(i).Append("</item>");
        sb.Append("</items>");
        return sb.ToString();
    }

    /// <summary>
    /// TextWriter that counts WriteAsync invocations and tracks the largest single
    /// payload size, so the test can prove output is drained incrementally rather
    /// than accumulated into one big write.
    /// </summary>
    private sealed class IncrementalObservingWriter : TextWriter
    {
        public int WriteCount { get; private set; }
        public int MaxBufferedChars { get; private set; }
        public long TotalChars { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value)
        {
            if (value == null) return;
            WriteCount++;
            if (value.Length > MaxBufferedChars) MaxBufferedChars = value.Length;
            TotalChars += value.Length;
        }

        public override void Write(char value)
        {
            WriteCount++;
            TotalChars++;
            if (1 > MaxBufferedChars) MaxBufferedChars = 1;
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;
    }
}
