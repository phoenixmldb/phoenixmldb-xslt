using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The processor is XSD 1.1 (system-property('xsl:xsd-version') = 1.1), and behaves as one:
/// xs:dateTimeStamp is an available type, and xs:anyURI accepts any string, so xsl:namespace does
/// not reject "####" (XTDE0905 under XSD 1.0 only). type-available also takes an EQName.
/// </summary>
public sealed class XsdOneOneBehaviourTests
{
    private static async Task<string> RunAsync(string body)
    {
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform' xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:other='urn:other'>" +
                  "<xsl:output method='xml' omit-xml-declaration='yes'/>" +
                  "<xsl:template name='xsl:initial-template'>" + body + "</xsl:template></xsl:stylesheet>";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task Type_available_resolves_names_and_knows_dateTimeStamp()
        => (await RunAsync("<out><xsl:value-of select=\"type-available('Q{http://www.w3.org/2001/XMLSchema}dateTimeStamp'), " +
                           "type-available('xs:dateTimeStamp'), type-available('Q{http://www.w3.org/2001/XMLSchema}date'), " +
                           "type-available('other:date')\"/></out>"))
            .Should().Contain(">true true true false<");

    [Fact]
    public async Task A_namespace_node_accepts_any_anyURI_string()
        => (await RunAsync("<out><xsl:namespace name='ns' select=\"'####'\"/></out>"))
            .Should().Contain("xmlns:ns=\"####\"");
}
