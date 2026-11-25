using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A stylesheet with a streamable mode whose input is read into memory, in place of streaming it,
/// said nothing about it (xslt#582). With <see cref="XsltTransformer.StreamingFallbackWarnings"/>
/// the warning listener is told why: the construct and its location. Under
/// <see cref="XsltTransformer.StrictStreamability"/>, a construct that the rules of XSLT 3.0 §19.8
/// find guaranteed-streamable but that this processor cannot evaluate while streaming is
/// phx:XTSE9430 (phx:XTDE9430 for xsl:source-document). And the value of an ancestor
/// (<c>string(..)</c>), which §19.8.1 makes free-ranging (climbing and absorption), streamed and
/// gave an empty string: it is now evaluated on a tree, and is XTSE3430 under the strict check.
/// </summary>
public sealed class StreamingFallbackReportTests
{
    private const string SourceUri = "file:///data/in/books.xml";

    private const string Books = """<books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book></books>""";

    // The value of an ancestor of the matched node: climbing and absorption (§19.8.1).
    public static TheoryData<string> AncestorValues => new()
    {
        """<xsl:template match="title"><t><xsl:value-of select="string(..)"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="data(..)"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="string(ancestor::book)"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="string(root())"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select=".."/></t></xsl:template>""",
        """<xsl:template match="title"><t v="{..}"/></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select=".. = 'A'"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="concat(.., '!')"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:copy-of select=".."/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select=".. || '!'"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="-.."/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select=".. cast as xs:string"/></t></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(AncestorValues))]
    public async Task The_value_of_an_ancestor_is_evaluated_on_a_tree(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, t => t.DisableStreaming = true);
        var run = await RunAsync(stylesheet);

        tree.Output.Should().Contain("<t");
        run.Output.Should().Be(tree.Output);
        run.Streamed.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AncestorValues))]
    public async Task The_value_of_an_ancestor_is_XTSE3430_under_the_strict_check(string rules)
    {
        var act = () => RunAsync(Stylesheet(rules), t => t.StrictStreamability = true);

        (await act.Should().ThrowAsync<XsltException>()).Which.ErrorCode.Should().Be("XTSE3430");
    }

    // An ancestor that is only inspected, or whose attribute is read: these stream.
    public static TheoryData<string> AncestorInspections => new()
    {
        """<xsl:template match="title"><t><xsl:value-of select="name(..)"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="string(../@price)"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="count(ancestor::*)"/></t></xsl:template>""",
        """<xsl:template match="title"><t><xsl:value-of select="exists(..)"/></t></xsl:template>""",
        """<xsl:template match="title"><t p="{ancestor::book/@price}"/></xsl:template>""",
        // A node comparison inspects its operands (§19.8.8.6).
        """<xsl:template match="title"><t><xsl:value-of select=".. is ancestor::book"/></t></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(AncestorInspections))]
    public async Task An_inspected_ancestor_streams(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, t => t.DisableStreaming = true);
        var run = await RunAsync(stylesheet, t => t.StrictStreamability = true);

        run.Output.Should().Be(tree.Output);
        run.Streamed.Should().BeTrue();
    }

    // Read into memory by default, and not an error: the check is not certain of its verdict.
    private const string AncestorValue = """<xsl:template match="title"><t><xsl:value-of select="string(..)"/></t></xsl:template>""";

    [Fact]
    public async Task A_read_into_memory_is_reported_with_its_construct_and_location()
    {
        var run = await RunAsync(Stylesheet(AncestorValue), t => t.StreamingFallbackWarnings = true);

        run.Streamed.Should().BeFalse();
        run.Warnings.Should().ContainSingle().Which.Should().StartWith("XSLT streaming: the input was read into memory: ")
            .And.Contain(" at line 3, column ").And.Contain("(the template match=\"title\")")
            .And.EndWith(", which this processor cannot evaluate while streaming.");
    }

    [Fact]
    public async Task A_run_that_starts_at_a_named_template_is_reported()
    {
        var run = await RunAsync(Stylesheet("""<xsl:template name="go"><go/></xsl:template>"""), t =>
        {
            t.StreamingFallbackWarnings = true;
            t.SetInitialTemplate("go");
        });

        run.Output.Should().Contain("<go");
        run.Warnings.Should().Equal(
            "XSLT streaming: the input was read into memory: the transformation starts at the named template go, not at a template rule of the streamable mode.");
    }

    [Theory]
    [InlineData(false, false)] // not asked for
    [InlineData(true, true)]   // a tree asked for
    public async Task Nothing_is_reported_unless_asked_for(bool warnings, bool disableStreaming)
    {
        var run = await RunAsync(Stylesheet(AncestorValue), t =>
        {
            t.StreamingFallbackWarnings = warnings;
            t.DisableStreaming = disableStreaming;
        });

        run.Streamed.Should().BeFalse();
        run.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_streams_reports_nothing()
    {
        var run = await RunAsync(Stylesheet("""<xsl:template match="book"><b p="{@price}"/></xsl:template>"""),
            t => t.StreamingFallbackWarnings = true);

        run.Streamed.Should().BeTrue();
        run.Warnings.Should().BeEmpty();
    }

    // §19.8 finds this guaranteed-streamable: "/" in the loop is the root of a snapshot, which is
    // grounded. The check that sends a rule that reads outside its match to the tree takes it as a
    // read outside the match.
    private const string AcceptedButReadWhole =
        """<xsl:template match="/"><out><xsl:for-each select="snapshot(books/book)"><b n="{count(/*)}"/></xsl:for-each></out></xsl:template>""";

    [Fact]
    public async Task Under_the_strict_check_a_construct_this_processor_cannot_stream_is_phx_XTSE9430()
    {
        var act = () => RunAsync(Stylesheet(AcceptedButReadWhole), t => t.StrictStreamability = true);

        var error = (await act.Should().ThrowAsync<XsltException>()).Which;
        error.ErrorCode.Should().Be("phx:XTSE9430");
        error.Message.Should().StartWith("phx:XTSE9430: The input cannot be streamed: ")
            .And.Contain("(the template match=\"/\") needs the whole input in memory, although the rules of XSLT 3.0 section 19.8 find it guaranteed streamable.");
        error.Location.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)] // the default
    [InlineData(true)]  // the strict check, with a buffer asked for
    public async Task Without_the_strict_check_the_input_is_read_into_memory(bool strictWithFallback)
    {
        var tree = await RunAsync(Stylesheet(AcceptedButReadWhole), t => t.DisableStreaming = true);
        var run = await RunAsync(Stylesheet(AcceptedButReadWhole), t =>
        {
            t.StrictStreamability = strictWithFallback;
            t.StreamingFallback = strictWithFallback;
        });

        run.Output.Should().Be(tree.Output);
        run.Streamed.Should().BeFalse();
    }

    [Fact]
    public async Task The_W3C_harness_reads_the_input_into_memory_under_the_strict_check()
    {
        var tree = await RunAsync(Stylesheet(AcceptedButReadWhole), t => t.DisableStreaming = true);
        var run = await RunAsync(Stylesheet(AcceptedButReadWhole), t =>
        {
            t.StrictStreamability = true;
            t.ReadWholeUnderStrictStreamability = true;
        });

        run.Output.Should().Be(tree.Output);
        run.Streamed.Should().BeFalse();
    }

    private static string Stylesheet(string rules) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
          {{rules}}
        </xsl:stylesheet>
        """;

    private static async Task<(string Output, bool Streamed, List<string> Warnings)> RunAsync(string stylesheet,
        Action<XsltTransformer>? configure = null)
    {
        var warnings = new List<string>();
        var transformer = new XsltTransformer { WarningListener = warnings.Add };
        configure?.Invoke(transformer);
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetSourceDocumentUri(new Uri(SourceUri));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Books));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed, warnings);
    }
}
