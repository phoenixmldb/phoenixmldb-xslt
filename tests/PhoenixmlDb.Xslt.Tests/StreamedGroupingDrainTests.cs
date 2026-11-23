using System.Globalization;
using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed grouping inside the copy of the root element (BUGS #140, Martin Honnen's xslt#575
/// stylesheet). Its input streamed, but its whole result waited for the end of the input before
/// any of it reached the writer, and every group member stayed in the transform's store: the
/// memory grew with the input.
/// </summary>
public sealed class StreamedGroupingDrainTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-drain").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Head = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
          <xsl:template match="item/category"/>
        """;

    private const string MartinsRule = """
          <xsl:template match="root">
            <xsl:copy>
              <xsl:for-each-group select="item!copy-of()" group-adjacent="category">
                <category name="{current-grouping-key()}"><xsl:apply-templates select="current-group()"/></category>
              </xsl:for-each-group>
            </xsl:copy>
          </xsl:template>
        """;

    private static string Items(int count) => "<root a=\"A\">" + string.Concat(Enumerable.Range(1, count).Select(i =>
        $"<item><name>item {i}</name><category>cat{(i + 2) / 3}</category></item>")) + "</root>";

    /// <summary>
    /// An input stream that says how much of it was read, and gives at most 4K at a time, so
    /// that what was read follows what was asked for and not the size of a reader's buffer.
    /// </summary>
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private const int Chunk = 4096;

        public long MostRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Note(base.Read(buffer, offset, Math.Min(count, Chunk)));
        public override int Read(Span<byte> buffer) => Note(base.Read(buffer[..Math.Min(buffer.Length, Chunk)]));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Note(await base.ReadAsync(buffer.AsMemory(offset, Math.Min(count, Chunk)), cancellationToken).ConfigureAwait(false));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Note(await base.ReadAsync(buffer[..Math.Min(buffer.Length, Chunk)], cancellationToken).ConfigureAwait(false));

        private int Note(int read)
        {
            MostRead = Position;
            return read;
        }
    }

    /// <summary>A writer that notes how much of the input was read when the first text reached it.</summary>
    private sealed class FirstWriteWriter(CountingStream input) : StringWriter(CultureInfo.InvariantCulture)
    {
        public long InputReadAtFirstWrite { get; private set; } = -1;

        private void Note()
        {
            if (InputReadAtFirstWrite < 0)
                InputReadAtFirstWrite = input.MostRead;
        }

        public override void Write(char value) { Note(); base.Write(value); }
        public override void Write(string? value) { Note(); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Note(); base.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { Note(); base.Write(buffer); }
        public override Task WriteAsync(char value) { Note(); return base.WriteAsync(value); }
        public override Task WriteAsync(string? value) { Note(); return base.WriteAsync(value); }
        public override Task WriteAsync(char[] buffer, int index, int count) { Note(); return base.WriteAsync(buffer, index, count); }
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Note();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static async Task<(string Result, long InputReadAtFirstWrite, long InputLength)> StreamToWriter(string rules, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"{Head}{rules}</xsl:stylesheet>");
        var bytes = Encoding.UTF8.GetBytes(input);
        using var stream = new CountingStream(bytes);
        using var writer = new FirstWriteWriter(stream);
        await t.TransformAsync(stream, writer);
        return (writer.ToString(), writer.InputReadAtFirstWrite, bytes.Length);
    }

    private static async Task<string> Tree(string rules, string input)
    {
        var t = new XsltTransformer { DisableStreaming = true };
        await t.LoadStylesheetAsync($"{Head}{rules}</xsl:stylesheet>");
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task The_result_reaches_the_writer_before_the_input_ends()
    {
        var input = Items(6000);
        var (result, readAtFirstWrite, length) = await StreamToWriter(MartinsRule, input);

        result.Should().Be(await Tree(MartinsRule, input));
        result.Should().StartWith("<root><category name=\"cat1\"><item><name>item 1</name></item>");
        // The first text reached the writer when a small part of the input had been read; it
        // came only after all of it.
        readAtFirstWrite.Should().BePositive();
        readAtFirstWrite.Should().BeLessThan(length / 4, $"the input is {length} bytes");
    }

    public static TheoryData<string> Shapes => new()
    {
        // An attribute of the copy from the first group, before any child: in the start tag.
        """
        <xsl:template match="root">
          <xsl:copy>
            <xsl:for-each-group select="item!copy-of()" group-adjacent="category">
              <xsl:if test="current-grouping-key() = 'cat1'"><xsl:attribute name="first" select="current-grouping-key()"/></xsl:if>
              <g k="{current-grouping-key()}">{count(current-group())}</g>
            </xsl:for-each-group>
          </xsl:copy>
        </xsl:template>
        """,
        // Text and an element before the grouping, and after it.
        """
        <xsl:template match="root">
          <xsl:copy>
            <head n="{@a}"/>text
            <xsl:for-each-group select="item!copy-of()" group-starting-with="item[ends-with(name, '1')]">
              <g>{current-group()/name}</g>
            </xsl:for-each-group>
          </xsl:copy>
        </xsl:template>
        """,
        // A namespace on the copy, declared by the rule.
        """
        <xsl:template match="root">
          <xsl:copy>
            <xsl:namespace name="p" select="'urn:p'"/>
            <xsl:for-each-group select="item!copy-of()" group-ending-with="item[ends-with(name, '0')]">
              <p:g xmlns:p="urn:p">{current-group()/name}</p:g>
            </xsl:for-each-group>
          </xsl:copy>
        </xsl:template>
        """,
        // Not the shape: the copy is inside xsl:if, and its result is held to the end as before.
        """
        <xsl:template match="root">
          <xsl:if test="true()">
            <xsl:copy>
              <xsl:for-each-group select="item!copy-of()" group-adjacent="category"><g>{current-grouping-key()}</g></xsl:for-each-group>
            </xsl:copy>
          </xsl:if>
        </xsl:template>
        """,
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_streamed_result_sent_on_between_groups_is_the_tree_result(string rules)
    {
        var input = Items(40);
        (await StreamToWriter(rules, input)).Result.Should().Be(await Tree(rules, input));
    }

    [Fact]
    public async Task An_attribute_after_the_groups_is_still_XTDE0410()
    {
        const string rules = """
            <xsl:template match="root">
              <xsl:copy>
                <xsl:for-each-group select="item!copy-of()" group-adjacent="category"><g/></xsl:for-each-group>
                <xsl:attribute name="late" select="1"/>
              </xsl:copy>
            </xsl:template>
            """;
        var input = Items(40);
        var tree = () => Tree(rules, input);
        await tree.Should().ThrowAsync<XsltException>().WithMessage("*XTDE0410*");
        var streamed = () => StreamToWriter(rules, input);
        await streamed.Should().ThrowAsync<XsltException>().WithMessage("*XTDE0410*");
    }

    [Fact]
    public async Task The_members_of_the_groups_do_not_stay_in_the_store()
    {
        // On a store of the test's own: a streamed source document grouped the same way.
        await File.WriteAllTextAsync(Path.Combine(_dir, "d.xml"), Items(2000), TestContext.Current.CancellationToken);
        const string sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template match="/">
                <xsl:source-document streamable="yes" href="d.xml"><xsl:apply-templates mode="s"/></xsl:source-document>
              </xsl:template>
              <xsl:template match="root" mode="s">
                <xsl:for-each-group select="item!copy-of()" group-adjacent="category">{current-grouping-key()};</xsl:for-each-group>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var store = new XdmInMemoryStore();
        var doc = new XdmDocument
        {
            StringValueResolver = store.StringValueResolver,
            Id = store.NextId(),
            Document = new DocumentId(1),
            Children = [],
        };
        store.Register(doc);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(sheet, new Uri(Path.Combine(_dir, "main.xsl")));

        var result = await t.TransformAsync(Xdm.XdmSequence.OfNode(doc, store));

        result.Should().StartWith("cat1;cat2;").And.EndWith("cat667;");
        // 2000 members of 5 nodes each stayed in it.
        store.GetAllNodes().Count().Should().BeLessThan(200);
    }
}
