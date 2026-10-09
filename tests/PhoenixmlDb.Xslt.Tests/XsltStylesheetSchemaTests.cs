using FluentAssertions;
using PhoenixmlDb.Core.Schema;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The schema for XSLT 3.0 stylesheets, shipped as an XSD 1.0 copy of the W3C's XSD 1.1 schema.
/// </summary>
public sealed class XsltStylesheetSchemaTests
{
    private static readonly CompiledSchema Schema = XsltStylesheetSchema.Compile();

    private static string Sheet(string body, string attributes = "") =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform' "
        + "xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:p='urn:p' " + attributes + ">" + body + "</xsl:stylesheet>";

    private static SchemaValidationResult Check(string stylesheet) => SchemaValidator.Validate(Schema, stylesheet);

    [Fact]
    public void It_compiles_as_xsd_1_0_and_reads_nothing_else()
    {
        Schema.Documents.Select(d => d.Uri).Should().Equal(XsltStylesheetSchema.DocumentUri);
        using var document = XsltStylesheetSchema.Open();
        using var reader = new StreamReader(document);
        var text = reader.ReadToEnd();
        text.Should().Contain("MODIFIED COPY").And.NotContain("<xs:assert").And.NotContain("xmlns:vc=");
    }

    [Fact]
    public void An_ordinary_stylesheet_is_valid()
    {
        Check(Sheet("""
            <xsl:output method='xml' indent='yes'/>
            <xsl:param name='p' as='xs:string' select='"x"'/>
            <xsl:template match='/' mode='#default'>
              <out a='{$p}'>
                <xsl:for-each select='//item'>
                  <xsl:sort select='@n' data-type='number'/>
                  <xsl:copy-of select='.'/>
                </xsl:for-each>
                <xsl:choose><xsl:when test='1'>a</xsl:when><xsl:otherwise>b</xsl:otherwise></xsl:choose>
              </out>
            </xsl:template>
            """)).Diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// xsl:variable is a declaration and an instruction. The original puts it in two
    /// substitution groups, which XSD 1.0 does not have.
    /// </summary>
    [Fact]
    public void A_variable_is_valid_as_a_declaration_and_as_an_instruction()
    {
        Check(Sheet("<xsl:variable name='g' select='1'/><xsl:template name='t'><xsl:variable name='l' select='2'/></xsl:template>"))
            .Diagnostics.Should().BeEmpty();
        Check("<xsl:package version='3.0' name='urn:pkg' package-version='1.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>"
            + "<xsl:variable name='g' select='1' visibility='public'/></xsl:package>").Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("<xsl:output method='json'/>")]
    [InlineData("<xsl:output method='adaptive'/>")]
    [InlineData("<xsl:output method='p:mine'/>")]
    [InlineData("<xsl:template match='x' mode='a #default'/>")]
    [InlineData("<xsl:template match='x' mode='#all'/>")]
    [InlineData("<xsl:use-package name='urn:lib'><xsl:accept component='function' names='p:f#1 p:*' visibility='private'/></xsl:use-package>")]
    [InlineData("<xsl:use-package name='urn:lib'><xsl:accept component='*' names='*' visibility='hidden'/></xsl:use-package>")]
    [InlineData("<xsl:mode streamable='yes' on-no-match='shallow-copy'/>")]
    [InlineData("<xsl:import-schema namespace='urn:s'><xs:schema targetNamespace='urn:s'><xs:element name='e' type='xs:int'/></xs:schema></xsl:import-schema>")]
    [InlineData("<p:data xmlns:p='urn:p'><anything/></p:data>")]
    public void Valid_xslt_3_0_is_accepted(string declaration)
    {
        Check(Sheet(declaration)).Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("<xsl:template match='/' banana='yes'/>", "banana")]
    [InlineData("<xsl:output method='nonsense'/>", "method")]
    [InlineData("<xsl:output indent='perhaps'/>", "indent")]
    [InlineData("<xsl:template match='/'><xsl:choose><xsl:otherwise/></xsl:choose></xsl:template>", "otherwise")]
    [InlineData("<xsl:apply-templates/>", "apply-templates")]
    [InlineData("<xsl:tempalte match='/'/>", "tempalte")]
    public void A_structural_mistake_is_reported(string declaration, string named)
    {
        var result = Check(Sheet(declaration));
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Message.Contains(named, StringComparison.Ordinal));
    }

    /// <summary>
    /// What the copy does not check: a rule the original states as an assertion. This pins the
    /// limit, so that it is a decision when it changes.
    /// </summary>
    [Fact]
    public void A_rule_the_original_states_as_an_assertion_is_not_checked()
    {
        Check(Sheet("<xsl:template match='/'><xsl:value-of select='1'>and content</xsl:value-of></xsl:template>"))
            .IsValid.Should().BeTrue();
    }
}
