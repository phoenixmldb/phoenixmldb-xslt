using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A nested xsl:apply-templates in a streamed template reads each child whole and runs its
/// template on that copy. The copy stayed in the node store to the end of the transform, so
/// memory grew with the input: 942 MiB for 1M items (xslt#529). It is now released when its
/// template is done. Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedNestedDispatchMemoryTests : IDisposable
{
    private const int Items = 1000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-nested-" + Guid.NewGuid().ToString("N"));

    public StreamedNestedDispatchMemoryTests()
    {
        Directory.CreateDirectory(_dir);
        var xml = new StringBuilder("<root>");
        for (var i = 1; i <= Items; i++)
            xml.Append("<item v=\"").Append(i).Append("\"><n>").Append(i).Append("</n></item>");
        File.WriteAllText(Path.Combine(_dir, "d.xml"), xml.Append("</root>").ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(string Result, int StoreNodes)> Run(string rules)
    {
        var store = new XdmInMemoryStore();
        var doc = new XdmDocument
        {
            StringValueResolver = store.StringValueResolver,
            Id = store.NextId(),
            Document = new DocumentId(1),
            Children = [],
        };
        store.Register(doc);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:mode name="s" streamable="yes" on-no-match="shallow-skip" use-accumulators="#all"/>
              <xsl:accumulator name="c" as="xs:integer" initial-value="0" streamable="yes">
                <xsl:accumulator-rule match="item" phase="end" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:template match="/">
                <xsl:source-document streamable="yes" href="d.xml" use-accumulators="#all"><xsl:apply-templates mode="s"/></xsl:source-document>
              </xsl:template>
              <xsl:template match="root" mode="s"><xsl:apply-templates mode="s"/></xsl:template>
              {{rules}}
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        var result = await t.TransformAsync(Xdm.XdmSequence.OfNode(doc, store));
        return (result.Trim(), store.GetAllNodes().Count());
    }

    [Theory]
    [InlineData("<xsl:template match=\"item[@v mod 250 = 0]\" mode=\"s\"><xsl:value-of select=\"accumulator-before('c'), n\"/><xsl:text> </xsl:text></xsl:template>",
        "249 250 499 500 749 750 999 1000")]
    [InlineData("<xsl:template match=\"item[@v mod 500 = 0]/n\" mode=\"s\"><xsl:value-of select=\".\"/><xsl:text> </xsl:text></xsl:template>",
        "500 1000")]
    public async Task The_children_read_whole_are_not_kept(string rules, string expected)
    {
        var (result, storeNodes) = await Run(rules);
        result.Should().Be(expected);
        storeNodes.Should().BeLessThan(Items / 10);
    }
}
