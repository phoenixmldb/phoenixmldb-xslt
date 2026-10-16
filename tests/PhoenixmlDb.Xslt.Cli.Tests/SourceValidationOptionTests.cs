using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Cli.Tests;

/// <summary>
/// <c>--validation strict|lax</c> (and <c>--val:strict</c>, <c>--val:lax</c>) validate the source
/// document, as <see cref="XsltTransformer.SourceValidation"/> does (xslt#398). Without one the
/// source stays untyped; before, the CLI had no way to ask for validation.
/// </summary>
public sealed class SourceValidationOptionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-cli-validation").FullName;

    public SourceValidationOptionTests()
    {
        File.WriteAllText(Path.Combine(_dir, "s.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="value" type="xs:decimal"/></xs:schema>
            """);
        File.WriteAllText(Path.Combine(_dir, "s.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:import-schema schema-location="s.xsd"/>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="data(value) instance of xs:decimal"/></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "ok.xml"), "<value>1.5</value>");
        File.WriteAllText(Path.Combine(_dir, "bad.xml"), "<value></value>");
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
    public async Task Without_the_option_the_source_is_untyped()
    {
        var (exit, output, _) = await RunAsync("s.xsl", "bad.xml");
        exit.Should().Be(0);
        output.Should().Be("false");
    }

    [Theory]
    [InlineData("--validation", "strict")]
    [InlineData("--validation", "lax")]
    [InlineData("--val:strict", null)]
    [InlineData("--val:lax", null)]
    public async Task A_valid_source_is_typed(string option, string? value)
    {
        var (exit, output, error) = await RunAsync(value is null ? [option, "s.xsl", "ok.xml"] : [option, value, "s.xsl", "ok.xml"]);
        exit.Should().Be(0, error);
        output.Should().Be("true");
    }

    /// <summary>XTTE1510 with strict validation, XTTE1515 with lax (XSLT 3.0 §25.4.1).</summary>
    [Theory]
    [InlineData("--validation", "strict", "XTTE1510")]
    [InlineData("--validation", "lax", "XTTE1515")]
    [InlineData("--val:strict", null, "XTTE1510")]
    [InlineData("--val:lax", null, "XTTE1515")]
    public async Task An_invalid_source_is_an_error(string option, string? value, string code)
    {
        var (exit, _, error) = await RunAsync(value is null ? [option, "s.xsl", "bad.xml"] : [option, value, "s.xsl", "bad.xml"]);
        exit.Should().NotBe(0);
        error.Should().Contain(code);
    }

    [Fact]
    public async Task An_unknown_validation_value_is_refused()
    {
        var (exit, _, error) = await RunAsync("--validation", "preserve", "s.xsl", "ok.xml");
        exit.Should().Be(1);
        error.Should().Contain("strict or lax");
    }
}
