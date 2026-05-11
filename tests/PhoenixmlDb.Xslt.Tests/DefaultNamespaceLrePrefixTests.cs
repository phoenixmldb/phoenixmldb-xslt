using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression tests for literal-result-element prefix detection when the source
/// element has NO prefix (uses the default namespace).
///
/// Bug: in Docbook TNG <c>variable.xsl</c>, `&lt;xsl:variable name="v:theme-list"
/// as="element()*"&gt;&lt;theme/&gt;&lt;theme/&gt;&lt;/xsl:variable&gt;` produced an AST
/// where the FIRST `&lt;theme&gt;` LRE got prefix=`xsl` (stolen from the parent
/// `&lt;xsl:variable&gt;`'s entry in the line-keyed prefix map) while the second got
/// prefix=`h` (correct). When the LRE was later serialized as `&lt;xsl:theme&gt;`,
/// downstream queries against `$v:theme-list` failed because the engine treated it
/// as a result-tree-fragment with an undeclared `xsl:` prefix.
///
/// Pinpointed by Martin Honnen via VS debugger screenshot showing
/// `Instructions[0].Name = {xsl:theme}` and `Instructions[1].Name = {h:theme}`
/// in the parsed `v:theme-list` global declaration.
///
/// Fix: <c>ParseLiteralResultElement</c> only consults the prefix map when the
/// source element actually had a prefix in LINQ-to-XML's view. Default-namespace
/// elements skip the map and use the LINQ ancestor walk directly.
/// </summary>
public class DefaultNamespaceLrePrefixTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task Sibling_default_ns_LREs_inside_xsl_variable_get_consistent_prefix()
    {
        // Mirror Docbook TNG variable.xsl lines 246-249 exactly: an xsl:variable
        // with `as="element()*"` body containing two sibling default-namespace LREs.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:h="http://www.w3.org/1999/xhtml"
                            xmlns:v="http://example.com/v"
                            xmlns="http://www.w3.org/1999/xhtml"
                            exclude-result-prefixes="#all"
                            version="3.0">
              <xsl:variable name="v:items" as="element()*">
                <theme name="dark" id="materials-dark" dark="true"/>
                <theme name="light" id="materials-light" dark="false"/>
              </xsl:variable>
              <xsl:template match="/">
                <out>
                  <c><xsl:value-of select="count($v:items)"/></c>
                  <both-element><xsl:value-of select="every $i in $v:items satisfies $i instance of element()"/></both-element>
                  <both-xhtml><xsl:value-of select="every $i in $v:items satisfies namespace-uri($i) = 'http://www.w3.org/1999/xhtml'"/></both-xhtml>
                  <dark-found><xsl:value-of select="exists($v:items[@dark='true'])"/></dark-found>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        // Output uses `h:` prefix because xhtml is the default namespace in the
        // stylesheet AND the default-ns prefix-binding in the result tree is `h`.
        result.Should().Contain("<h:c>2</h:c>");
        result.Should().Contain("<h:both-element>true</h:both-element>",
            "both <theme/> siblings must remain element nodes (not coerced into result-tree-fragments)");
        result.Should().Contain("<h:both-xhtml>true</h:both-xhtml>",
            "both <theme/> siblings must be in the xhtml namespace (no prefix in source = inherit default ns)");
        result.Should().Contain("<h:dark-found>true</h:dark-found>",
            "predicate `@dark='true'` must work — requires axis-step on a real element node, not RTF/atomic");
        result.Should().NotContain("xsl:theme",
            "no <theme/> sibling should leak into the xsl namespace (Martin Honnen's specific bug)");
    }

    [Fact]
    public async Task First_sibling_default_ns_LRE_does_not_inherit_xsl_prefix_from_parent()
    {
        // Direct probe: the FIRST default-ns LRE under an xsl:* parent must NOT pick
        // up the parent's `xsl:` prefix. This was the specific bug Martin reported
        // (parent `<xsl:variable>` at line 246 → first child `<theme>` at line 247
        // got prefix=`xsl` due to a stale prefix-map entry).
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns="http://example.com/default"
                            version="3.0">
              <xsl:variable name="items" as="element()*">
                <first/>
                <second/>
              </xsl:variable>
              <xsl:template match="/">
                <results>
                  <xsl:for-each select="$items">
                    <item ns="{namespace-uri(.)}" local="{local-name(.)}"/>
                  </xsl:for-each>
                </results>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        // Both items must be in the default namespace, NOT in the xsl namespace.
        result.Should().Contain("ns=\"http://example.com/default\" local=\"first\"");
        result.Should().Contain("ns=\"http://example.com/default\" local=\"second\"");
        result.Should().NotContain("http://www.w3.org/1999/XSL/Transform",
            "no LRE should have leaked into the xsl namespace");
    }
}
