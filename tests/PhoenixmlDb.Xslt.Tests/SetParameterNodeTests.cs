using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A LINQ to XML or System.Xml node passed to SetParameter is usable as a node in the stylesheet (xslt#12). It arrived
/// as a foreign .NET object: $map instance of node() was false and $map//e failed with "context item is not a node
/// (got item of type XDocument)". The string-parameter test is a guard.
/// </summary>
public sealed class SetParameterNodeTests
{
    private const string Markup = "<m><e id='a'>ALPHA</e><e id='b'>BETA</e></m>";

    private static async Task<string> RunAsync(object? value, string select)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:param name="map"/>
              <xsl:template match="/"><xsl:value-of select="{{select}}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetParameter("map", value);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Fact]
    public async Task An_XDocument_parameter_is_a_navigable_document_node()
        => (await RunAsync(XDocument.Parse(Markup), "($map instance of document-node()) and $map//e[@id='a'] = 'ALPHA'"))
            .Should().Be("true");

    [Fact]
    public async Task An_XElement_parameter_is_a_navigable_element_node()
        => (await RunAsync(XElement.Parse(Markup), "($map instance of element(m)) and $map/e[@id='b'] = 'BETA'"))
            .Should().Be("true");

    [Fact]
    public async Task An_XmlDocument_parameter_is_a_navigable_document_node()
    {
        var doc = new XmlDocument();
        doc.LoadXml(Markup);
        (await RunAsync(doc, "string-join($map//e, ',')")).Should().Be("ALPHA,BETA");
    }

    // A string that looks like markup stays a string: only node objects are converted.
    [Fact]
    public async Task A_string_parameter_is_not_parsed_into_a_node()
        => (await RunAsync("<m/>", "not($map instance of node()) and string-length($map) = 4"))
            .Should().Be("true");
}
