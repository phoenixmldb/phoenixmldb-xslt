using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A separator is an attribute value template and may consume the input in its own right —
/// separator="{substring(head(//AUTHOR), 1, 1)}" reads the stream to decide what to put between
/// the items. Only the select was examined for input navigation, so at the document level the
/// separator evaluated against the empty synthetic node and folded to "": the items were joined
/// with nothing at all (W3C strm si-value-of-044, si-attribute-044).
/// </summary>
public sealed class StreamingSeparatorAvtTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-sep-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> RunAsync(string body, string streamable)
    {
        var source = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(source, "<doc><AUTHOR>Jane Austen</AUTHOR></doc>").ConfigureAwait(true);
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode streamable="{{streamable}}"/>
              <xsl:template name="main">
                <xsl:source-document streamable="{{streamable}}" href="{{new Uri(source).AbsoluteUri}}">
                  <out>{{body}}</out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private const string ValueOf = """<xsl:value-of select="1 to 3" separator="{substring(head(//AUTHOR), 1, 1)}"/>""";
    private const string Attribute = """<xsl:attribute name="a" select="1 to 3" separator="{substring(head(//AUTHOR), 1, 1)}"/>""";

    [Fact]
    public async Task AConsumingSeparator_OnValueOf_SeesTheStream()
        => (await RunAsync(ValueOf, "yes")).Should().Be("<out>1J2J3</out>",
            "the streamed run joined the items with nothing");

    [Fact]
    public async Task AConsumingSeparator_OnAttribute_SeesTheStream()
        => (await RunAsync(Attribute, "yes")).Should().Be("""<out a="1J2J3"/>""");

    [Theory]
    [InlineData(ValueOf)]
    [InlineData(Attribute)]
    public async Task TheStreamedRunAgreesWithTheUnstreamedOne(string body)
        => (await RunAsync(body, "yes")).Should().Be(await RunAsync(body, "no"));

    /// <summary>A grounded separator needs no buffering and is unchanged.</summary>
    [Fact]
    public async Task AGroundedSeparator_IsUnchanged()
        => (await RunAsync("""<xsl:value-of select="1 to 3" separator="-"/>""", "yes")).Should().Be("<out>1-2-3</out>");
}
