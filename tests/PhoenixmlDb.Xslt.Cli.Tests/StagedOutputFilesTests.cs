using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// The CLI writes the -o file and every xsl:result-document to a temporary file and moves them
/// into place only when the whole run has succeeded. A run that fails after an output file was
/// written (here: a second result document whose directory cannot be made) leaves no output file,
/// no temporary file, and an existing file of the same name unchanged. The files were written one
/// at a time, so the first stayed, replacing what was there.
/// </summary>
public sealed class StagedOutputFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-staged").FullName;

    private const string Documents = """
            <xsl:result-document href="a.txt" method="text">new a</xsl:result-document>
            <xsl:result-document href="blocker/b.txt" method="text">new b</xsl:result-document>
            """;

    public StagedOutputFilesTests()
    {
        File.WriteAllText(Path.Combine(_dir, "tree.xsl"), $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">new out{Documents}</xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "streamed.xsl"), $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/">new out{Documents}</xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "in.xml"), "<a/>");
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Out(string name) => Path.Combine(_dir, "out", name);

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

    private Task<int> Run(string stylesheet)
        => RunAsync("-o", Path.Combine("out", "o.txt"), "--output-dir", "out", stylesheet, "in.xml");

    private string[] TemporaryFiles() => Directory.GetFiles(Path.Combine(_dir, "out"), "*.tmp", SearchOption.AllDirectories);

    [Theory]
    [InlineData("tree.xsl")]
    [InlineData("streamed.xsl")]
    public async Task A_failed_write_leaves_no_output_file_and_no_temporary_file(string stylesheet)
    {
        await File.WriteAllTextAsync(Out("blocker"), "a file, so blocker/ cannot be a directory", TestContext.Current.CancellationToken);

        (await Run(stylesheet)).Should().NotBe(0);

        File.Exists(Out("o.txt")).Should().BeFalse();
        File.Exists(Out("a.txt")).Should().BeFalse();
        TemporaryFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData("tree.xsl")]
    [InlineData("streamed.xsl")]
    public async Task A_failed_write_leaves_existing_files_unchanged(string stylesheet)
    {
        await File.WriteAllTextAsync(Out("blocker"), "a file, so blocker/ cannot be a directory", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Out("o.txt"), "old out", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Out("a.txt"), "old a", TestContext.Current.CancellationToken);

        (await Run(stylesheet)).Should().NotBe(0);

        (await File.ReadAllTextAsync(Out("o.txt"), TestContext.Current.CancellationToken)).Should().Be("old out");
        (await File.ReadAllTextAsync(Out("a.txt"), TestContext.Current.CancellationToken)).Should().Be("old a");
        TemporaryFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData("tree.xsl")]
    [InlineData("streamed.xsl")]
    public async Task A_successful_run_writes_every_file_and_replaces_old_ones(string stylesheet)
    {
        await File.WriteAllTextAsync(Out("a.txt"), "old a", TestContext.Current.CancellationToken);

        (await Run(stylesheet)).Should().Be(0);

        (await File.ReadAllTextAsync(Out("o.txt"), TestContext.Current.CancellationToken)).Trim().Should().Be("new out");
        (await File.ReadAllTextAsync(Out("a.txt"), TestContext.Current.CancellationToken)).Should().Be("new a");
        (await File.ReadAllTextAsync(Path.Combine(Out("blocker"), "b.txt"), TestContext.Current.CancellationToken)).Should().Be("new b");
        TemporaryFiles().Should().BeEmpty();
    }
}
