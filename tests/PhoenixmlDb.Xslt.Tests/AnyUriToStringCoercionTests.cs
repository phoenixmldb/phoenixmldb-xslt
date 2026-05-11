using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression tests for xs:anyURI → xs:string function-conversion-rule coercion in
/// <c>xsl:variable as="xs:string"</c>. Per F&amp;O 4.0 §1.6.3 step 4, an xs:anyURI
/// value supplied where xs:string is expected is cast to xs:string.
///
/// Originated from Martin Honnen's Docbook TNG XPTY0020 triage: <c>templates.xsl</c>
/// declared <c>&lt;xsl:variable name="uri" as="xs:string" select="resolve-uri(...)"/&gt;</c>
/// and our engine raised XTTE0570 because <c>resolve-uri</c> returns xs:anyURI and we
/// didn't apply the function-conversion-rule cast.
/// </summary>
public class AnyUriToStringCoercionTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task xsl_variable_as_xs_string_accepts_resolve_uri_result()
    {
        // resolve-uri returns xs:anyURI; the as="xs:string" check must accept it.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0">
              <xsl:variable name="u" as="xs:string"
                            select="resolve-uri('templates.xml', 'file:///tmp/')"/>
              <xsl:template match="/"><out><xsl:value-of select="$u"/></out></xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        result.Should().Contain("<out");
        result.Should().Contain("templates.xml");
    }

    [Fact]
    public async Task xsl_variable_as_xs_string_accepts_xs_anyURI_constructor()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0">
              <xsl:variable name="u" as="xs:string" select="xs:anyURI('http://example.com/foo')"/>
              <xsl:template match="/"><out><xsl:value-of select="$u"/></out></xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        result.Should().Contain("http://example.com/foo");
    }

    [Fact]
    public async Task local_xsl_variable_as_xs_string_accepts_resolve_uri_result()
    {
        // The originating Docbook TNG case: a LOCAL xsl:variable (inside another
        // xsl:variable body) with as="xs:string" select="resolve-uri(...)". The
        // top-level case worked before the fix; the local-scope path raised XTTE0570.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0">
              <xsl:variable name="outer" as="xs:string">
                <xsl:variable name="u" as="xs:string"
                              select="resolve-uri('inner.xml', 'file:///tmp/')"/>
                <xsl:value-of select="$u"/>
              </xsl:variable>
              <xsl:template match="/"><out><xsl:value-of select="$outer"/></out></xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        result.Should().Contain("inner.xml");
    }
}
