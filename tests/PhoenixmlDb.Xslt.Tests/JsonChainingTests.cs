using FluentAssertions;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Chaining two XSLT 3.0 transforms where the first yields a map (typically via
/// fn:parse-json) and the second consumes it as its context item. Martin Honnen's
/// 2026-05-21 repro: TransformToSequenceAsync silently dropped non-node head items
/// and substituted a synthetic &lt;empty/&gt; document, so the second transform's
/// `match="."` template saw a document node and `?name` raised
/// "Lookup requires a map or array, got XdmDocument".
/// </summary>
public sealed class JsonChainingTests
{
    [Fact]
    public async Task TwoStage_ParseJsonThenLookupInSecondTransform()
    {
        const string parseStage = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="adaptive"/>
              <xsl:param name="json" as="xs:string"/>
              <xsl:template name="xsl:initial-template">
                <xsl:sequence select="parse-json($json)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        const string consumeStage = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match=".">
                <person>
                  <name>{?name}</name>
                  <age>{?age}</age>
                  <city>{?city}</city>
                </person>
              </xsl:template>
            </xsl:stylesheet>
            """;

        const string jsonSample = """
            { "name": "John", "age": 30, "city": "New York" }
            """;

        // Stage 1: parse JSON via the initial template, get back a map in an XdmSequence.
        var parser = new XsltTransformer();
        parser.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        await parser.LoadStylesheetAsync(parseStage);
        parser.SetParameter("json", jsonSample);

        var parsed = await parser.TransformToSequenceAsync((XdmSequence?)null);
        parsed.Count.Should().Be(1);
        parsed.Head.Should().BeAssignableTo<System.Collections.Generic.IDictionary<object, object?>>();

        // Stage 2: feed the map back in. Pre-fix the engine substituted an <empty/>
        // XdmDocument and `?name` failed with "Lookup requires a map or array".
        var consumer = new XsltTransformer();
        await consumer.LoadStylesheetAsync(consumeStage);

        var consumed = await consumer.TransformToSequenceAsync(parsed);

        // The consumer template returned a <person> element built from the map's
        // keys, wrapped in a result document by the XSLT engine (XSLT 3.0 §5.7.1:
        // sequence constructors yield a document node when the result needs one).
        // The string-value collapses to the concatenated text of <name>+<age>+<city>;
        // that the lookups landed at all is what this regression actually proves.
        var head = consumed.Head;
        head.Should().BeAssignableTo<PhoenixmlDb.Xdm.Nodes.XdmDocument>();
        var stringValue = ((PhoenixmlDb.Xdm.Nodes.XdmDocument)head!).StringValue ?? "";
        stringValue.Should().Contain("John");
        stringValue.Should().Contain("30");
        stringValue.Should().Contain("New York");
    }
}
