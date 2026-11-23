using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The engine gave some nodes of a streamed input ids of their own, in the id space of the
/// transform's store, whose ids start at 1: the document node of xsl:source-document 999 999,
/// the streaming processor's nodes from 1 000 000 and an element read whole for a dispatch from
/// 5 000 000. Once the store had given out about a million ids, a node it built had the id of a
/// streamed node still in it, such as the open root element, and one of the two was found for the
/// other. With temporary trees in stores of their own (xslt#516) the store's node was found where
/// the tree's was asked for: an item's name came out as <c>&lt;root/&gt;</c> (300 000 items).
/// </summary>
public sealed class StreamedNodeIdRangeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-idrange").FullName;

    public StreamedNodeIdRangeTests()
    {
        var items = string.Concat(Enumerable.Range(1, 12).Select(i => $"<item><name>n{i}</name><category>c{(i + 2) / 3}</category></item>"));
        File.WriteAllText(Path.Combine(_dir, "d.xml"), $"<root>{items}</root>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Sheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:mode name="s" streamable="yes" on-no-match="shallow-copy"/>
          <xsl:template match="/">
            <xsl:source-document streamable="yes" href="d.xml"><xsl:apply-templates mode="s"/></xsl:source-document>
          </xsl:template>
          <xsl:template match="root" mode="s">
            <xsl:copy>
              <xsl:for-each-group select="item!copy-of()" group-adjacent="category">
                <xsl:variable name="first"><x><xsl:value-of select="current-group()[1]/name"/></x></xsl:variable>
                <g k="{current-grouping-key()}" first="{$first/x}">{current-group()/name}</g>
              </xsl:for-each-group>
            </xsl:copy>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private async Task<string> Run(ulong idsGivenBefore)
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
        // A store that has already given out this many ids, as after a large part of a big input.
        store.AdvanceNextIdBy(idsGivenBefore);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Sheet, new Uri(Path.Combine(_dir, "main.xsl")));
        return (await t.TransformAsync(Xdm.XdmSequence.OfNode(doc, store))).Trim();
    }

    [Fact]
    public async Task A_store_that_gave_out_a_million_ids_gives_the_same_result()
    {
        var fresh = await Run(0);
        fresh.Should().Be("<root><g k=\"c1\" first=\"n1\">n1 n2 n3</g><g k=\"c2\" first=\"n4\">n4 n5 n6</g>"
            + "<g k=\"c3\" first=\"n7\">n7 n8 n9</g><g k=\"c4\" first=\"n10\">n10 n11 n12</g></root>");
        // Every count of ids already given out from just below the fixed ids to past them, so that
        // each node the transform builds has, in one of the runs, an id a streamed node had.
        var differing = new List<ulong>();
        for (var before = 999_920UL; before <= 1_000_000UL; before++)
        {
            if (await Run(before) != fresh)
                differing.Add(before);
        }
        differing.Should().BeEmpty();
    }
}
