using FluentAssertions;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The streaming scanner walks an xsl:source-document body to register a ForEachSubscription for each streamable
/// for-each, and the dispatcher drives those during the streaming pass. Three arms of that walk could not see a map:
/// xsl:map and xsl:map-entry had no case at all (their children hang off a Content property, and the default arm
/// descends only into instructions that are themselves sequence constructors), and xsl:where-populated's case was a
/// bare break. So a map built under xsl:source-document produced nothing — its for-each never ran and its select
/// evaluated against the synthetic empty document (#117; W3C si-coco-014, si-map-006).
/// </summary>
public sealed class StreamedMapScannerTests
{
    private static XsltSequenceConstructor SourceDocumentBody(string body)
    {
        var xsl = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">{{body}}</xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var parser = new StylesheetParser(new XQueryExpressionParser());
        var stylesheet = parser.Parse(xsl);
        var sourceDoc = stylesheet.Templates[0].Body.Instructions[0] as XsltSourceDocument;
        sourceDoc.Should().NotBeNull("the parsed body should start with xsl:source-document");
        return sourceDoc!.Content!;
    }

    private static int SubscriptionCount(string body)
        => new StreamingExpressionScanner().ScanWithSubscriptions(SourceDocumentBody(body)).Subscriptions.Count;

    [Fact]
    public void A_for_each_inside_a_map_registers_a_subscription()
        => SubscriptionCount("""
                <xsl:map>
                  <xsl:for-each select="/*/*/ITEM">
                    <xsl:map-entry key="generate-id()" select="copy-of(TITLE)"/>
                  </xsl:for-each>
                </xsl:map>
                """).Should().Be(1);

    // W3C si-coco-014's shape: the map sits inside xsl:where-populated, whose arm used to stop the walk.
    [Fact]
    public void A_for_each_inside_a_map_inside_where_populated_registers_a_subscription()
        => SubscriptionCount("""
                <xsl:variable name="m" as="map(*)?">
                  <xsl:where-populated>
                    <xsl:map>
                      <xsl:for-each select="/*/*/ITEM">
                        <xsl:map-entry key="generate-id()" select="copy-of(TITLE)"/>
                      </xsl:for-each>
                    </xsl:map>
                  </xsl:where-populated>
                </xsl:variable>
                """).Should().Be(1);

    // W3C si-map-006's shape: no for-each at all, so xsl:map-entry needs its own arm.
    [Fact]
    public void A_for_each_inside_a_map_entry_body_registers_a_subscription()
        => SubscriptionCount("""
                <xsl:map>
                  <xsl:map-entry key="'k'">
                    <xsl:for-each select="/*/*/ITEM">
                      <xsl:value-of select="TITLE"/>
                    </xsl:for-each>
                  </xsl:map-entry>
                </xsl:map>
                """).Should().Be(1);

    // Deliberately unchanged: xsl:on-empty content may never be evaluated, so a for-each inside one must NOT
    // subscribe and dispatch against the stream for content that is then discarded.
    [Fact]
    public void A_for_each_inside_on_empty_still_registers_nothing()
        => SubscriptionCount("""
                <xsl:on-empty>
                  <xsl:for-each select="/*/*/ITEM">
                    <xsl:value-of select="TITLE"/>
                  </xsl:for-each>
                </xsl:on-empty>
                """).Should().Be(0);
}
