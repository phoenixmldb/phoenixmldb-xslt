using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The <c>type</c> attribute on <c>xsl:attribute</c>, <c>xsl:copy</c> and <c>xsl:copy-of</c>
/// (XSLT 3.0 §25.4.1). On <c>xsl:attribute</c> the value was not checked; on the two copy
/// instructions the stylesheet was refused when it was compiled (xslt#364).
/// </summary>
public sealed class TypeOnAttributeAndCopyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-type-copy").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string body)
    {
        var xsd = Path.Combine(_dir, "order.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o" xmlns:o="urn:o" elementFormDefault="qualified">
              <xs:complexType name="orderType"><xs:sequence><xs:element name="qty" type="xs:integer"/></xs:sequence></xs:complexType>
              <xs:simpleType name="small"><xs:restriction base="xs:integer"><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:o"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema namespace="urn:o" schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              <xsl:variable name="good"><a n="7"><o:qty>1</o:qty></a><b><o:qty>2</o:qty></b></xsl:variable>
              <xsl:variable name="bad"><a n="70"><o:qty>x</o:qty></a></xsl:variable>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<in/>");
    }

    private async Task<string> CodeAsync(string body)
    {
        var run = () => RunAsync(body);
        var message = (await run.Should().ThrowAsync<XsltException>()).Which.Message;
        return message[..message.IndexOf(':', StringComparison.Ordinal)];
    }

    [Theory]
    // xsl:attribute: a built-in type and a type of the schema
    [InlineData("<e><xsl:attribute name=\"n\" type=\"xs:integer\">x</xsl:attribute></e>", "XTTE1540")]
    [InlineData("<e><xsl:attribute name=\"n\" type=\"o:small\">70</xsl:attribute></e>", "XTTE1540")]
    [InlineData("<e><xsl:attribute name=\"n\" type=\"o:small\" select=\"70\"/></e>", "XTTE1540")]
    // an attribute cannot have a complex type
    [InlineData("<e><xsl:attribute name=\"n\" type=\"o:orderType\">7</xsl:attribute></e>", "XTTE1540")]
    [InlineData("<e><xsl:attribute name=\"n\" type=\"o:nope\">7</xsl:attribute></e>", "XTSE1520")]
    // xsl:copy-of: an element, each of several elements, an attribute
    [InlineData("<xsl:copy-of select=\"$bad/a\" type=\"o:orderType\"/>", "XTTE1540")]
    [InlineData("<xsl:copy-of select=\"$good/b, $bad/a\" type=\"o:orderType\"/>", "XTTE1540")]
    [InlineData("<e><xsl:copy-of select=\"$bad/a/@n\" type=\"o:small\"/></e>", "XTTE1540")]
    [InlineData("<e><xsl:copy-of select=\"$good/a/@n\" type=\"o:orderType\"/></e>", "XTTE1535")]
    // xsl:copy
    [InlineData("<xsl:for-each select=\"$bad/a\"><xsl:copy type=\"o:orderType\"><xsl:copy-of select=\"*\"/></xsl:copy></xsl:for-each>", "XTTE1540")]
    [InlineData("<e><xsl:for-each select=\"$bad/a/@n\"><xsl:copy type=\"o:small\"/></xsl:for-each></e>", "XTTE1540")]
    [InlineData("<e><xsl:copy select=\"$good/a/@n\" type=\"o:orderType\"/></e>", "XTTE1535")]
    public async Task What_is_not_valid_against_the_type_is_refused(string body, string code)
        => (await CodeAsync(body)).Should().Be(code);

    [Theory]
    [InlineData("<e><xsl:attribute name=\"n\" type=\"xs:integer\">12</xsl:attribute></e>", "<e n=\"12\"/>")]
    [InlineData("<e><xsl:attribute name=\"n\" type=\"o:small\">7</xsl:attribute></e>", "<e n=\"7\"/>")]
    [InlineData("<xsl:copy-of select=\"$good/b\" type=\"o:orderType\"/>", "<b><o:qty xmlns:o=\"urn:o\">2</o:qty></b>")]
    [InlineData("<e><xsl:copy-of select=\"$good/a/@n\" type=\"o:small\"/></e>", "<e n=\"7\"/>")]
    [InlineData("<xsl:for-each select=\"$good/b\"><xsl:copy type=\"o:orderType\"><xsl:copy-of select=\"*\"/></xsl:copy></xsl:for-each>", "<b><o:qty xmlns:o=\"urn:o\">2</o:qty></b>")]
    [InlineData("<e><xsl:for-each select=\"$good/a/@n\"><xsl:copy type=\"o:small\"/></xsl:for-each></e>", "<e n=\"7\"/>")]
    // text is copied as it is: a type is for elements and attributes
    [InlineData("<e><xsl:for-each select=\"$good/b/o:qty/text()\"><xsl:copy type=\"o:orderType\"/></xsl:for-each></e>", "<e>2</e>")]
    public async Task What_is_valid_against_the_type_is_written_as_it_is(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
