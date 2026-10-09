using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>file:/abs/path</c> is a file URI (RFC 8089), the same location as <c>file:///abs/path</c>.
/// .NET takes it for neither an absolute URI nor a relative reference, and doc, document and
/// the unparsed-text functions threw UriFormatException for it in a stylesheet with a base URI:
/// not an XSLT error, and the host's resolver was not asked.
/// </summary>
public sealed class FileUriWithoutAuthorityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-xfileuri-" + Guid.NewGuid().ToString("N"));

    public FileUriWithoutAuthorityTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "d.xml"), "<r>7</r>");
        File.WriteAllText(Path.Combine(_dir, "t.txt"), "7");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly Uri HttpBase = new("http://host.invalid/app/main.xsl");

    private string OneSlash(string file) => "file:" + new Uri(Path.Combine(_dir, file)).AbsolutePath;

    private static string Sheet(string select) =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:output method='text'/>"
        + "<xsl:template match='/'><xsl:value-of select=\"" + select + "\"/></xsl:template></xsl:stylesheet>";

    private static async Task<string> Run(XsltTransformer transformer, string select)
    {
        await transformer.LoadStylesheetAsync(Sheet(select), HttpBase);
        return (await transformer.TransformAsync("<x/>")).Trim();
    }

    [Theory]
    [InlineData("string(doc('{0}'))", "d.xml")]
    [InlineData("string(document('{0}'))", "d.xml")]
    [InlineData("doc-available('{0}')", "d.xml", "true")]
    [InlineData("unparsed-text('{0}')", "t.txt")]
    [InlineData("string-join(unparsed-text-lines('{0}'))", "t.txt")]
    [InlineData("unparsed-text-available('{0}')", "t.txt", "true")]
    public async Task The_file_is_read(string expression, string file, string expected = "7")
    {
        var select = string.Format(System.Globalization.CultureInfo.InvariantCulture, expression, OneSlash(file));

        (await Run(new XsltTransformer(), select)).Should().Be(expected);
    }

    private sealed class Recorder : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location);
            return null;
        }
    }

    [Theory]
    [InlineData("doc('file:/abs/path')")]
    [InlineData("document('file:/abs/path')")]
    [InlineData("unparsed-text('file:/abs/path')")]
    [InlineData("unparsed-text-lines('file:/abs/path')")]
    public async Task Under_a_policy_the_host_is_asked_and_its_refusal_is_not_a_dotnet_exception(string expression)
    {
        var host = new Recorder();
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build() };

        var act = () => Run(transformer, expression);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<UriFormatException>();
        host.Asked.Should().Contain("file:///abs/path");
    }

    [Theory]
    [InlineData("doc-available('file:/abs/path')")]
    [InlineData("unparsed-text-available('file:/abs/path')")]
    [InlineData("unparsed-text-available('file:abs')")]
    public async Task Availability_is_false_and_does_not_fail(string expression)
    {
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(new Recorder()).Build() };

        (await Run(transformer, expression)).Should().Be("false");
    }
}
