using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Martin Honnen 2026-07-30 (XSpec). A prefixed name in an element()/attribute() kind test
/// (e.g. <c>self::attribute(x:expand-text)</c>) raised a spurious
/// <c>XPST0081: Namespace prefix 'x' in attribute() test is not declared</c> at parse time,
/// because the XQuery parser validated the prefix against the XQuery prolog — which never
/// sees the stylesheet's <c>xmlns:x</c> binding. Saxon accepts it. The prefix must be
/// resolved in the XSLT namespace post-pass instead. XSpec's
/// gather-specs.xsl uses this pattern (<c>self::attribute(x:expand-text)</c>).
/// </summary>
public sealed class MartinKindTestPrefixTests
{
    private static XsltTransformer InitialTemplateTransformer()
    {
        var t = new XsltTransformer();
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return t;
    }

    [Fact]
    public async Task AttributeKindTest_WithStylesheetPrefix_MatchesByNamespace()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:x="http://example.com/x"
              exclude-result-prefixes="#all">
              <xsl:variable name="test-element" as="element()"><foo x:expand-text="yes">test</foo></xsl:variable>
              <xsl:template name="xsl:initial-template">
                <test>
                  <xsl:sequence select="$test-element/@*[self::attribute(x:expand-text)]"/>
                </test>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        // The x:expand-text attribute (namespace http://example.com/x) must be selected by
        // the prefixed attribute() kind test and copied onto <test>.
        r.Should().Contain("expand-text=\"yes\"");
        r.Should().Contain("http://example.com/x");
    }

    [Fact]
    public async Task AttributeKindTest_PrefixInDifferentNamespace_DoesNotMatch()
    {
        // A same-local-name attribute in a DIFFERENT namespace must NOT be selected —
        // proves the kind test compares by namespace, not just local name.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0"
              xmlns:x="http://example.com/x"
              xmlns:y="http://example.com/y"
              exclude-result-prefixes="#all">
              <xsl:variable name="test-element" as="element()"><foo y:expand-text="yes">test</foo></xsl:variable>
              <xsl:template name="xsl:initial-template">
                <test count="{count($test-element/@*[self::attribute(x:expand-text)])}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Should().Contain("count=\"0\"");
    }
}
