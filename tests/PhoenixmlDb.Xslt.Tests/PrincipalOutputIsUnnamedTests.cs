using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The principal result is serialized by the unnamed output definition (XSLT 3.0 §26). The first
/// xsl:output declaration was taken, so a named one written before the unnamed one serialized
/// the principal result: a named text output emptied an xhtml result (W3C output-0134).
/// </summary>
public sealed class PrincipalOutputIsUnnamedTests
{
    private static async Task<string> Run(string outputs)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              {{outputs}}
              <xsl:template name="xsl:initial-template"><out><e/></out></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("<xsl:output name='n' method='text'/><xsl:output method='xml' omit-xml-declaration='yes'/>", "<out><e/></out>")]
    [InlineData("<xsl:output name='n' method='xml' indent='yes'/><xsl:output method='xml' omit-xml-declaration='yes'/>", "<out><e/></out>")]
    [InlineData("<xsl:output name='n' method='text'/>", "<out><e/></out>")] // as with no xsl:output at all
    [InlineData("<xsl:output method='xml' omit-xml-declaration='yes'/><xsl:output name='n' method='text'/>", "<out><e/></out>")]
    public async Task A_named_output_does_not_serialize_the_principal_result(string outputs, string expected)
        => (await Run(outputs)).Should().Be(expected);
}
