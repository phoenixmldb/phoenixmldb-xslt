using System;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Resource-safety regressions from the adversarial audit (parsers target): the stylesheet
/// compiler must not crash the host on adversarially deep input.
/// </summary>
public class AuditResourceSafetyTests
{
    private static string NestedIfStylesheet(int depth)
    {
        var sb = new StringBuilder();
        sb.Append("<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">");
        sb.Append("<xsl:template match=\"/\">");
        for (int i = 0; i < depth; i++) sb.Append("<xsl:if test=\"true()\">");
        sb.Append("<x/>");
        for (int i = 0; i < depth; i++) sb.Append("</xsl:if>");
        sb.Append("</xsl:template></xsl:stylesheet>");
        return sb.ToString();
    }

    [Fact]
    public async Task DeeplyNestedStylesheet_IsBoundedNotStackOverflow()
    {
        // The parser builds an instruction tree one level per nesting level, and every later pass
        // that walks it (streamability classification, the executor at transform time) recurses to
        // the same depth. A pathologically deep stylesheet must raise a catchable exception rather
        // than overflow the native stack on ANY of those passes (an uncatchable StackOverflow would
        // crash the test host = failure).
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(NestedIfStylesheet(50_000));

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DeepButLegalStylesheet_CompilesAndTransforms()
    {
        // The nesting cap must sit comfortably above real stylesheets AND be shallow enough that a
        // tree at the cap survives every recursive pass. Compile+transform a stylesheet nested just
        // under the cap end-to-end: it must produce output, not crash — proving the cap value is
        // safe, not merely that over-deep input is rejected.
        // 29 nested xsl:if → instruction-tree depth == the cap (30). Compiling and TRANSFORMING at
        // the exact maximum allowed depth must not crash — direct evidence the cap is below the
        // executor's overflow threshold, not merely that deeper input is rejected.
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(NestedIfStylesheet(29));
        var result = await t.TransformAsync("<in/>");

        result.Should().Contain("<x");
    }
}
