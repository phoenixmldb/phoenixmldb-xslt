using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The serialization attributes of an xsl:result-document apply to its result, over those of
/// its xsl:output (XSLT 3.0 §25.1). normalization-form and encoding were not read, and an
/// href-less result-document with an unnamed xsl:output used that declaration as it was, so its
/// own indent, omit-xml-declaration, standalone, byte-order-mark and encoding were dropped
/// (W3C normalize-unicode-016). Expected values follow Saxon-HE 12.10. "u" followed by U+0308 is
/// "ü" in NFC.
/// </summary>
public sealed class ResultDocumentOwnSerializationTests
{
    private const string Decomposed = "u&#x308;";
    private const string Composed = "ü";

    private static async Task<XsltTransformer> Load(string resultDocument)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">{{resultDocument}}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return t;
    }

    [Theory]
    [InlineData("NFC", Composed)]
    [InlineData("{'NF' || 'C'}", Composed)]
    [InlineData("none", "ü")]
    public async Task The_principal_result_document_is_normalized(string form, string expected)
    {
        var t = await Load($"<xsl:result-document normalization-form=\"{form}\"><r a='{Decomposed}'>{Decomposed}</r></xsl:result-document>");
        (await t.TransformAsync((string?)null)).Should().Contain($"<r a=\"{expected}\">{expected}</r>");
    }

    [Theory]
    [InlineData("indent='yes'", "<r>\n  <c>x</c>\n</r>")]
    [InlineData("omit-xml-declaration='no'", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><r><c>x</c></r>")]
    [InlineData("omit-xml-declaration='no' standalone='yes'", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><r>")]
    [InlineData("omit-xml-declaration='no' encoding='iso-8859-1'", "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><r>")]
    [InlineData("method='text'", "x")]
    public async Task The_result_document_attributes_win_over_the_unnamed_output(string attributes, string expected)
    {
        var t = await Load($"<xsl:result-document {attributes}><r><c>x</c></r></xsl:result-document>");
        (await t.TransformAsync((string?)null)).Trim().Should().Contain(expected);
    }

    [Fact]
    public async Task A_byte_order_mark_is_written_when_asked_for()
    {
        var t = await Load("<xsl:result-document byte-order-mark='yes'><r/></xsl:result-document>");
        (await t.TransformAsync((string?)null)).Should().StartWith("\uFEFF");
    }

    [Fact]
    public async Task A_secondary_result_document_is_normalized()
    {
        var t = await Load($"<xsl:result-document href='s.xml' normalization-form='NFC'><r>{Decomposed}</r></xsl:result-document><done/>");
        await t.TransformAsync((string?)null);
        t.SecondaryResultDocuments.Values.Should().ContainSingle().Which.Should().Contain($"<r>{Composed}</r>");
    }
}
