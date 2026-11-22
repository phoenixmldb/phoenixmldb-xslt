using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The element or attribute name of a kind test in a path xsl:evaluate compiles at run time:
/// <c>self::element(a:x)</c>, <c>b:y/self::element(b:y)</c>. Only a step's name test had its namespace
/// resolved at run time, so a kind test's name never matched and the counts were 0 (3.2.0 and main);
/// <c>self::a:x</c> in the same path worked. The prefixes are bound on xsl:evaluate only.
/// </summary>
public class EvaluateKindTestNameTests
{
    [Fact]
    public async Task A_kind_test_name_in_an_evaluated_path_matches()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:evaluate xmlns:a="urn:a" xmlns:b="urn:b" context-item="(//*:x)[2]"
                    xpath="'count(self::element(a:x)), count(self::a:x), count(b:y/self::element(b:y)), count(b:y/attribute(b:n)), count(root()/self::document-node(element(r)))'"/>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await t.TransformAsync("""<r xmlns:a="urn:a" xmlns:b="urn:b"><a:x/><a:x><b:y b:n="1"/><b:y/></a:x></r>""");

        result.Trim().Should().Be("1 1 2 1 1");
    }
}
