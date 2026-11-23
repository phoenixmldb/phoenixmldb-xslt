using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Three W3C cases raised a false XTSE3430 (streamable-077, evaluate-046, si-group-051); behind
/// each was a defect in the analysis or at run time.
/// </summary>
public sealed class StreamingD1FixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-d1-" + Guid.NewGuid().ToString("N"));

    public StreamingD1FixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static async Task<string> RunAsync(string stylesheet, string input, bool tree = false, bool strict = false)
    {
        var t = new XsltTransformer { DisableStreaming = tree, StrictStreamability = strict };
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(input)).Trim();
    }

    private const string Head = """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes"><xsl:output omit-xml-declaration="yes"/>""";

    /// <summary>
    /// "1 to count(.//*)" is a sequence of integers, not a crawl; and the range reads the matched
    /// element's descendants, so the rule cannot run on the unread element. The range was
    /// motionless, and the streamed run counted no descendants (W3C streamable-077).
    /// </summary>
    [Fact]
    public async Task A_range_over_a_count_of_descendants_is_streamable_and_counts_them()
    {
        var input = Path.Combine(_dir, "items.xml");
        await File.WriteAllTextAsync(input, "<r><item><a><b/></a></item><item><c/></item></r>", TestContext.Current.CancellationToken);
        var t = new XsltTransformer { StrictStreamability = true };
        await t.LoadStylesheetAsync(Head + $$"""
            <xsl:mode name="s" streamable="yes"/>
            <xsl:template name="main"><out><xsl:source-document streamable="yes" href="{{new Uri(input).AbsoluteUri}}"><xsl:apply-templates select="." mode="s"/></xsl:source-document></out></xsl:template>
            <xsl:template match="item" mode="s"><n><xsl:for-each select="1 to count(.//*)"><xsl:value-of select="concat(., ';')"/></xsl:for-each></n></xsl:template>
            <xsl:template match="text()" mode="s"/>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        (await t.TransformAsync((string?)null)).Trim().Should().Be("<out><n>1;2;</n><n>1;</n></out>");
    }

    /// <summary>
    /// A pattern with two predicates on an attribute step: the candidates of the predicates were
    /// taken from the element's children, which do not hold an attribute, so it never matched
    /// (W3C evaluate-046).
    /// </summary>
    [Theory]
    [InlineData("""*/@*[starts-with(local-name(), '_')][namespace-uri() = '']""")]
    [InlineData("""@*[namespace-uri() = ''][starts-with(local-name(), '_')]""")]
    [InlineData("""@*[starts-with(local-name(), '_')][1]""")]
    public async Task An_attribute_pattern_with_two_predicates_matches(string pattern)
        => (await RunAsync(Head + $$"""
                <xsl:mode on-no-match="shallow-copy"/>
                <xsl:template match="{{pattern}}"><xsl:attribute name="{substring(local-name(), 2)}" select="'X'"/></xsl:template>
                </xsl:stylesheet>
                """, """<r><m _a="1"/></r>""", tree: true))
            .Should().Be("""<r><m a="X"/></r>""");

    /// <summary>
    /// A streamed element's attributes had no parent, so ".." from an attribute was empty and a
    /// template for it that read ../accumulator-before() got nothing (W3C evaluate-046).
    /// </summary>
    [Fact]
    public async Task A_streamed_attribute_has_its_element_as_parent()
    {
        var stylesheet = Head + """
            <xsl:mode streamable="yes" on-no-match="shallow-copy" use-accumulators="n"/>
            <xsl:accumulator name="n" initial-value="0" streamable="yes"><xsl:accumulator-rule match="*" phase="end" select="$value + 1"/></xsl:accumulator>
            <xsl:template match="*/@_a"><xsl:attribute name="a" select="name(..), ../accumulator-before('n')"/></xsl:template>
            </xsl:stylesheet>
            """;
        const string Input = """<r><x/><y/><m _a="1"/></r>""";
        (await RunAsync(stylesheet, Input)).Should().Be("""<r><x/><y/><m a="m 2"/></r>""");
        (await RunAsync(stylesheet, Input, tree: true)).Should().Be("""<r><x/><y/><m a="m 2"/></r>""");
    }

    /// <summary>
    /// xsl:analyze-string and xsl:evaluate follow the general rules (§19.8.4.3, §19.8.4.16); the
    /// analysis did not model them, so the strict check refused any rule that held one.
    /// </summary>
    [Fact]
    public async Task Analyze_string_and_evaluate_over_attributes_are_streamable()
        => (await RunAsync(Head + """
                <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
                <xsl:template match="m"><o><xsl:analyze-string select="@a" regex="[0-9]+"><xsl:matching-substring><xsl:evaluate xpath="'$p * 2'" with-params="map{ QName('', 'p'): xs:integer(.) }"/></xsl:matching-substring></xsl:analyze-string></o></xsl:template>
                </xsl:stylesheet>
                """, """<r><m a="x12y"/></r>""", strict: true))
            .Should().Be("<o>24</o>");

    /// <summary>§19.8.4.16: a streamed node cannot be the context item of the dynamic expression.</summary>
    [Fact]
    public async Task Evaluate_with_a_streamed_context_item_is_XTSE3430()
    {
        var run = () => RunAsync(Head + """
            <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
            <xsl:template match="m"><o><xsl:evaluate xpath="'name()'" context-item="."/></o></xsl:template>
            </xsl:stylesheet>
            """, "<r><m/></r>");
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE3430");
    }

    private async Task<string> GroupedAsync(string population)
    {
        var docs = Path.Combine(_dir, "t.xml");
        await File.WriteAllTextAsync(docs, """<ts><t d="1" v="a"/><t d="2" v="b"/></ts>""", TestContext.Current.CancellationToken);
        return await RunAsync(Head + $$"""
            <xsl:mode streamable="yes"/>
            <xsl:template match="r">
              <out><xsl:for-each-group select="{{population}}" group-adjacent="@k">
                <g><xsl:source-document streamable="yes" href="{{new Uri(docs).AbsoluteUri}}"><xsl:for-each select="//t[@d = current-group()[1]/@d]">{@v}</xsl:for-each></xsl:source-document></g>
              </xsl:for-each-group></out>
            </xsl:template>
            </xsl:stylesheet>
            """, """<r><o k="1" d="1"/><o k="2" d="2"/></r>""", strict: true);
    }

    /// <summary>
    /// In an xsl:source-document inside a group of copied nodes, current-group() is held in
    /// memory and a predicate that reads it does not read the streamed document (W3C
    /// si-group-051); with a group of streamed nodes it is XTSE3430 (si-group-052).
    /// </summary>
    [Fact]
    public async Task A_group_of_copies_may_be_read_in_a_nested_streamed_document()
        => (await GroupedAsync("o/copy-of()")).Should().Be("<out><g>a</g><g>b</g></out>");

    [Fact]
    public async Task A_group_of_streamed_nodes_may_not()
    {
        var run = () => GroupedAsync("o");
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE3430");
    }
}
