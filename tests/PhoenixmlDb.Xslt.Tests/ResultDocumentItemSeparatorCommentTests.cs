using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// §5.7.2 sequence normalization: when an explicit (non-absent) <c>item-separator</c>
/// is in effect on an <c>xsl:result-document</c>, a copy of the separator is inserted
/// between EVERY pair of adjacent items in the serialized sequence — including around
/// comment nodes, not just between adjacent atomic values.
///
/// Mirrors W3C conformance cases insn/result-document/result-document-1408 / -1409 / -1410.
/// </summary>
public sealed class ResultDocumentItemSeparatorCommentTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri("file:///sheet.xsl"));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    private const string Sheet1408 = """
        <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
           <xsl:param name="twiddle" select="'~'"/>
           <xsl:template name="xsl:initial-template">
              <xsl:result-document method="xml" indent="no" item-separator="{$twiddle}">
                 <xsl:comment>start</xsl:comment>
                 <xsl:sequence select="11 to 15"/>
                 <xsl:comment>middle</xsl:comment>
                 <xsl:sequence select="16 to 20"/>
                 <xsl:comment>end</xsl:comment>
              </xsl:result-document>
           </xsl:template>
        </xsl:transform>
        """;

    [Fact]
    public async Task Tilde_SeparatorAroundComments()
    {
        // result-document-1408
        var result = await Transform(Sheet1408);
        result.Should().MatchRegex(@"<!--start-->~11~12~13~14~15~<!--middle-->~16~17~18~19~20~<!--end-->$");
    }

    [Fact]
    public async Task Newline_SeparatorAroundComments()
    {
        // result-document-1410 (separator is a literal newline)
        const string ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <xsl:template name="xsl:initial-template">
                  <xsl:result-document method="xml" indent="no" item-separator="&#xa;">
                     <xsl:comment>start</xsl:comment>
                     <xsl:sequence select="11 to 15"/>
                     <xsl:comment>middle</xsl:comment>
                     <xsl:sequence select="16 to 20"/>
                     <xsl:comment>end</xsl:comment>
                  </xsl:result-document>
               </xsl:template>
            </xsl:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex("<!--start-->\n11\n12\n13\n14\n15\n<!--middle-->\n16\n17\n18\n19\n20\n<!--end-->$");
    }
}
