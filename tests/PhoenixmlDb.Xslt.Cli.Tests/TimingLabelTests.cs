using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// <c>--timing</c> said "(streaming)" whenever the CLI took its streaming branch, even when the
/// engine then read the input into memory, as it does for <c>-it</c> (xslt#463). The label now
/// comes from <see cref="XsltTransformer.LastTransformStreamed"/>.
/// </summary>
public sealed class TimingLabelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-timing").FullName;

    public TimingLabelTests()
    {
        File.WriteAllText(Path.Combine(_dir, "s.xsl"), """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="sum(root/item)"/></xsl:template>
              <xsl:template name="go">go</xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<root><item>1</item><item>2</item></root>");
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
    public async Task A_streamed_run_is_labelled_streaming()
    {
        var (exit, output, error) = await RunAsync("s.xsl", "in.xml", "--timing");

        exit.Should().Be(0, error);
        output.Should().Be("3");
        error.Should().Contain("(streaming)");
    }

    [Fact]
    public async Task A_run_from_a_named_template_is_labelled_not_streamed()
    {
        var (exit, output, error) = await RunAsync("s.xsl", "in.xml", "-it", "go", "--timing");

        exit.Should().Be(0, error);
        output.Should().Be("go");
        error.Should().Contain("(not streamed").And.NotContain("(streaming)");
    }
}
