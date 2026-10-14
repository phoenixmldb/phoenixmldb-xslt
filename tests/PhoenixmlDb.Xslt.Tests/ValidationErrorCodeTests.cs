using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A constructed node that fails validation has the error code of XSLT 3.0 §25.4.1: XTTE1510
/// (strict, not valid), XTTE1512 (strict, no top-level declaration), XTTE1515 (lax, not valid).
/// It had XQDY0027, the code of XQuery's validate expression, so a stylesheet that caught the
/// error by its code did not catch it (xslt#354).
/// </summary>
public sealed class ValidationErrorCodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-validation-codes").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<XsltTransformer> LoadAsync(string body)
    {
        var xsd = Path.Combine(_dir, "order.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o" xmlns:o="urn:o" elementFormDefault="qualified">
              <xs:element name="order"><xs:complexType><xs:sequence><xs:element name="qty" type="xs:integer"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:o" xmlns:err="http://www.w3.org/2005/xqt-errors"
                            version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema namespace="urn:o" schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return t;
    }

    private const string Invalid = "<o:qty>x</o:qty>";

    public static TheoryData<string, string> Failures => new()
    {
        { $"<o:order xsl:validation=\"strict\">{Invalid}</o:order>", "XTTE1510" },
        { $"<xsl:element name=\"o:order\" validation=\"strict\">{Invalid}</xsl:element>", "XTTE1510" },
        { $"<xsl:variable name=\"v\"><o:order>{Invalid}</o:order></xsl:variable><xsl:copy-of select=\"$v/*\" validation=\"strict\"/>", "XTTE1510" },
        { $"<xsl:variable name=\"d\"><xsl:document validation=\"strict\"><o:order>{Invalid}</o:order></xsl:document></xsl:variable><r/>", "XTTE1510" },
        { "<o:nope xsl:validation=\"strict\"/>", "XTTE1512" },
        { "<xsl:element name=\"o:nope\" validation=\"strict\"/>", "XTTE1512" },
        { "<xsl:variable name=\"d\"><xsl:document validation=\"strict\"><o:nope/></xsl:document></xsl:variable><r/>", "XTTE1512" },
        { "<e><xsl:attribute name=\"o:a\" validation=\"strict\">1</xsl:attribute></e>", "XTTE1512" },
        { $"<o:order xsl:validation=\"lax\">{Invalid}</o:order>", "XTTE1515" },
        { $"<xsl:element name=\"o:order\" validation=\"lax\">{Invalid}</xsl:element>", "XTTE1515" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_error_has_the_code_of_XSLT(string body, string code)
    {
        var t = await LoadAsync(body);
        var run = () => t.TransformAsync("<in/>");
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith(code + ":");
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_stylesheet_catches_it_by_that_code(string body, string code)
    {
        var t = await LoadAsync($"<xsl:try>{body}<xsl:catch errors=\"err:{code}\">caught</xsl:catch></xsl:try>");
        (await t.TransformAsync("<in/>")).Should().Be("caught");
    }

    [Theory]
    [InlineData("<o:order xsl:validation=\"strict\"><o:qty>1</o:qty></o:order>", "<o:order xmlns:o=\"urn:o\"><o:qty>1</o:qty></o:order>")]
    // lax: what has no declaration is not assessed
    [InlineData("<o:nope xsl:validation=\"lax\"/>", "<o:nope xmlns:o=\"urn:o\"/>")]
    public async Task Valid_content_and_content_that_lax_does_not_assess_pass(string body, string expected)
    {
        var t = await LoadAsync(body);
        (await t.TransformAsync("<in/>")).Should().Be(expected);
    }
}
