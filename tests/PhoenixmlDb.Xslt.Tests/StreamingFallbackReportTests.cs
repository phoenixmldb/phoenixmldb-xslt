using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Ast;
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

    // The causes that are not a construct of the stylesheet give the warning only, also under the
    // strict check: the run does not start at the document node, or the caller asked for
    // validation or XInclude.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_that_starts_at_a_named_template_is_reported(bool strict)
    {
        var run = await RunAsync(Stylesheet("""<xsl:template name="go"><go/></xsl:template>"""), t =>
        {
            t.StreamingFallbackWarnings = true;
            t.StrictStreamability = strict;
            t.SetInitialTemplate("go");
        });

        run.Output.Should().Contain("<go");
        run.Warnings.Should().Equal(
            "XSLT streaming: the input was read into memory: the transformation starts at the named template go, not at a template rule of the streamable mode.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_that_starts_at_a_function_is_reported(bool strict)
    {
        var run = await RunAsync(Stylesheet("""<xsl:function name="f:go" xmlns:f="urn:f" visibility="public"><go/></xsl:function>"""), t =>
        {
            t.StreamingFallbackWarnings = true;
            t.StrictStreamability = strict;
            t.SetInitialFunction("go", "urn:f");
        });

        run.Output.Should().Contain("<go");
        run.Warnings.Should().ContainSingle().Which.Should().StartWith(
            "XSLT streaming: the input was read into memory: the transformation starts at the function ");
        run.Warnings[0].Should().EndWith("go, not at a template rule of the streamable mode.");
    }

    [Fact]
    public async Task A_run_with_no_input_reports_nothing()
    {
        var warnings = new List<string>();
        var transformer = new XsltTransformer { WarningListener = warnings.Add, StreamingFallbackWarnings = true };
        await transformer.LoadStylesheetAsync(Stylesheet("""<xsl:template name="go"><go/></xsl:template>"""));
        transformer.SetInitialTemplate("go");

        (await transformer.TransformAsync((string?)null)).Should().Contain("<go");
        warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XInclude_expansion_of_the_input_is_reported(bool strict)
    {
        var tree = await RunAsync(Stylesheet(Streams), t => t.DisableStreaming = true);
        var run = await RunAsync(Stylesheet(Streams), t =>
        {
            t.StreamingFallbackWarnings = true;
            t.StrictStreamability = strict;
            t.EnableXInclude();
        });

        run.Output.Should().Be(tree.Output);

        run.Warnings.Should().Equal(
            "XSLT streaming: the input was read into memory: XInclude expansion of the input (ExpandXInclude) reads it as a tree.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_of_the_input_is_reported(bool strict)
    {
        var tree = await RunAsync(Stylesheet(Streams), t => t.DisableStreaming = true);
        var run = await RunAsync(Stylesheet(Streams), t =>
        {
            t.StreamingFallbackWarnings = true;
            t.StrictStreamability = strict;
            t.SourceValidation = PhoenixmlDb.Xslt.Ast.ValidationMode.Lax;
        });

        run.Output.Should().Be(tree.Output);

        run.Warnings.Should().Equal(
            "XSLT streaming: the input was read into memory: the input is validated (SourceValidation Lax), and it is validated as a tree.");
    }

    // A rule for the document node that the streaming pass cannot evaluate: the comparison in the
    // select of xsl:attribute. §19.8 accepts it. (In an attribute value template of the literal
    // result element it streams, BUGS.md #174.)
    private const string DocumentRule = """<xsl:template match="/"><out><xsl:attribute name="r" select="/*/book/@price = (93, 7)"/></out></xsl:template>""";

    [Fact]
    public async Task A_rule_for_the_document_node_is_reported_with_the_instruction()
    {
        var run = await RunAsync(Stylesheet(DocumentRule), t => t.StreamingFallbackWarnings = true);

        run.Output.Should().Contain("r=\"true\"");
        run.Warnings.Should().ContainSingle().Which.Should().StartWith(
            "XSLT streaming: the input was read into memory: /*/book/@price at line 3, column ")
            .And.EndWith("(the template match=\"/\"), which this processor cannot evaluate while streaming.");
    }

    [Fact]
    public async Task A_rule_for_the_document_node_is_phx_XTSE9430_under_the_strict_check()
    {
        var act = () => RunAsync(Stylesheet(DocumentRule), t => t.StrictStreamability = true);

        (await act.Should().ThrowAsync<XsltException>()).Which.ErrorCode.Should().Be("phx:XTSE9430");
    }

    [Fact]
    public async Task A_document_of_xsl_source_document_read_into_memory_is_reported_once()
    {
        var dir = Directory.CreateTempSubdirectory("phx-read-whole").FullName;
        try
        {
            var href = new Uri(Path.Combine(dir, "t.xml")).AbsoluteUri;
            await File.WriteAllTextAsync(Path.Combine(dir, "t.xml"), """<ts><t d="1"/><t d="7"/></ts>""", TestContext.Current.CancellationToken);
            var stylesheet = $$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:template match="/"><out><xsl:for-each select="1 to 2"><xsl:source-document streamable="yes" href="{{href}}"><o><xsl:attribute name="r" select="/*/t/@d = (1, 9)"/></o></xsl:source-document></xsl:for-each></out></xsl:template>
                </xsl:stylesheet>
                """;

            var run = await RunAsync(stylesheet, t => t.StreamingFallbackWarnings = true, asText: true);
            var tree = await RunAsync(stylesheet, t =>
            {
                t.DisableStreaming = true;
                t.StrictStreamability = true;
            }, asText: true);

            run.Output.Should().Contain("""<o r="true"/><o r="true"/>""");
            run.Warnings.Should().ContainSingle().Which.Should().StartWith(
                $"XSLT streaming: the document {href} was read into memory: xsl:attribute at line 2, column ")
                .And.EndWith("(the body of xsl:source-document), which this processor cannot evaluate while streaming.");
            tree.Output.Should().Be(run.Output);
            tree.Warnings.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_message_names_an_instruction_as_a_stylesheet_writes_it()
    {
        static XsltSequenceConstructor Empty() => new() { Instructions = [] };
        var avt = new XsltAttributeValueTemplate { Parts = [] };

        XsltTransformEngine.InstructionName(new XsltProcessingInstruction { Name = avt }).Should().Be("xsl:processing-instruction");
        XsltTransformEngine.InstructionName(new XsltForEachGroup { Select = PhoenixmlDb.XQuery.Ast.ContextItemExpression.Instance, Body = Empty() })
            .Should().Be("xsl:for-each-group");
        XsltTransformEngine.InstructionName(new XsltVariableInstruction { Name = new PhoenixmlDb.Core.QName(PhoenixmlDb.Core.NamespaceId.None, "v") })
            .Should().Be("xsl:variable");
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

    private const string Streams = """<xsl:template match="book"><b p="{@price}"/></xsl:template>""";

    [Fact]
    public async Task A_run_that_streams_reports_nothing()
    {
        var run = await RunAsync(Stylesheet(Streams), t => t.StreamingFallbackWarnings = true);

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
        error.Message.Should().StartWith("phx:XTSE9430: The input cannot be streamed: this processor cannot evaluate ")
            .And.Contain(" (the template match=\"/\") while streaming, although the rules of XSLT 3.0 section 19.8 find it guaranteed streamable.")
            .And.EndWith(" Without StrictStreamability (xslt command line: --strict-streaming), or with StreamingFallback"
                + " (--streaming-fallback), it reads the input into memory and gives the same result.");
    }

    // A library user has the location of the construct: the instruction that holds it, in the
    // stylesheet module.
    [Fact]
    public async Task The_strict_error_has_the_location_of_the_construct()
    {
        const string module = "file:///data/xsl/accepted.xsl";
        var stylesheet = Stylesheet(AcceptedButReadWhole);
        var transformer = new XsltTransformer { StrictStreamability = true };
        await transformer.LoadStylesheetAsync(stylesheet, new Uri(module));
        transformer.SetSourceDocumentUri(new Uri(SourceUri));

        var act = () => transformer.TransformAsync(Books);

        var location = (await act.Should().ThrowAsync<XsltException>()).Which.Location;
        location.Should().NotBeNull();
        location!.Module.Should().Be(module);
        location.Line.Should().Be(3);
        // The literal result element b, which holds count(/*): the column of its name.
        location.Column.Should().Be(stylesheet.Split('\n')[2].IndexOf("<b n=", StringComparison.Ordinal) + 2);
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
        Action<XsltTransformer>? configure = null, bool asText = false)
    {
        var warnings = new List<string>();
        var transformer = new XsltTransformer { WarningListener = warnings.Add };
        configure?.Invoke(transformer);
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetSourceDocumentUri(new Uri(SourceUri));
        if (asText)
            return (await transformer.TransformAsync(Books), transformer.LastTransformStreamed, warnings);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Books));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed, warnings);
    }
}
