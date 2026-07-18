using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §5.7.2 (Constructing Simple Content) / §5.7.1 (complex content): when a
/// temporary tree (a variable with a body and no <c>as=</c>) is built from a sequence of
/// adjacent atomic values, consecutive atomic values are separated by a single space.
/// Regression coverage for the seqtor-036a/037a/039a/040a cluster, where a variable built
/// from two empty <c>xsl:sequence select="''"</c> items must have string value " " (one
/// space), and that space then survives into xsl:comment / xsl:processing-instruction /
/// xsl:attribute / xsl:value-of simple content (document nodes are NOT merged, so each
/// copy-of contributes a distinct space-separated item).
/// </summary>
public class SimpleContentSequenceSeparatorTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<in/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    private const string NonEmptyVar = """
                <xsl:variable name="non-empty">
                  <xsl:sequence select="''"/>
                  <xsl:sequence select="''"/>
                </xsl:variable>
        """;

    [Fact]
    public async Task TempTree_TwoAdjacentAtomics_SeparatedBySingleSpace()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="non-empty">
                  <xsl:sequence select="''"/>
                  <xsl:sequence select="''"/>
                </xsl:variable>
                <r><xsl:value-of select="concat('[', string($non-empty), ']')"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // Two empty strings joined with a single space → temp tree string value " ".
        (await Transform(ss)).Should().Be("<r>[ ]</r>");
    }

    [Fact]
    public async Task TempTree_TwoNonEmptyAtomics_SeparatedBySingleSpace()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="v">
                  <xsl:sequence select="'a'"/>
                  <xsl:sequence select="'b'"/>
                </xsl:variable>
                <r><xsl:value-of select="concat('[', string($v), ']')"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await Transform(ss)).Should().Be("<r>[a b]</r>");
    }

    [Fact]
    public async Task Comment_SequenceOfCopyOfDocNodes_SpaceSeparated()
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
            {{NonEmptyVar}}
                <xsl:comment>
                  <xsl:copy-of select="$non-empty"/>
                  <xsl:value-of select="'|'"/>
                  <xsl:copy-of select="$non-empty"/>
                  <xsl:value-of select="'|'"/>
                  <xsl:copy-of select="$non-empty"/>
                </xsl:comment>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // seqtor-036a: doc-node items " " space-joined with the two "|" text nodes → 2,3,2 gaps.
        (await Transform(ss)).Should().Be("<!--  |   |  -->");
    }

    [Fact]
    public async Task ProcessingInstruction_SequenceOfCopyOfDocNodes_SpaceSeparated_LeadingWsStripped()
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
            {{NonEmptyVar}}
                <xsl:processing-instruction name="foo">
                  <xsl:copy-of select="$non-empty"/>
                  <xsl:value-of select="'|'"/>
                  <xsl:copy-of select="$non-empty"/>
                  <xsl:value-of select="'|'"/>
                  <xsl:copy-of select="$non-empty"/>
                </xsl:processing-instruction>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // seqtor-037a: PI strips leading whitespace from the data → "|   |  ".
        (await Transform(ss)).Should().Be("<?foo |   |  ?>");
    }

    [Fact]
    public async Task Attribute_SequenceOfCopyOfDocNodes_SpaceSeparated()
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
            {{NonEmptyVar}}
                <bar>
                  <xsl:attribute name="foo">
                    <xsl:copy-of select="$non-empty"/>
                    <xsl:value-of select="'|'"/>
                    <xsl:copy-of select="$non-empty"/>
                    <xsl:value-of select="'|'"/>
                    <xsl:copy-of select="$non-empty"/>
                  </xsl:attribute>
                </bar>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // seqtor-039a.
        (await Transform(ss)).Should().Be("<bar foo=\" | | \"/>");
    }

    [Fact]
    public async Task ValueOf_SequenceOfCopyOfDocNodes_SpaceSeparated()
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
            {{NonEmptyVar}}
                <bar>
                  <xsl:value-of>
                    <xsl:copy-of select="$non-empty"/>
                    <xsl:value-of select="'|'"/>
                    <xsl:copy-of select="$non-empty"/>
                    <xsl:value-of select="'|'"/>
                    <xsl:copy-of select="$non-empty"/>
                  </xsl:value-of>
                </bar>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // seqtor-040a.
        (await Transform(ss)).Should().Be("<bar> | | </bar>");
    }
}
