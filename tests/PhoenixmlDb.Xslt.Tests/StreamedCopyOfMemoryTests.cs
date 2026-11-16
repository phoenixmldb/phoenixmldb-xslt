using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed pass keeps only what its result holds (xslt#520). Each element a predicate on the
/// path was tested on was built in the node store and stayed there to the end of the transform,
/// so <c>copy-of(root/item[@v &lt; -990]/@v)</c> in a variable, or <c>sum()</c> of the same path,
/// grew with the input: 1M items used 650 MiB where <c>sum(root/item)</c> uses 100 MiB. With a
/// grounded operand beside it, <c>(copy-of(path), $extra)</c> read the whole input into memory
/// first (1.26 GiB). And an unpredicated <c>copy-of(root/item/@v)</c> in a variable was empty.
/// Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedCopyOfMemoryTests : IDisposable
{
    private const int Items = 1000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-cpymem-" + Guid.NewGuid().ToString("N"));

    public StreamedCopyOfMemoryTests()
    {
        Directory.CreateDirectory(_dir);
        var xml = new StringBuilder("<root>");
        for (var i = 1; i <= Items; i++)
            xml.Append("<item v=\"").Append(i % 2 == 0 ? -i : i).Append("\">").Append(i).Append("</item>");
        File.WriteAllText(Path.Combine(_dir, "d.xml"), xml.Append("</root>").ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Stylesheet(string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/">
            <xsl:variable name="extra" as="element()*"><x v="10"/></xsl:variable>
            <xsl:source-document streamable="yes" href="d.xml">{{body}}</xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    /// <summary>Runs on a store of the test's own, and gives the result and the nodes left in it.</summary>
    private async Task<(string Result, int StoreNodes)> Run(string body)
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
        await t.LoadStylesheetAsync(Stylesheet(body), new Uri(Path.Combine(_dir, "main.xsl")));
        var result = await t.TransformAsync(Xdm.XdmSequence.OfNode(doc, store));
        return (result.Trim(), store.GetAllNodes().Count());
    }

    public static TheoryData<string, string> Shapes => new()
    {
        // The variable form.
        { "<xsl:variable name='c' select='copy-of(root/item[@v &lt; -990]/@v)'/><xsl:value-of select='$c'/>",
            "-992 -994 -996 -998 -1000" },
        // The sequence form, and the copy-of((path, $x)) that is rewritten to it.
        { "<xsl:variable name='c' select='(copy-of(root/item[@v &lt; -990]/@v), copy-of($extra/@v))'/><xsl:value-of select='$c'/>",
            "-992 -994 -996 -998 -1000 10" },
        { "<xsl:variable name='c' select='copy-of((root/item[@v &lt; -990]/@v, $extra/@v))'/><xsl:value-of select='$c'/>",
            "-992 -994 -996 -998 -1000 10" },
        // Martin's sum shape (xslt#461) with a predicate.
        { "<xsl:value-of select='sum(root/item[@v &lt; -990]/@v)'/>", "-4980" },
        // Elements, and a predicate on the attribute.
        { "<xsl:variable name='c' select='copy-of(root/item[@v &lt; -990])'/><xsl:value-of select='$c'/>",
            "992 994 996 998 1000" },
        { "<xsl:variable name='c' select='copy-of(root/item/@v[. &lt; -990])'/><xsl:value-of select='$c'/>",
            "-992 -994 -996 -998 -1000" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_result_is_right(string body, string expected)
        => (await Run(body)).Result.Should().Be(expected);

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_nodes_left_do_not_grow_with_the_input(string body, string expected)
    {
        var (result, storeNodes) = await Run(body);
        result.Should().Be(expected);
        // The kept nodes, the stylesheet's own and a few per level: far below one per item.
        storeNodes.Should().BeLessThan(Items / 10);
    }

    [Theory]
    [InlineData("<xsl:value-of select='count($c)'/>", "1000")]
    [InlineData("<xsl:value-of select='count($c), $c[3]'/>", "1000 3")]
    [InlineData("<xsl:value-of select='$c instance of attribute()+'/>", "true")]
    public async Task An_unpredicated_attribute_copy_holds_the_attributes(string use, string expected)
        => (await Run("<xsl:variable name='c' select='copy-of(root/item/@v)'/>" + use)).Result.Should().Be(expected);
}
