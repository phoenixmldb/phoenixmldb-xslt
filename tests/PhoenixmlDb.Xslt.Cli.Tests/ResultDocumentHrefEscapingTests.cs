using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// <c>xsl:result-document href="result-[1].xml"</c> writes the file <c>result-[1].xml</c>, and
/// <c>current-output-uri()</c> in it is the escaped absolute URI <c>…/result-%5B1%5D.xml</c>
/// (XSLT 3.0 §5.8). It reported the URI unescaped (xslt#562).
/// </summary>
public sealed class ResultDocumentHrefEscapingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-rdesc").FullName;

    public ResultDocumentHrefEscapingTests()
    {
        File.WriteAllText(Path.Combine(_dir, "s.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:result-document href="result-[1].xml" method="text">{current-output-uri()}</xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_file_keeps_its_name_and_reports_the_escaped_uri()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = _dir };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "xslt.dll"));
        foreach (var arg in new[] { "-it", "xsl:initial-template", "-o", "out.txt", "s.xsl" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await output;

        process.ExitCode.Should().Be(0, await error);
        var written = Path.Combine(_dir, "result-[1].xml");
        File.Exists(written).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "result-%5B1%5D.xml")).Should().BeFalse();
        var directory = new Uri(_dir + Path.DirectorySeparatorChar).AbsoluteUri;
        (await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken)).Trim()
            .Should().Be(directory + "result-%5B1%5D.xml");
    }
}
