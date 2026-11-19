using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// <c>--global-context-item</c> supplies a global context item separate from the source. A
/// streamed source is not the global context item (XSLT 3.0 §2.3.2), so a stylesheet with a
/// streamable initial mode and <c>xsl:global-context-item use="required"</c> fails with XTDE3086,
/// which names the option and <c>--no-stream</c>, before any output; with the option the globals
/// read the separate document. <c>--no-stream</c> keeps the source as the global context item.
/// </summary>
public sealed class GlobalContextItemOptionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-gci").FullName;

    public GlobalContextItemOptionTests()
    {
        File.WriteAllText(Path.Combine(_dir, "s.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:global-context-item use="required"/>
              <xsl:param name="items" select="*/item"/>
              <xsl:mode on-no-match="deep-skip" streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/">{sum($items)}</xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<items><item>5</item><item>5</item></items>");
        File.WriteAllText(Path.Combine(_dir, "gci.xml"), "<items><item>7</item><item>7</item><item>7</item></items>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "xslt.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = _dir };
        start.ArgumentList.Add(cli);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await output).Trim(), await error);
    }

    [Fact]
    public async Task Without_the_option_a_streamed_source_is_XTDE3086_naming_both_options_and_writes_nothing()
    {
        var (exit, output, error) = await RunAsync("-o", "out.txt", "s.xsl", "in.xml");

        exit.Should().NotBe(0);
        error.Should().Contain("XTDE3086").And.Contain("--global-context-item").And.Contain("--no-stream");
        output.Should().BeEmpty();
        File.Exists(Path.Combine(_dir, "out.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task With_the_option_the_globals_read_the_separate_document()
    {
        var (exit, output, error) = await RunAsync("--global-context-item", "gci.xml", "s.xsl", "in.xml");

        exit.Should().Be(0, error);
        output.Should().Be("21");
    }

    [Fact]
    public async Task With_no_stream_the_source_is_the_global_context_item()
    {
        var (exit, output, error) = await RunAsync("--no-stream", "s.xsl", "in.xml");

        exit.Should().Be(0, error);
        output.Should().Be("10");
    }

    [Fact]
    public async Task With_no_stream_and_the_option_the_separate_document_is_the_global_context_item()
    {
        var (exit, output, error) = await RunAsync("--no-stream", "--global-context-item", "gci.xml", "s.xsl", "in.xml");

        exit.Should().Be(0, error);
        output.Should().Be("21");
    }

    [Fact]
    public async Task A_missing_file_is_an_error()
    {
        var (exit, _, error) = await RunAsync("--global-context-item", "none.xml", "s.xsl", "in.xml");

        exit.Should().Be(1);
        error.Should().Contain("Global context item file not found");
    }
}
