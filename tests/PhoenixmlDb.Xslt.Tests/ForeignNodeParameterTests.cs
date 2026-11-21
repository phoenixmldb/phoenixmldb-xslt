using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A node made by another transformer (its own node store, its own namespace ids) and handed to a
/// transform as a parameter must be navigated and matched by its own names, whatever ids the
/// receiving transform's store gave the same URIs.
/// </summary>
public class ForeignNodeParameterTests
{
    private const string Producer = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:template name="xsl:initial-template">
            <a:x xmlns:a="urn:a" xmlns:b="urn:b"><b:y n="1"/><a:z/><b:y n="2"/></a:x>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // The consumer's own input declares and uses urn:b first, so its store interns them in the
    // other order.
    private const string ConsumerInput = """<r xmlns:b="urn:b" xmlns:a="urn:a"><b:q/><a:q/></r>""";

    private const string Consumer = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:a="urn:a" xmlns:b="urn:b" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:param name="p"/>
          <xsl:template match="/">
            <xsl:variable name="x" select="$p/a:x"/>
            <xsl:value-of select="count($x), count($x/b:y), count($x/a:z), string-join($x/*/local-name(), ','), $x/b:y[2]/@n, count(//b:q), count(//a:q)" separator="|"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task NodeFromAnotherTransformer_IsMatchedByItsNamespaces()
    {
        var producer = new XsltTransformer();
        await producer.LoadStylesheetAsync(Producer);
        producer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        var produced = await producer.TransformToSequenceAsync(null);
        var items = produced.ToList();
        var x = items.Single(i => i is Xdm.Nodes.XdmDocument); // the result tree's document node

        var consumer = new XsltTransformer();
        await consumer.LoadStylesheetAsync(Consumer);
        consumer.SetParameter("p", x);
        var result = await consumer.TransformAsync(ConsumerInput);

        result.Should().Be("1|2|1|y,z,y|2|1|1");
    }

    [Fact]
    public async Task NodeFromAnotherTransformer_NoNamespaces_IsNavigable()
    {
        var producer = new XsltTransformer();
        await producer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="xsl:initial-template"><x><y n="1"/><z/><y n="2"/></x></xsl:template>
            </xsl:stylesheet>
            """);
        producer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        var doc = (await producer.TransformToSequenceAsync(null)).Single();

        var consumer = new XsltTransformer();
        await consumer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:param name="p"/>
              <xsl:template match="/">
                <xsl:value-of select="count($p/*), count($p/x/y), string-join($p/x/*/local-name(), ','), $p/x/y[2]/@n, name($p/*)" separator="|"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        consumer.SetParameter("p", doc);
        (await consumer.TransformAsync("<r><q/><q/><q/><q/><q/></r>")).Should().Be("1|2|y,z,y|2|x");
    }
}
