using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.SetGlobalContextItem(string?, Uri?)"/> and
/// <see cref="XsltTransformer.SetGlobalContextItem(object?)"/> supply a global context item
/// separate from the source (XSLT 3.0 §2.3.2, §5.4.1). A streamed source is not the global context
/// item, so with a streamable initial mode:
/// <list type="bullet">
/// <item><c>xsl:global-context-item use="required"</c> without a separate item is XTDE3086, raised
/// before the input is read or output is written;</item>
/// <item>with a separate item the globals read it, whether the engine streams the input or reads it
/// into a tree (both routes dropped it: the streaming pass gave an absent focus, the tree the
/// source);</item>
/// <item><c>use="absent"</c> is satisfied (it was XTSE3088, counting the streamed source).</item>
/// </list>
/// With <see cref="XsltTransformer.DisableStreaming"/> the source is the global context item,
/// unless a separate item is set.
/// </summary>
public class SetGlobalContextItemTests
{
    private const string Input = "<items><item>5</item><item>5</item></items>";
    private const string Separate = "<items><item>7</item><item>7</item><item>7</item></items>";

    // The rule for "/" sums a global, so the streaming pass cannot answer it: the input is read into a tree.
    private const string TreeRoute = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
          <xsl:global-context-item use="required"/>
          <xsl:param name="items" select="*/item"/>
          <xsl:mode on-no-match="deep-skip" streamable="yes"/>
          <xsl:output method="text"/>
          <xsl:template match="/">{sum($items)}</xsl:template>
        </xsl:stylesheet>
        """;

    // A rule per item that reads only its own item: the input is streamed.
    private const string StreamedRoute = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:global-context-item use="required"/>
          <xsl:variable name="n" select="count(*/item)"/>
          <xsl:mode on-no-match="shallow-skip" streamable="yes"/>
          <xsl:output method="text"/>
          <xsl:template match="item"><xsl:value-of select="$n * ."/>;</xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<XsltTransformer> Load(string stylesheet, bool disableStreaming = false)
    {
        var t = new XsltTransformer { DisableStreaming = disableStreaming };
        await t.LoadStylesheetAsync(stylesheet);
        return t;
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Required_with_a_streamed_source_and_no_separate_item_is_XTDE3086_naming_both_remedies()
    {
        var t = await Load(TreeRoute);

        var act = () => t.TransformAsync(Input);

        (await act.Should().ThrowAsync<Exception>()).Which.Message
            .Should().Contain("XTDE3086").And.Contain("--global-context-item").And.Contain("SetGlobalContextItem")
            .And.Contain("--no-stream").And.Contain("DisableStreaming");
    }

    [Fact]
    public async Task XTDE3086_is_raised_before_any_output_is_written()
    {
        var t = await Load(StreamedRoute.Replace("<xsl:output method=\"text\"/>", "<xsl:output method=\"xml\"/>", StringComparison.Ordinal));
        using var input = Bytes(Input);
        using var output = new MemoryStream();

        string? message = null;
        try
        {
            await t.TransformAsync(input, output);
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        message.Should().Contain("XTDE3086");
        output.Length.Should().Be(0);
    }

    [Fact]
    public async Task With_a_separate_item_the_tree_route_reads_it()
    {
        var t = await Load(TreeRoute);
        t.SetGlobalContextItem(Separate);

        (await t.TransformAsync(Input)).Should().Be("21");
    }

    [Fact]
    public async Task With_a_separate_item_the_streamed_route_reads_it_and_still_streams()
    {
        var t = await Load(StreamedRoute);
        t.SetGlobalContextItem(Separate);
        using var input = new StringReader(Input);
        using var output = new StringWriter();

        await t.TransformAsync(input, output);

        output.ToString().Should().Be("15;15;");
        t.LastTransformStreamed.Should().BeTrue();
    }

    [Fact]
    public async Task With_streaming_disabled_the_source_is_the_global_context_item()
        => (await (await Load(TreeRoute, disableStreaming: true)).TransformAsync(Input)).Should().Be("10");

    [Fact]
    public async Task With_streaming_disabled_a_separate_item_takes_the_place_of_the_source()
    {
        var t = await Load(TreeRoute, disableStreaming: true);
        t.SetGlobalContextItem(Separate);

        (await t.TransformAsync(Input)).Should().Be("21");
    }

    [Fact]
    public async Task Null_removes_the_separate_item()
    {
        var t = await Load(TreeRoute);
        t.SetGlobalContextItem(Separate);
        t.SetGlobalContextItem(null);

        var act = () => t.TransformAsync(Input);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3086");
    }

    [Fact]
    public async Task The_object_overload_takes_an_element()
    {
        var t = await Load(TreeRoute.Replace("select=\"*/item\"", "select=\"item\"", StringComparison.Ordinal));
        t.SetGlobalContextItem(XElement.Parse(Separate));

        (await t.TransformAsync(Input)).Should().Be("21");
    }

    [Fact]
    public async Task The_object_overload_takes_an_atomic_value_and_a_string_typed_as_object_is_an_xs_string()
    {
        var t = await Load("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" expand-text="yes">
              <xsl:global-context-item use="required"/>
              <xsl:variable name="g" select="(. instance of xs:string) || ':' || ."/>
              <xsl:mode on-no-match="deep-skip" streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/">{$g}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetGlobalContextItem((object)"<not-xml");

        (await t.TransformAsync(Input)).Should().Be("true:<not-xml");
    }

    [Fact]
    public async Task Use_absent_is_satisfied_by_a_streamed_source()
    {
        var t = await Load(StreamedRoute.Replace("use=\"required\"", "use=\"absent\"", StringComparison.Ordinal)
            .Replace("count(*/item)", "2", StringComparison.Ordinal));

        (await t.TransformAsync(Input)).Should().Be("10;10;");
    }

    [Fact]
    public async Task A_separate_item_is_checked_against_the_declared_type()
    {
        var t = await Load(TreeRoute.Replace("use=\"required\"", "use=\"required\" as=\"document-node(element(items))\"", StringComparison.Ordinal));
        t.SetGlobalContextItem("<other/>");

        var act = () => t.TransformAsync(Input);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0590");
    }

    [Fact]
    public void Malformed_XML_is_rejected_when_it_is_set()
    {
        var t = new XsltTransformer();

        var act = () => t.SetGlobalContextItem("<a>");

        act.Should().Throw<System.Xml.XmlException>();
    }
}
