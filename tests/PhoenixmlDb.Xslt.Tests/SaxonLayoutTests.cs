using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>phx:indent-style="saxon-he-10"</c> (xslt#499): an indented result laid out as Saxon-HE 10.9
/// lays it out, to the byte. Each case in SaxonLayout is a stylesheet that builds a random tree
/// (scripts/saxon-layout-cases.py); its expected result is the output of Saxon-HE 10.9 for it.
/// </summary>
public class SaxonLayoutTests
{
    private static string CaseDirectory => Path.Combine(AppContext.BaseDirectory, "SaxonLayout");

    public static TheoryData<string> Cases()
    {
        var cases = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(CaseDirectory, "case*.xsl").Order(StringComparer.Ordinal))
            cases.Add(Path.GetFileNameWithoutExtension(path));
        return cases;
    }

    [Fact]
    public void There_are_cases()
        => Cases().Count.Should().Be(180);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_result_is_the_one_Saxon_HE_10_writes(string name)
    {
        var stylesheet = await File.ReadAllTextAsync(Path.Combine(CaseDirectory, name + ".xsl"), TestContext.Current.CancellationToken);
        var expected = await File.ReadAllTextAsync(Path.Combine(CaseDirectory, name + ".xml"), Encoding.UTF8, TestContext.Current.CancellationToken);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");

        var actual = await transformer.TransformAsync((string?)null);

        actual.Should().Be(expected);
    }
}
