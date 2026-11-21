using AwesomeAssertions;
using PhoenixmlDb.Core;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A node from another transformation reaches a transformation through every entry point, and is
/// read in its own store there (BUGS.md #124).
/// </summary>
/// <remarks>
/// The producer's result tree is in the producer's store. The consumer's own input interns the
/// namespaces in the other order and holds nodes with the same ids, so reading the foreign node in
/// the consumer's store gives other nodes and other namespaces: the defect returned wrong counts
/// and names, silently.
/// </remarks>
public class ForeignNodeEntryPointTests
{
    private const string Expected = "1|2|1|y,z,y|2|urn:b|b";

    // Every consumer reports on $n (the foreign document) the same way.
    private const string Report =
        "count($n/a:x), count($n/a:x/b:y), count($n/a:x/a:z), string-join($n/a:x/*/local-name(), ','), " +
        "$n/a:x/b:y[2]/@n, namespace-uri($n/a:x/*[1]), string-join(($n/a:x/namespace::*[. = 'urn:b'] ! name()), ',')";

    private const string ConsumerInput = """<r xmlns:b="urn:b" xmlns:a="urn:a"><b:q/><a:q/><b:q/><a:q/></r>""";

    private static async Task<object> ProduceAsync()
        => (await ProduceSequenceAsync()).Single(i => i is Xdm.Nodes.XdmDocument)!;

    private static async Task<Xdm.XdmSequence> ProduceSequenceAsync()
    {
        var producer = new XsltTransformer();
        await producer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="xsl:initial-template">
                <a:x xmlns:a="urn:a" xmlns:b="urn:b"><b:y n="1"/><a:z/><b:y n="2"/></a:x>
              </xsl:template>
            </xsl:stylesheet>
            """);
        producer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await producer.TransformToSequenceAsync(null);
    }

    private static string Stylesheet(string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:a="urn:a" xmlns:b="urn:b" xmlns:f="urn:f" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          {{body}}
        </xsl:stylesheet>
        """;

    private static QName Name(string local) => new(NamespaceId.None, local);

    [Fact]
    public async Task Global_context_item()
    {
        var foreign = await ProduceAsync();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet($"""
            <xsl:global-context-item use="required"/>
            <xsl:variable name="n" select="."/>
            <xsl:template match="/"><xsl:value-of select="{Report}" separator="|"/></xsl:template>
            """));
        t.SetGlobalContextItem(foreign);
        (await t.TransformAsync(ConsumerInput)).Should().Be(Expected);
    }

    [Fact]
    public async Task Initial_template_parameter_and_tunnel_parameter()
    {
        var foreign = await ProduceAsync();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet($"""
            <xsl:template name="xsl:initial-template">
              <xsl:param name="n"/>
              <xsl:param name="tn" tunnel="yes"/>
              <xsl:value-of select="{Report}" separator="|"/>
              <xsl:text>#</xsl:text>
              <xsl:value-of select="count($tn/a:x/b:y)"/>
            </xsl:template>
            """));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        t.SetInitialTemplateParameter(Name("n"), foreign);
        t.SetInitialTunnelParameter(Name("tn"), foreign);
        (await t.TransformAsync((string?)null)).Should().Be(Expected + "#2");
    }

    [Fact]
    public async Task Initial_function_argument()
    {
        var foreign = await ProduceAsync();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet($"""
            <xsl:function name="f:report" visibility="public">
              <xsl:param name="n"/>
              <xsl:value-of select="{Report}" separator="|"/>
            </xsl:function>
            """));
        t.SetInitialFunction("report", "urn:f");
        t.AddInitialFunctionArgument(foreign);
        (await t.TransformAsync((string?)null)).Should().Be(Expected);
    }

    [Fact]
    public async Task Source_sequence()
    {
        // The producer's result sequence, which carries its store, is the source of this one.
        var produced = await ProduceSequenceAsync();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet($"""
            <xsl:template match="/"><xsl:variable name="n" select="."/><xsl:value-of select="{Report}" separator="|"/></xsl:template>
            """));
        (await t.TransformAsync(produced)).Should().Be(Expected);
    }
}
