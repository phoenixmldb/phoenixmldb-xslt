using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 decl/output slice (output-0702 / output-0716 / output-0717).
/// When the json output method serializes a NODE and json-node-output-method is in effect,
/// the node must be SERIALIZED using that method (producing markup as the JSON string value),
/// not reduced to its string-value. Default node method is "xml"; when "html", nodes serialize
/// with the HTML method (Content-Type meta inserted, HTML void-element handling).
/// </summary>
public sealed class JsonNodeOutputMethodTests
{
    private static XsltTransformer InitialTemplateTransformer()
    {
        var t = new XsltTransformer();
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return t;
    }

    // output-0716 shape: json-node-output-method="html", array of element nodes.
    // The <head> node must serialize as HTML markup with a Content-Type <meta>.
    [Fact]
    public async Task JsonHtmlNodeMethod_SerializesNodeAsHtmlMarkupWithContentTypeMeta()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="json" json-node-output-method="html"/>
              <t:template name="t:initial-template">
                <t:variable name="e1" as="element()"><head><title>A document</title></head></t:variable>
                <t:variable name="e2" as="element()"><body><p>Some content</p></body></t:variable>
                <t:variable name="array" select="[$e1, $e2]"/>
                <t:sequence select="$array"/>
              </t:template>
            </t:transform>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        // Must be HTML markup (not the string-value "A document"), with the Content-Type meta.
        r.Should().Contain("<head>", because: "the node must be serialized as HTML markup, not its string-value");
        r.Should().Contain("<meta http-equiv=\\\"Content-Type\\\"", because: "html node-output-method inserts a Content-Type meta");
        r.Should().NotContain("\"A document\"", because: "the node must not be reduced to its string-value");
    }

    // output-0717 shape: json-node-output-method="xml" (the default) → XML serialization, no meta.
    [Fact]
    public async Task JsonXmlNodeMethod_SerializesNodeAsXmlMarkupNoMeta()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="json" json-node-output-method="xml"/>
              <t:template name="t:initial-template">
                <t:variable name="e1" as="element()"><head><title>A document</title></head></t:variable>
                <t:variable name="e2" as="element()"><body><p>Some content</p></body></t:variable>
                <t:variable name="array" select="[$e1, $e2]"/>
                <t:sequence select="$array"/>
              </t:template>
            </t:transform>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Should().Contain("<head><title", because: "xml node-output-method serializes the node as XML markup");
        r.Should().NotContain("meta http-equiv", because: "xml serialization inserts no Content-Type meta");
    }

    // output-0702 shape: json map whose values are element nodes built directly by <xsl:sequence>
    // (a node, NOT map-entry LRE content — see the note below). The html node-output-method must
    // serialize the node value with the Content-Type meta.
    [Fact]
    public async Task JsonHtmlNodeMethod_MapWithNodeValue_InsertsContentTypeMeta()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="json" indent="no" json-node-output-method="html"/>
              <t:template name="t:initial-template">
                <t:variable name="d1" as="element()"><html><head><title>Document 1</title></head><body><p>Content 1</p></body></html></t:variable>
                <t:sequence select="map { 'doc1' : $d1 }"/>
              </t:template>
            </t:transform>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Should().Contain("\"doc1\":", because: "the map key is serialized as a JSON member");
        r.Should().Contain("<meta http-equiv=\\\"Content-Type\\\"", because: "html node-output-method inserts the Content-Type meta into <head>");
        r.Should().Contain("<title>Document 1", because: "the node map value is serialized as HTML markup, not its string-value");
    }

    // Guard: a non-node atomic value must be unaffected by json-node-output-method.
    [Fact]
    public async Task JsonHtmlNodeMethod_AtomicValueUnaffected()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="json" json-node-output-method="html"/>
              <t:template name="t:initial-template">
                <t:sequence select="['plain', 42]"/>
              </t:template>
            </t:transform>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Should().Contain("\"plain\"").And.Contain("42");
        r.Should().NotContain("meta http-equiv", because: "atomic values are not nodes and get no HTML treatment");
    }
}
