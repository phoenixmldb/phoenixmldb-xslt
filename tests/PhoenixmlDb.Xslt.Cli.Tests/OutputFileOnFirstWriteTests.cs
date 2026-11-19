using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// With <c>-o</c> and a streamed source, the CLI creates the output file when the first output
/// arrives. A transformation that failed before it wrote anything left an empty file, or emptied
/// an existing file of the same name.
/// </summary>
public sealed class OutputFileOnFirstWriteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-out").FullName;

    public OutputFileOnFirstWriteTests()
    {
        File.WriteAllText(Path.Combine(_dir, "fails.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:global-context-item use="required"/>
              <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
              <xsl:template match="item"><xsl:value-of select="."/></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "empty.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
              <xsl:output method="text"/>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "ok.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
              <xsl:output method="text"/>
              <xsl:template match="item"><xsl:value-of select="."/>;</xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<items><item>1</item><item>2</item></items>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Out => Path.Combine(_dir, "out.txt");

    private async Task<int> RunAsync(params string[] args)
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
        await output;
        await error;
        return process.ExitCode;
    }

    [Fact]
    public async Task A_failure_before_any_output_creates_no_file()
    {
        (await RunAsync("-o", "out.txt", "fails.xsl", "in.xml")).Should().NotBe(0);

        File.Exists(Out).Should().BeFalse();
    }

    [Fact]
    public async Task A_failure_before_any_output_leaves_an_existing_file_as_it_was()
    {
        await File.WriteAllTextAsync(Out, "old", TestContext.Current.CancellationToken);

        (await RunAsync("-o", "out.txt", "fails.xsl", "in.xml")).Should().NotBe(0);

        (await File.ReadAllTextAsync(Out, TestContext.Current.CancellationToken)).Should().Be("old");
    }

    [Fact]
    public async Task A_successful_empty_result_gives_an_empty_file()
    {
        await File.WriteAllTextAsync(Out, "old", TestContext.Current.CancellationToken);

        (await RunAsync("-o", "out.txt", "empty.xsl", "in.xml")).Should().Be(0);

        (await File.ReadAllTextAsync(Out, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_is_written()
    {
        (await RunAsync("-o", "out.txt", "ok.xsl", "in.xml")).Should().Be(0);

        (await File.ReadAllTextAsync(Out, TestContext.Current.CancellationToken)).Should().Be("1;2;");
    }
}
