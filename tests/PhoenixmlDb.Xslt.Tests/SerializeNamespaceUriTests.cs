using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:serialize</c> resolved a namespace id back to its URI only when the node provider was
/// an <c>XdmDocumentStore</c>. The XSLT engine has its own <c>XdmInMemoryStore</c>, so the URI
/// came back empty and every prefixed declaration serialized as <c>xmlns:p=""</c> — which XML
/// does not permit, because only the default namespace can be undeclared. The result was
/// markup that does not reparse, produced by a function whose entire job is to produce markup.
///
/// <c>xsl:result-document</c> was never affected: it uses the engine's own store-aware
/// serializer, which is what these overrides now route through.
/// </summary>
public class SerializeNamespaceUriTests
{
    private static async Task<string> Run(string body)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:e="urn:e"
                            exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:variable name="el" as="element()"><e:root><e:kid>v</e:kid></e:root></xsl:variable>
              <xsl:template match="/" name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync("<in/>").ConfigureAwait(true);
    }

    [Theory]
    [InlineData("serialize($el)")]
    [InlineData("serialize($el, map{'method':'xml'})")]
    [InlineData("serialize($el, map{'method':'adaptive'})")]
    public async Task Serialize_EmitsTheRealNamespaceUri(string call)
    {
        var r = await Run($"""<xsl:value-of select="{call}"/>""").ConfigureAwait(true);
        r.Should().Contain("xmlns:e=\"urn:e\"");
        r.Should().NotContain("xmlns:e=\"\"");
    }

    [Fact]
    public async Task Serialize_OutputReparses()
    {
        // The point of the bug: xmlns:e="" is not well-formed, so the markup could not be read
        // back. Parsing it here is the assertion that actually matters.
        var r = await Run("""<xsl:value-of select="serialize($el, map{'method':'xml'})"/>""")
            .ConfigureAwait(true);
        var act = () => XDocument.Parse(r);
        act.Should().NotThrow();
        XDocument.Parse(r).Root!.Name.NamespaceName.Should().Be("urn:e");
    }

    [Fact]
    public async Task Serialize_RoundTripsThroughParseXml()
    {
        // Same assertion from inside the stylesheet, which is how a user hits it.
        var r = await Run(
            """<xsl:value-of select="namespace-uri(parse-xml(serialize($el, map{'method':'xml'}))/*)"/>""")
            .ConfigureAwait(true);
        r.Should().Be("urn:e");
    }

    [Fact]
    public async Task Serialize_NamespacedNodeInsideAMap_KeepsItsUri()
    {
        var r = await Run("""<xsl:value-of select="serialize(map{'k':$el}, map{'method':'adaptive'})"/>""")
            .ConfigureAwait(true);
        r.Should().Contain("map{");
        r.Should().Contain("xmlns:e=\"urn:e\"");
    }

    [Fact]
    public async Task Serialize_SequenceValuedMapEntry_SerializesEachItem()
    {
        // The engine's adaptive serializer had no case for a sequence, so a map entry holding
        // element()+ became its string value instead of the items' markup.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:map="http://www.w3.org/2005/xpath-functions/map">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="m" as="map(xs:string, element()+)">
                  <xsl:map><xsl:map-entry key="'all'" select="//item"/></xsl:map>
                </xsl:variable>
                <xsl:value-of select="serialize($m, map{'method':'adaptive'})"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        var r = await t.TransformAsync("<root><item id='1'>a</item><item id='2'>b</item></root>")
            .ConfigureAwait(true);
        r.Should().Contain("<item");
        r.Should().Contain("id=\"1\"");
        r.Should().Contain("id=\"2\"");
    }

    [Fact]
    public async Task Serialize_AttributeAtTopLevel_StillRaisesSenr0001()
    {
        // The override must keep the error contract, not just the happy path.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:value-of select="serialize(/root/item/@id, map{'method':'xml'})"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        var act = async () => await t.TransformAsync("<root><item id='1'>a</item></root>")
            .ConfigureAwait(true);
        (await act.Should().ThrowAsync<System.Exception>().ConfigureAwait(true))
            .Which.Message.Should().Contain("cannot be serialized at the top level");
    }
}
