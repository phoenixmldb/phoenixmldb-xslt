using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:value-of inside a streamed xsl:source-document gives what the unstreamed run gives: a value-of
/// with a body, value-of select=".", and a text() path (whose text nodes merge, no separator).
/// </summary>
public sealed class StreamedValueOfTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "phx-vo-" + Guid.NewGuid().ToString("N") + ".xml");

    public StreamedValueOfTests() => File.WriteAllText(_file, "<r><p>1</p><p>2</p><p>3</p></r>");

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("<xsl:value-of><xsl:for-each select='/r/p'>[<xsl:value-of select='.'/>]</xsl:for-each></xsl:value-of>", "[1][2][3]")]
    [InlineData("<xsl:value-of select='.'/>", "123")]
    [InlineData("<xsl:value-of select='//p/text()'/>", "123")]
    public async Task Streamed_value_of_matches_the_unstreamed_result(string body, string expected)
    {
        var href = new Uri(_file).AbsoluteUri;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:output omit-xml-declaration='yes'/><xsl:template name='xsl:initial-template'><out>" +
            $"<xsl:source-document streamable='yes' href='{href}'>{body}</xsl:source-document></out></xsl:template></xsl:stylesheet>");
        (await t.TransformAsync((string?)null)).Should().Be($"<out>{expected}</out>");
    }
}
