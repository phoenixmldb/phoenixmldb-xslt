using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:assert/@error-code is an attribute value template (XSLT 3.0 §23.2), normalized to an error code as xsl:message's
/// is. It was used as the raw attribute, so an escaped EQName reported doubled braces and a prefixed code was never
/// resolved (xslt#112, W3C error-1665a, assert-006). The no-code test is a guard.
/// </summary>
public sealed class AssertErrorCodeTests
{
    private static async Task<string> ErrorOfAsync(string assert, string namespaces = "")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" {{namespaces}} exclude-result-prefixes="#all">
              <xsl:template match="/">{{assert}}<out/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var act = () => t.TransformAsync("<doc/>");
        return (await act.Should().ThrowAsync<Exception>()).Which.Message;
    }

    [Fact]
    public async Task An_escaped_standard_EQName_reduces_to_its_local_name()
        => (await ErrorOfAsync("""<xsl:assert test="false()" error-code="Q{{http://www.w3.org/2005/xqt-errors}}XTDE1665"/>"""))
            .Should().StartWith("XTDE1665").And.NotContain("{{");

    [Fact]
    public async Task A_prefixed_code_resolves_through_the_in_scope_namespaces()
        => (await ErrorOfAsync("""<xsl:assert test="false()" error-code="my:ABCD9999"/>""", "xmlns:my=\"http://example.com/my\""))
            .Should().Contain("Q{http://example.com/my}ABCD9999");

    [Fact]
    public async Task The_code_is_evaluated_as_an_attribute_value_template()
        // An unprefixed code is in no namespace, so it is reported as Q{}XTDE1665 (as W3C si-assert-901 expects Q{}XX99).
        => (await ErrorOfAsync("""<xsl:assert test="false()" error-code="{concat('XTDE', '1665')}"/>"""))
            .Should().StartWith("Q{}XTDE1665");

    [Fact]
    public async Task An_assert_without_a_code_still_raises_XTMM9001()
        => (await ErrorOfAsync("""<xsl:assert test="false()"/>""")).Should().Contain("XTMM9001");
}
