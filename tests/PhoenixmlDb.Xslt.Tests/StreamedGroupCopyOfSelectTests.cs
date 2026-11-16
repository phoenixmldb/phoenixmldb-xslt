using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed xsl:for-each-group whose select is <c>t/copy-of()</c> groups the <c>t</c> children
/// only. The select parses as a simple map through copy-of(), which the streamed member filter did
/// not look through, so every child was a member: an <c>&lt;n&gt;</c> sibling with no @k gave an
/// empty grouping key, XTTE1100 (W3C si-group-026). Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedGroupCopyOfSelectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-grpcpy-" + Guid.NewGuid().ToString("N"));

    public StreamedGroupCopyOfSelectTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "d.xml"),
            "<r><n>head</n><t k='a' v='1'/><t k='a' v='2'/><t k='b' v='3'/><t k='a' v='4'/></r>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string select, string grouping)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="d.xml"><xsl:apply-templates select="r" mode="s"/></xsl:source-document>
              </xsl:template>
              <xsl:template match="r" mode="s">
                <out><xsl:for-each-group select="{{select}}" {{grouping}}><g k="{current-grouping-key()}" v="{current-group()/@v}"/></xsl:for-each-group></out>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("t/copy-of()", "group-adjacent='@k'")]
    [InlineData("t/snapshot()", "group-adjacent='@k'")]
    [InlineData("t", "group-adjacent='@k'")]
    public async Task Only_the_selected_children_are_grouped(string select, string grouping)
        => (await Run(select, grouping)).Should().Be("<out><g k=\"a\" v=\"1 2\"/><g k=\"b\" v=\"3\"/><g k=\"a\" v=\"4\"/></out>");
}
