using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template or variable with an <c>as=</c> type captures its body by SERIALIZING it and
/// reparsing the text into nodes. The reparse wraps the chunk in a synthetic element carrying
/// the in-scope namespace declarations — but it was built from the live output scopes only, and
/// the reparse happens after the body has finished, by which point the scopes in force while
/// the body ran are already popped.
///
/// A prefix the chunk uses could therefore be missing from the wrapper. The reparse then threw
/// "'x' is an undeclared prefix", <c>AddBodyOutputChunk</c> fell back to appending the chunk as
/// a raw STRING, and the typed template failed its own return check with
/// <c>XTTE0505: … item of type String does not match declared type Element</c> — an error that
/// names a type mismatch and never mentions namespaces.
///
/// XSpec's <c>x:like</c> is exactly this: it copies <c>x:</c>-prefixed elements out of a
/// constructed tree into a template declared <c>as="element()+"</c>. It was XTTE0505 in 34 of
/// its 162 suites; fixing it moved 21 suites past Compile with none regressing.
/// </summary>
public sealed class TypedBodyReparseNamespaceTests
{
    private static async Task<string> RunAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<doc><a/></doc>");
    }

    [Fact]
    public async Task Typed_template_returns_nodes_when_the_body_copies_prefixed_elements()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                            xmlns:x="urn:x" xmlns:tv1="urn:tv1">
              <xsl:output method="xml" indent="no"/>
              <xsl:mode name="m" on-multiple-match="fail" on-no-match="shallow-copy"/>
              <xsl:template match="a" as="element()+" mode="m">
                <xsl:variable name="tmp" as="document-node()">
                  <xsl:document>
                    <wrap>
                      <x:expect label="one" name="tv1:first"/>
                      <x:expect label="two" name="tv1:second"/>
                    </wrap>
                  </xsl:document>
                </xsl:variable>
                <xsl:apply-templates select="$tmp/wrap/element()" mode="#current"/>
              </xsl:template>
              <xsl:template match="/"><out><xsl:apply-templates select="/doc/a" mode="m"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        // The failure mode was XTTE0505 before reaching here at all.
        r.Should().Contain("x:expect");
        r.Should().Contain("tv1:first");
    }

    [Fact]
    public async Task Typed_variable_body_with_a_prefixed_element_round_trips()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                            xmlns:p="urn:p">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v" as="element(p:item)">
                  <p:item>hello</p:item>
                </xsl:variable>
                <xsl:value-of select="$v instance of element(p:item)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Trim().Should().Be("true");
    }
}
