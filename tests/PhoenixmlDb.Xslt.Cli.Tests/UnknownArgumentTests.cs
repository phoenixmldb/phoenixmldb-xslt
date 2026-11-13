using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// An option the tool does not know, or a third argument, stops the run with an error. Both were
/// ignored: <c>xslt style.xsl in.xml -d out</c> ran as if -d were absent (the option is
/// --output-dir) and wrote the secondary results to the current directory, with exit code 0.
/// </summary>
public sealed class UnknownArgumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-args").FullName;

    public UnknownArgumentTests()
    {
        File.WriteAllText(Path.Combine(_dir, "rd.xsl"), """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><main/><xsl:result-document href="part.xml"><p/></xsl:result-document></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<root/>");
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
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

    [Theory]
    [InlineData("-d")]
    [InlineData("--outputdir")]
    public async Task An_unknown_option_is_an_error_and_nothing_is_written(string option)
    {
        var (exit, _, error) = await RunAsync("rd.xsl", "in.xml", option, "out");

        exit.Should().Be(1);
        error.Should().Contain($"unknown option '{option}'");
        File.Exists(Path.Combine(_dir, "part.xml")).Should().BeFalse();
    }

    [Fact]
    public async Task A_third_argument_is_an_error()
    {
        var (exit, _, error) = await RunAsync("rd.xsl", "in.xml", "extra.xml");

        exit.Should().Be(1);
        error.Should().Contain("unexpected argument 'extra.xml'");
    }

    [Fact]
    public async Task Output_dir_places_the_secondary_results()
    {
        var (exit, output, _) = await RunAsync("rd.xsl", "in.xml", "--output-dir", "out");

        exit.Should().Be(0);
        output.Should().Contain("<main/>");
        File.Exists(Path.Combine(_dir, "out", "part.xml")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "part.xml")).Should().BeFalse();
    }
}
