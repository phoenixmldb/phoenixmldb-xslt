using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// Without <c>-o</c> the CLI writes secondary result documents relative to <c>--output-dir</c>, or
/// else the working directory, and that directory is the base output URI: inside
/// <c>xsl:result-document href="rd.xml"</c>, <c>current-output-uri()</c> is the absolute URI of the
/// file written, and in the principal result it is the directory. The base output URI was set only
/// with <c>-o</c>, so the function gave the bare href, a relative URI, and in the principal result
/// the empty sequence (xslt#561).
/// </summary>
public sealed class CurrentOutputUriOptionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-cou").FullName;

    public CurrentOutputUriOptionTests()
    {
        File.WriteAllText(Path.Combine(_dir, "s.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:variable name="g" select="'[' || string(current-output-uri()) || ']'"/>
              <xsl:template name="xsl:initial-template">
                <xsl:text>principal {current-output-uri()} global {$g}</xsl:text>
                <xsl:result-document href="rd.txt" method="text">rd {current-output-uri()}</xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """);
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = _dir };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "xslt.dll"));
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await output).Trim(), await error);
    }

    private static string DirectoryUri(string directory) => new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri;

    [Fact]
    public async Task Without_o_the_working_directory_is_the_base_output_uri()
    {
        var (exit, output, error) = await RunAsync("-it", "xsl:initial-template", "s.xsl");

        exit.Should().Be(0, error);
        output.Should().Be($"principal {DirectoryUri(_dir)} global []");
        var written = Path.Combine(_dir, "rd.txt");
        (await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken)).Trim().Should().Be($"rd {new Uri(written).AbsoluteUri}");
    }

    [Fact]
    public async Task Without_o_the_output_directory_is_the_base_output_uri()
    {
        var (exit, output, error) = await RunAsync("-it", "xsl:initial-template", "--output-dir", "out", "s.xsl");

        exit.Should().Be(0, error);
        output.Should().Be($"principal {DirectoryUri(Path.Combine(_dir, "out"))} global []");
        var written = Path.Combine(_dir, "out", "rd.txt");
        (await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken)).Trim().Should().Be($"rd {new Uri(written).AbsoluteUri}");
    }

    [Fact]
    public async Task With_o_the_output_file_is_the_base_output_uri_as_before()
    {
        var (exit, _, error) = await RunAsync("-it", "xsl:initial-template", "-o", Path.Combine("out", "p.txt"), "s.xsl");

        exit.Should().Be(0, error);
        var principal = Path.Combine(_dir, "out", "p.txt");
        (await File.ReadAllTextAsync(principal, TestContext.Current.CancellationToken)).Trim().Should().Be($"principal {new Uri(principal).AbsoluteUri} global []");
        var written = Path.Combine(_dir, "out", "rd.txt");
        (await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken)).Trim().Should().Be($"rd {new Uri(written).AbsoluteUri}");
    }
}
