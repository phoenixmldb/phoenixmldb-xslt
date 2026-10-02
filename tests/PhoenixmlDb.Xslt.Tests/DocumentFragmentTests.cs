using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// document() with a fragment that is not a shorthand pointer (an NCName) is XTDE1160, raised
/// before the resource is fetched; a shorthand pointer still selects the element with that ID.
/// </summary>
public sealed class DocumentFragmentTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "phx-frag-" + Guid.NewGuid().ToString("N") + ".xml");

    public DocumentFragmentTests() => File.WriteAllText(_file, "<r><e xml:id='k'>hit</e></r>");

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string fragment)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            $"<xsl:template name='xsl:initial-template'><out><xsl:value-of select=\"document('{new Uri(_file).AbsoluteUri}#{fragment}')\"/></out></xsl:template></xsl:stylesheet>");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task A_fragment_that_is_not_an_NCName_is_XTDE1160()
        => (await FluentActions.Awaiting(() => RunAsync("123456789")).Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("XTDE1160");

    [Fact]
    public async Task A_shorthand_pointer_selects_the_element_by_ID()
        => (await RunAsync("k")).Should().Contain("<out>hit</out>");
}
