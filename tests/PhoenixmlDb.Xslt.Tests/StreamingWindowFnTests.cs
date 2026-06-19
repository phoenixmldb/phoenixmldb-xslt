using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// OP-bucket streaming phase 3 (forward-window functions): <c>head(path)</c>,
/// <c>tail(path)</c>, and <c>remove(path, N)</c> as the source of a streamable
/// <c>xsl:for-each select</c> or the LEFT of a per-item simple-map. Each peels to
/// its inner striding path and applies a forward positional window during dispatch,
/// mirroring the existing <c>subsequence()</c> peel/window machinery.
/// </summary>
public class StreamingWindowFnTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-window-fn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, inputFileName);
        await File.WriteAllTextAsync(inputPath, inputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private const string Xml = """
        <r><i>1</i><i>2</i><i>3</i><i>4</i></r>
        """;

    private static string Sheet(string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="r.xml">
              <out>{{body}}</out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // head() as for-each source: only the first matched item.
    [Fact]
    public async Task Head_ForEachSource_FirstItemOnly()
    {
        var result = await TransformWithFile(
            Sheet("""<xsl:for-each select="head(r/i)"><x><xsl:value-of select="."/></x></xsl:for-each>"""),
            Xml, "r.xml");
        result.Trim().Should().Be("<out><x>1</x></out>");
    }

    // tail() as for-each source: all but the first.
    [Fact]
    public async Task Tail_ForEachSource_AllButFirst()
    {
        var result = await TransformWithFile(
            Sheet("""<xsl:for-each select="tail(r/i)"><x><xsl:value-of select="."/></x></xsl:for-each>"""),
            Xml, "r.xml");
        result.Trim().Should().Be("<out><x>2</x><x>3</x><x>4</x></out>");
    }

    // remove(seq, N) as for-each source: skip position N.
    [Fact]
    public async Task Remove_ForEachSource_SkipsPositionN()
    {
        var result = await TransformWithFile(
            Sheet("""<xsl:for-each select="remove(r/i, 2)"><x><xsl:value-of select="."/></x></xsl:for-each>"""),
            Xml, "r.xml");
        result.Trim().Should().Be("<out><x>1</x><x>3</x><x>4</x></out>");
    }

    // head() as simple-map LEFT: RIGHT consumes the single item.
    [Fact]
    public async Task Head_SimpleMapLeft_PerItemRight()
    {
        var result = await TransformWithFile(
            Sheet("""<xsl:value-of select="head(r/i) ! (number(.) + 1)"/>"""),
            Xml, "r.xml");
        result.Trim().Should().Be("<out>2</out>");
    }
}
