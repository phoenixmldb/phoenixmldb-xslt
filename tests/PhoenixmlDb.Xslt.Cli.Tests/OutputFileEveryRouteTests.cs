using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// On every route of the CLI, the <c>-o</c> file is written only with output: a transformation
/// that fails creates no file and leaves an existing file as it was. The streamed route is
/// covered by <see cref="OutputFileOnFirstWriteTests"/>; these cover the in-memory route
/// (<c>--no-stream</c>, a source that is not streamed, an initial template) and a static error.
/// </summary>
public sealed class OutputFileEveryRouteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-routes").FullName;

    public OutputFileEveryRouteTests()
    {
        // Writes output, then fails: the failure comes after output was produced.
        File.WriteAllText(Path.Combine(_dir, "late.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/" name="start">partial<xsl:message terminate="yes">stop</xsl:message></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "late-streamable.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/">partial<xsl:message terminate="yes">stop</xsl:message></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "static.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/"><xsl:value-of select="1 +"/></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<a/>");
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

    private static readonly string[] InMemory = ["-o", "out.txt", "late.xsl", "in.xml"];
    private static readonly string[] NoStream = ["-o", "out.txt", "--no-stream", "late-streamable.xsl", "in.xml"];
    private static readonly string[] InitialTemplate = ["-o", "out.txt", "-it", "start", "late.xsl"];
    private static readonly string[] StaticError = ["-o", "out.txt", "static.xsl", "in.xml"];

    public static TheoryData<string[]> FailingRuns => new() { InMemory, NoStream, InitialTemplate, StaticError };

    [Theory]
    [MemberData(nameof(FailingRuns))]
    public async Task A_failed_run_creates_no_file(string[] args)
    {
        (await RunAsync(args)).Should().NotBe(0);

        File.Exists(Out).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(FailingRuns))]
    public async Task A_failed_run_leaves_an_existing_file_as_it_was(string[] args)
    {
        await File.WriteAllTextAsync(Out, "old", TestContext.Current.CancellationToken);

        (await RunAsync(args)).Should().NotBe(0);

        (await File.ReadAllTextAsync(Out, TestContext.Current.CancellationToken)).Should().Be("old");
    }
}
