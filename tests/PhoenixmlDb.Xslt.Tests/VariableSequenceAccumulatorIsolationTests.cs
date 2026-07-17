using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression tests for `xsl:variable` body executing inside an `xsl:function` body.
///
/// Bug: when an atomic-type-bodied xsl:variable (e.g. `as="xs:boolean"`) was bound
/// inside a function body, its body's `xsl:sequence` results leaked into the
/// function's accumulator instead of contributing to the variable's value. The
/// variable's body produced empty text, so the variable received the empty string
/// and failed XTTE0570.
///
/// Found while triaging Martin Honnen's Docbook TNG XPTY0020 chain — the
/// `$process` variable inside `fp:run-transforms` (which uses xsl:iterate inside
/// an xsl:function) hit this. Fix in `BindVariableAsync` else branch:
/// save/replace/restore `_sequenceAccumulator` around the body, and use
/// captured items as the variable value when text output is empty.
/// </summary>
public class VariableSequenceAccumulatorIsolationTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task xs_boolean_bodied_variable_inside_xsl_function_evaluates_correctly()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:f="http://example.com/f" version="3.0">
              <xsl:function name="f:run" as="xs:boolean*">
                <xsl:iterate select="(1, 2)">
                  <xsl:variable name="process" as="xs:boolean">
                    <xsl:choose>
                      <xsl:when test="true()"><xsl:sequence select="true()"/></xsl:when>
                      <xsl:otherwise><xsl:sequence select="false()"/></xsl:otherwise>
                    </xsl:choose>
                  </xsl:variable>
                  <xsl:sequence select="$process"/>
                </xsl:iterate>
              </xsl:function>
              <xsl:template match="/">
                <out><xsl:value-of select="f:run()" separator=","/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        result.Should().Contain("true,true");
    }

    [Fact]
    public async Task xsl_next_iteration_with_param_body_preserves_typed_items()
    {
        // Companion to #20: same accumulator-isolation issue but in xsl:next-iteration's
        // with-param body. Found in Docbook TNG fp:run-transforms where each iteration
        // re-binds $document via `<xsl:with-param name="document">
        //   <xsl:choose>...<xsl:sequence select="$next-result?output"/>...</xsl:choose>
        // </xsl:with-param>`. Without the fix, the doc-node gets serialized to text and
        // rebound as XsUntypedAtomic — downstream `<xsl:evaluate context-item="$document">`
        // then fails with XPTY0020 ("axis step on item of type XsUntypedAtomic").
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:f="http://example.com/f" version="3.0">
              <xsl:function name="f:roundtrip" as="xs:boolean*">
                <xsl:param name="doc" as="document-node()"/>
                <xsl:iterate select="(1, 2, 3)">
                  <xsl:param name="d" as="document-node()" select="$doc"/>
                  <xsl:on-completion select="()"/>
                  <xsl:variable name="ok" as="xs:boolean">
                    <xsl:evaluate xpath="'exists(/*)'" as="xs:boolean" context-item="$d"/>
                  </xsl:variable>
                  <xsl:sequence select="$ok"/>
                  <xsl:next-iteration>
                    <!-- Body content (no select) — the case that previously broke. -->
                    <xsl:with-param name="d">
                      <xsl:sequence select="$d"/>
                    </xsl:with-param>
                  </xsl:next-iteration>
                </xsl:iterate>
              </xsl:function>
              <xsl:template match="/">
                <out><xsl:value-of select="f:roundtrip(/)" separator=","/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");
        result.Should().Contain("true,true,true",
            "all three iterations must see $d as a document-node — body-content with-param must preserve typed items, not atomize them");
    }

    [Fact]
    public async Task xs_integer_bodied_variable_inside_xsl_function_evaluates_correctly()
    {
        // Same shape but with xs:integer to confirm fix isn't boolean-specific.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:f="http://example.com/f" version="3.0">
              <xsl:function name="f:double" as="xs:integer">
                <xsl:param name="x" as="xs:integer"/>
                <xsl:variable name="result" as="xs:integer">
                  <xsl:sequence select="$x * 2"/>
                </xsl:variable>
                <xsl:sequence select="$result"/>
              </xsl:function>
              <xsl:template match="/"><out><xsl:value-of select="f:double(21)"/></out></xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<i/>");
        result.Should().Contain(">42<");
    }

    [Fact]
    public async Task xsl_sequence_with_interleaved_lre_and_select_preserves_document_order()
    {
        // insn/sequence-0137a: an xsl:sequence WITH content whose children interleave
        // literal-result-elements (<e>5</e>, <f>6</f>, <g>7</g>) with nested
        // xsl:sequence select= atomic values. Under as="xs:integer*" the element nodes
        // atomize to their integer values; the result MUST preserve construction order,
        // not segregate atomic-producing instructions ahead of node-producing ones.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:template match="doc">
                <xsl:variable name="q" as="xs:integer *">
                  <xsl:for-each select="1 to 3">
                     <xsl:sequence>
                        <e>5</e>
                        <xsl:sequence select=".*10"/>
                        <f>6</f>
                        <xsl:sequence select=".*10 + 1"/>
                        <g>7</g>
                     </xsl:sequence>
                  </xsl:for-each>
                </xsl:variable>
                <zzz><xsl:value-of select="$q" separator=" "/></zzz>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");
        result.Should().Contain(">5 10 6 11 7 5 20 6 21 7 5 30 6 31 7</zzz>",
            "xsl:sequence-with-content children must contribute to one ordered sequence in source order");
    }
}
