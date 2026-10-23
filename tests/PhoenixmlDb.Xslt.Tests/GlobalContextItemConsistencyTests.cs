using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:global-context-item declarations in several modules of a package must be semantically
/// equivalent (XSLT 3.0 §3.5.6, XTSE3087); whitespace in the as attribute is not significant. The
/// check compared the parsed types' ToString(), which dropped the element test, so
/// document-node(element(customer)) matched document-node(element(supplier)) (W3C
/// glob-cxt-item-007), and an as attribute with leading spaces did not match its unspaced twin
/// (W3C glob-cxt-item-008).
/// </summary>
public sealed class GlobalContextItemConsistencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxgci-" + Guid.NewGuid().ToString("N"));

    public GlobalContextItemConsistencyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string mainAs, string includedAs)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "inc.xsl"), $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:global-context-item use="required" as="{includedAs}"/>
            </xsl:stylesheet>
            """);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:global-context-item use="required" as="{mainAs}"/>
              <xsl:include href="inc.xsl"/>
              <xsl:output method="text"/>
              <xsl:template match="/">ok</xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        return await transformer.TransformAsync("<doc/>");
    }

    [Fact]
    public async Task Different_element_tests_are_XTSE3087()
    {
        var act = () => RunAsync("document-node(element(doc))", "document-node(element(other))");
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTSE3087*");
    }

    [Fact]
    public async Task Whitespace_in_the_as_attribute_is_not_significant()
        => (await RunAsync("document-node(element(doc))", " document-node( element( doc ))")).Should().Be("ok");
}
