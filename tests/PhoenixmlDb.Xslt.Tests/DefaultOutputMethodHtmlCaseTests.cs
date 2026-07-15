using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The Serialization/XSLT default output-method resolution (applied only when
/// <c>xsl:output</c> declares no explicit <c>method</c>) selects the html/xhtml method from the
/// result's document element. That resolution matches the document element's local name
/// <c>html</c> CASE-SENSITIVELY (lowercase only): a lowercase <c>&lt;html&gt;</c> root defaults to
/// the html method (Content-Type meta injected into <c>&lt;head&gt;</c>, W3C decl/output
/// output-0715), while an UPPERCASE <c>&lt;HTML&gt;</c> root keeps the xml default and gets NO
/// injected meta (W3C insn/sequence sequence-0601 — a copy transform of an uppercase-HTML source).
///
/// Regression guard: commit fffd4be added the default-method resolution but matched <c>html</c>
/// case-insensitively, so an uppercase <c>&lt;HTML&gt;</c> copy was wrongly promoted to the html
/// method and had a Content-Type meta injected, corrupting the assert-xml result tree of
/// sequence-0601 (insn/sequence 86 -> 85). The distinct HTML-method element handling (void
/// elements, script/style raw text, HTML5 DOCTYPE name) stays case-insensitive and is unaffected,
/// because it only runs once a method has already been selected.
/// </summary>
public class DefaultOutputMethodHtmlCaseTests
{
    private static async Task<string> Transform(string stylesheet, string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri(Path.GetTempPath() + "/"));
        return await t.TransformAsync(source);
    }

    // A copy transform: no xsl:output method, indent="no". The result document element is the
    // COPIED source root. Mirrors insn/sequence sequence-0601's shape (identity copy of an HTML
    // document that also transposes a table via copy-of of a node sequence).
    private const string CopySheet = """
        <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
           <xsl:output indent="no"/>
           <xsl:template match="@*|node()">
              <xsl:copy><xsl:apply-templates select="@*|node()"/></xsl:copy>
           </xsl:template>
        </xsl:transform>
        """;

    [Fact]
    public async Task UppercaseHtmlRoot_NoMethod_KeepsXmlDefault_NoContentTypeMeta()
    {
        // sequence-0601 regression: an UPPERCASE <HTML> document element must NOT trigger the
        // html default-method, so no Content-Type <meta> is injected into <HEAD>.
        const string source = "<HTML><HEAD><TITLE>t</TITLE></HEAD><BODY><P>x</P></BODY></HTML>";
        var result = await Transform(CopySheet, source);
        result.Should().NotContain("http-equiv");
        result.Should().NotContain("Content-Type");
        // The copied HEAD content is preserved verbatim, adjacent, with no injected node.
        result.Should().Contain("<HEAD><TITLE>t</TITLE></HEAD>");
    }

    [Fact]
    public async Task LowercaseHtmlRoot_NoMethod_DefaultsToHtml_InjectsContentTypeMeta()
    {
        // Guard that the case-sensitive fix does NOT over-correct: a lowercase <html> root still
        // resolves to the html method and injects the Content-Type meta (output-0715 behaviour).
        const string source = "<html><head><title>t</title></head><body><p>x</p></body></html>";
        var result = await Transform(CopySheet, source);
        result.Should().Contain("http-equiv=\"Content-Type\"");
        result.Should().Contain("content=\"text/html; charset=UTF-8\"");
    }

    [Fact]
    public async Task CopyOfNodeSequence_IntoConstructedElement_NoItemSeparator_AdjacentNoSeparator()
    {
        // §5.7.2 scope guard: an item-separator is NOT declared, and a copy-of of a SEQUENCE of
        // element nodes assembled inside a constructed element must place the nodes adjacent with
        // no separator (and no stray whitespace). Locks the sequence-0601 table-transpose shape.
        const string sheet = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <xsl:output method="xml" indent="no"/>
               <xsl:template match="/">
                  <TABLE><xsl:copy-of select="for $i in 1 to 3 return /root/TR[$i]"/></TABLE>
               </xsl:template>
            </xsl:transform>
            """;
        const string source = "<root><TR><TD>1</TD></TR><TR><TD>2</TD></TR><TR><TD>3</TD></TR></root>";
        var result = await Transform(sheet, source);
        result.Should().Contain("<TABLE><TR><TD>1</TD></TR><TR><TD>2</TD></TR><TR><TD>3</TD></TR></TABLE>");
    }
}
