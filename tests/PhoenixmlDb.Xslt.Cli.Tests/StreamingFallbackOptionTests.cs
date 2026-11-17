using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// <c>--streaming-fallback</c> sets <see cref="XsltTransformer.StreamingFallback"/>: a template
/// in a streamable mode that is not guaranteed-streamable runs with a buffer instead of failing
/// with XTSE3430, whose message names the option.
/// </summary>
public sealed class StreamingFallbackOptionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-fallback").FullName;

    public StreamingFallbackOptionTests()
    {
        File.WriteAllText(Path.Combine(_dir, "s.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:for-each select="r/i"><xsl:value-of select="a"/>-<xsl:value-of select="b"/>;</xsl:for-each></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<r><i><a>1</a><b>2</b></i><i><a>3</a><b>4</b></i></r>");
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
    public async Task Without_the_option_it_is_XTSE3430_naming_the_option()
    {
        var (exit, _, error) = await RunAsync("s.xsl", "in.xml");
        exit.Should().NotBe(0);
        error.Should().Contain("XTSE3430").And.Contain("--streaming-fallback");
    }

    [Fact]
    public async Task With_the_option_it_runs()
    {
        var (exit, output, _) = await RunAsync("--streaming-fallback", "s.xsl", "in.xml");
        exit.Should().Be(0);
        output.Should().Be("1-2;3-4;");
    }
}
