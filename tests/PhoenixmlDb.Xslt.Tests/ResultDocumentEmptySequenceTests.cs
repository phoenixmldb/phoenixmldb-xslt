using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSpec census 08 (<c>XTTE0505</c>, 27 suites — one message across all of them).
/// <c>xsl:result-document</c> returns the EMPTY SEQUENCE (XSLT 3.0 §26.1); its
/// content becomes a result document and must never be attributed to the
/// containing sequence constructor.
///
/// An <c>xsl:result-document</c> with an <c>href</c> is redirected into its own
/// buffer, so it was already correct. One targeting the PRINCIPAL output (no
/// <c>href</c>) writes straight into <c>_output</c> — the same buffer an
/// enclosing <c>as=</c> body slices to compute its return value — so its content
/// was counted as the template's result.
///
/// XSpec's generated <c>x:main</c> is exactly this shape:
/// <c>&lt;xsl:template name="x:main" as="empty-sequence()"&gt;</c> wrapping an
/// <c>&lt;xsl:result-document format="…"&gt;</c> with no href, which produced
/// <c>XTTE0505: … expected zero items, got 1</c>.
/// </summary>
public sealed class ResultDocumentEmptySequenceTests
{
    private static async Task<string> RunAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<doc/>");
    }

    [Fact]
    public async Task PrincipalResultDocument_InEmptySequenceTemplate_DoesNotCountAsReturnValue()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output name="rep" method="xml" indent="no"/>
              <xsl:template match="/" as="empty-sequence()">
                <xsl:result-document format="rep">
                  <report>generated</report>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().Contain("<report>generated</report>",
            "the content is the principal result document and must still be serialized");
    }

    [Fact]
    public async Task PrincipalResultDocument_InNamedEmptySequenceTemplate_IsEmptySequence()
    {
        // The XSpec x:main shape: a NAMED template as="empty-sequence()" whose
        // whole body is xsl:message + a bare xsl:result-document.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output name="rep" method="xml" indent="no"/>
              <xsl:template match="/"><xsl:call-template name="main"/></xsl:template>
              <xsl:template name="main" as="empty-sequence()">
                <xsl:context-item use="absent"/>
                <xsl:message>info</xsl:message>
                <xsl:result-document format="rep">
                  <report>x</report>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().Contain("<report>x</report>");
    }

    [Fact]
    public async Task PrincipalResultDocument_IsNotReturnedAsATypedItem()
    {
        // The converse proof: declaring as="element(report)" must FAIL, because
        // the instruction returns the empty sequence, not the element. Before the
        // fix this succeeded, which is what made the empty-sequence() case fail.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output name="rep" method="xml" indent="no"/>
              <xsl:template match="/" as="element(report)">
                <xsl:result-document format="rep">
                  <report>x</report>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var act = async () => await RunAsync(ss);
        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("XTTE0505"),
                "xsl:result-document contributes no items, so element(report) cannot be satisfied");
    }

    [Fact]
    public async Task SecondaryResultDocument_InEmptySequenceTemplate_StillWorks()
    {
        // Regression guard: the href path was already correct and must stay so.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/" as="empty-sequence()">
                <xsl:result-document href="sec-guard.xml">
                  <r>secondary</r>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        // A stylesheet base URI is needed to resolve the relative href.
        await t.LoadStylesheetAsync(ss, new Uri("file:///sheet.xsl"));
        var r = await t.TransformAsync("<doc/>");
        r.Should().NotContain("secondary", "it belongs to the secondary document, not the principal output");
        t.SecondaryResultDocuments.Should().ContainKey("sec-guard.xml");
        t.SecondaryResultDocuments["sec-guard.xml"].Should().Contain("<r>secondary</r>");
    }
}
