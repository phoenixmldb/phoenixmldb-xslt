using AwesomeAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The order between trees (xslt#542). XDM leaves it implementation-dependent; the engine's rule
/// is that a parsed document (the principal source, fn:doc(), fn:parse-xml(), a streamed source)
/// precedes every constructed tree, whenever either was built, as in XQuery's store. Global
/// variables are evaluated when the transform starts, so by creation order their trees came
/// before a document read later, where the W3C cases (sx-union-012/017/022/035, si-fork-118,
/// square-array-201) expect the document first.
/// </summary>
public class TreeOrderTests
{
    private static async Task<string> TransformAsync(string stylesheet, string? input, params (string Name, string Xml)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tree-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, xml) in files)
                await File.WriteAllTextAsync(Path.Combine(dir, name), xml);
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            if (input is null)
                transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return (await transformer.TransformAsync(input)).Trim();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Sheet(string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:variable name="g" as="element()*"><g1/><g2/></xsl:variable>
          {{body}}
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task ADocumentReadByDocPrecedesAGlobalVariablesTree()
    {
        var result = await TransformAsync(Sheet("""
            <xsl:template name="xsl:initial-template">
              <xsl:value-of select="(doc('d.xml')/r/* union $g) ! name()"/>
            </xsl:template>
            """), null, ("d.xml", "<r><d1/><d2/></r>"));

        result.Should().Be("d1 d2 g1 g2");
    }

    [Fact]
    public async Task ADocumentFromParseXmlPrecedesAGlobalVariablesTree()
    {
        var result = await TransformAsync(Sheet("""
            <xsl:template name="xsl:initial-template">
              <xsl:value-of select="($g union parse-xml('&lt;r>&lt;p1/>&lt;/r>')/r/*) ! name()"/>
            </xsl:template>
            """), null);

        result.Should().Be("p1 g1 g2");
    }

    [Fact]
    public async Task AStreamedDocumentPrecedesAGlobalVariablesTree()
    {
        var result = await TransformAsync(Sheet("""
            <xsl:template name="xsl:initial-template">
              <xsl:source-document streamable="yes" href="s.xml">
                <xsl:value-of select="(/r/* union $g) ! name()"/>
              </xsl:source-document>
            </xsl:template>
            """), null, ("s.xml", "<r><s1/><s2/></r>"));

        result.Should().Be("s1 s2 g1 g2");
    }

    [Fact]
    public async Task ConstructedTreesKeepTheOrderTheyWereBuiltIn()
    {
        // Between constructed trees the order is creation order, as before: the global's tree
        // is built when the transform starts, the local variable's later.
        var result = await TransformAsync(Sheet("""
            <xsl:template name="xsl:initial-template">
              <xsl:variable name="local" as="element()*"><l1/></xsl:variable>
              <xsl:value-of select="($local union $g) ! name()"/>
            </xsl:template>
            """), null);

        result.Should().Be("g1 g2 l1");
    }

    [Fact]
    public async Task GenerateIdDoesNotDependOnTreesBuiltBeforeInTheProcess()
    {
        // The tree ordinal is a process-wide count; generate-id() of a node of the engine's
        // store does not include it, so a second run gives the same ids, as it did before.
        var sheet = Sheet("""
            <xsl:template match="/">
              <xsl:value-of select="generate-id(/), generate-id(/*), generate-id($g[1])"/>
            </xsl:template>
            """);

        var first = await TransformAsync(sheet, "<a><b/></a>");
        var second = await TransformAsync(sheet, "<a><b/></a>");

        second.Should().Be(first);
        first.Split(' ').Should().OnlyContain(id => id.StartsWith("d0n", StringComparison.Ordinal));
    }
}
