using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:current-output-uri() (XSLT 3.0 §20.3.7). Reported by Martin Honnen 2026-09-06: the
/// function was a stub that returned the empty sequence unconditionally, so it was silently
/// empty even when the destination URI was known — no output and no error.
/// <para>
/// The empty sequence is only correct when the base output URI is ABSENT (writing to stdout or
/// to a string). When the host supplies one, current-output-uri() must report it, and inside an
/// <c>xsl:result-document</c> it must report that document's destination.
/// </para>
/// </summary>
public class CurrentOutputUriTests
{
    private static async System.Threading.Tasks.Task<string> RunAsync(
        string stylesheet, System.Uri? baseOutputUri)
    {
        var transformer = new XsltTransformer();
        if (baseOutputUri != null) transformer.SetBaseOutputUri(baseOutputUri);
        await transformer.LoadStylesheetAsync(stylesheet);
        return (await transformer.TransformAsync((string?)null)).Trim();
    }

    /// <summary>Absent base output URI is the spec's "absent" case: empty sequence, no error.</summary>
    [Fact]
    public async System.Threading.Tasks.Task AbsentBaseOutputUri_ReturnsEmptySequence()
    {
        var xslt = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template"><r>[{current-output-uri()}]</r></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(xslt, null)).Should().Contain("[]");
    }

    /// <summary>With a base output URI supplied, the principal result reports it.</summary>
    [Fact]
    public async System.Threading.Tasks.Task PrincipalOutput_ReportsBaseOutputUri()
    {
        var xslt = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template"><r>[{current-output-uri()}]</r></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(xslt, new System.Uri("file:///out/result.xml"));
        result.Should().Contain("[file:///out/result.xml]");
    }

    /// <summary>
    /// Inside xsl:result-document the destination is the @href resolved against the base output
    /// URI — and the previous value is restored afterwards, so a sibling instruction still sees
    /// the principal destination.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ResultDocumentHref_ReportsResolvedUri_AndRestores()
    {
        var xslt = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:result-document href="sub/child.xml"><c>[{current-output-uri()}]</c></xsl:result-document>
                <r>[{current-output-uri()}]</r>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var transformer = new XsltTransformer();
        transformer.SetBaseOutputUri(new System.Uri("file:///out/result.xml"));
        await transformer.LoadStylesheetAsync(xslt);
        var principal = (await transformer.TransformAsync((string?)null)).Trim();

        principal.Should().Contain("[file:///out/result.xml]", "the URI is restored after the result-document");
        var secondary = transformer.SecondaryResultDocuments.Should().ContainSingle().Subject;
        secondary.Value.Should().Contain("[file:///out/sub/child.xml]",
            "a relative href resolves against the base output URI");
    }
}
