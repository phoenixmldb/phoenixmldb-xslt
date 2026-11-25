using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// With <c>--stream</c>, a run of a stylesheet with a streamable mode that reads its input into
/// memory says why on stderr: the construct and its location (xslt#582). It said nothing; only
/// <c>--timing</c> showed it. With <c>--strict-streaming</c>, a construct that the rules of XSLT 3.0
/// §19.8 find guaranteed-streamable but that the engine cannot evaluate while streaming is
/// phx:XTSE9430. The warnings of the warning listener (xsl:mode warning-on-no-match) go to stderr;
/// they were not written. A run with no warning writes nothing new.
/// </summary>
public sealed class StreamingWarningOptionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-stream-warning").FullName;

    private const string Head = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
        """;

    public StreamingWarningOptionTests()
    {
        // The value of an ancestor: read into memory, with the right result.
        File.WriteAllText(Path.Combine(_dir, "ancestor.xsl"), Head + """
              <xsl:template match="title"><t><xsl:value-of select="string(..)"/></t></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "streams.xsl"), Head + """
              <xsl:template match="book"><b p="{@price}"/></xsl:template>
            </xsl:stylesheet>
            """);
        // §19.8 accepts it ("/" in the loop is the root of a snapshot); the engine reads the input
        // into memory for it.
        File.WriteAllText(Path.Combine(_dir, "accepted.xsl"), Head + """
              <xsl:template match="/"><out><xsl:for-each select="snapshot(books/book)"><b n="{count(/*)}"/></xsl:for-each></out></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "nomatch.xsl"), """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode warning-on-no-match="yes"/>
              <xsl:template match="/"><r><xsl:apply-templates select="books/@x"/></r></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), """<books x="1"><book price="5"><title>A</title></book></books>""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "xslt.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _dir };
        start.ArgumentList.Add(cli);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await output).Trim(), await error);
    }

    [Fact]
    public async Task With_stream_a_run_that_reads_the_input_into_memory_says_why()
    {
        var (exit, output, error) = await RunAsync("ancestor.xsl", "in.xml", "--stream");

        exit.Should().Be(0, error);
        output.Should().Be("<t>A</t>");
        // The construct as the engine writes it (.. is parent::node()), where it is: line 3, as the
        // template follows the mode declaration on its line.
        error.Should().StartWith("Warning: XSLT streaming: the input was read into memory: parent::node()")
            .And.Contain($" at {Path.Combine(_dir, "ancestor.xsl")}:3:")
            .And.Contain("(the template match=\"title\"), which this processor cannot evaluate while streaming.");
        error.TrimEnd().Should().NotContain("\n");
    }

    [Theory]
    [InlineData("ancestor.xsl")]        // read into memory, without --stream
    [InlineData("ancestor.xsl", "--no-stream")]
    [InlineData("streams.xsl", "--stream")]
    [InlineData("streams.xsl")]
    public async Task A_run_with_no_warning_writes_nothing_to_stderr(string stylesheet, string? flag = null)
    {
        var (exit, _, error) = flag is null
            ? await RunAsync(stylesheet, "in.xml")
            : await RunAsync(stylesheet, "in.xml", flag);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
    }

    [Fact]
    public async Task With_strict_streaming_a_construct_the_engine_cannot_stream_is_phx_XTSE9430()
    {
        var (exit, output, error) = await RunAsync("accepted.xsl", "in.xml", "--strict-streaming");

        exit.Should().Be(2);
        output.Should().BeEmpty();
        error.Should().StartWith($"XSLT error at {Path.Combine(_dir, "accepted.xsl")}:")
            .And.Contain(": phx:XTSE9430: The input cannot be streamed: this processor cannot evaluate ");
    }

    [Fact]
    public async Task Without_strict_streaming_the_same_run_reads_the_input_into_memory()
    {
        var (exit, output, error) = await RunAsync("accepted.xsl", "in.xml");

        exit.Should().Be(0, error);
        output.Should().Be("""<out><b n="1"/></out>""");
        error.Should().BeEmpty();
    }

    [Fact]
    public async Task A_warning_of_the_stylesheet_goes_to_stderr()
    {
        var (exit, output, error) = await RunAsync("nomatch.xsl", "in.xml");

        exit.Should().Be(0, error);
        output.Should().Be("<r>1</r>");
        error.Should().Be("Warning: No template rule matches attribute x; the built-in rule was used" + Environment.NewLine);
    }
}
