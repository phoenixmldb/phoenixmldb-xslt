using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XPath plans are kept per compiled stylesheet and reused by later transforms. Namespace ids come
/// from each transform's own node store, in the order that transform first sees each URI, so two
/// inputs that declare the same namespaces in different orders give the same URI different ids.
/// A reused plan must still match each input by the ids of its own store.
/// </summary>
public class SharedPlanTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:a="urn:a" xmlns:b="urn:b" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:template match="/">
            <xsl:value-of select="count(//a:x), count(//b:x), count(/*/*[self::a:x])" separator=","/>
            <xsl:text>|</xsl:text>
            <xsl:apply-templates select="/*/*"/>
          </xsl:template>
          <xsl:template match="a:x">A</xsl:template>
          <xsl:template match="b:x">B</xsl:template>
        </xsl:stylesheet>
        """;

    // The same namespaces, declared and first used in opposite orders.
    private static readonly string[] Inputs =
    [
        """<r xmlns:a="urn:a" xmlns:b="urn:b"><a:x/><b:x/><b:x/></r>""",
        """<r xmlns:b="urn:b" xmlns:a="urn:a"><b:x/><a:x/><a:x/><a:x/></r>""",
        """<b:r xmlns:b="urn:b"><b:x/><a:x xmlns:a="urn:a"/></b:r>""",
    ];

    private static readonly string[] Expected = ["1,2,1|ABB", "3,1,3|BAAA", "1,1,1|BA"];

    private static async Task<XsltTransformer> CompiledAsync()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Stylesheet);
        return transformer;
    }

    [Fact]
    public async Task OneTransformerUsedAgain_MatchesEachInputByItsOwnNamespaceIds()
    {
        var transformer = await CompiledAsync();
        for (var round = 0; round < 2; round++)
            for (var i = 0; i < Inputs.Length; i++)
                (await transformer.TransformAsync(Inputs[i])).Should().Be(Expected[i], $"input {i}, round {round}");
    }

    [Fact]
    public async Task TransformersInParallel_GiveTheSameResultsAsOneThread()
    {
        var tasks = Enumerable.Range(0, 8).Select(n => Task.Run(async () =>
        {
            var transformer = await CompiledAsync();
            var results = new List<string>();
            for (var round = 0; round < 5; round++)
                for (var i = 0; i < Inputs.Length; i++)
                    results.Add(await transformer.TransformAsync(Inputs[(i + n) % Inputs.Length]));
            return (n, results);
        })).ToArray();

        foreach (var (n, results) in await Task.WhenAll(tasks))
            for (var k = 0; k < results.Count; k++)
                results[k].Should().Be(Expected[(k % Inputs.Length + n) % Inputs.Length], $"transformer {n}, call {k}");
    }
}
