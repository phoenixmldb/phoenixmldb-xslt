using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The engine has two representations of a text node. One is <c>XdmText</c>, a node in a store.
/// The other is <c>TextNodeItem</c>, a bare record holding just the string, used where a sequence
/// must remember that an item is a text node rather than an atomic string (see
/// <see cref="TextNodeItemMatchingTests"/>). An <c>xsl:function</c> whose body is
/// <c>xsl:value-of</c> returns the second kind.
///
/// <c>fn:deep-equal</c> knew only the first. Its node arm saw "one is a node, the other is not"
/// and answered false for two text nodes with the same content; its atomizing arm answered TRUE
/// for a text node compared against a plain string. Both directions are wrong and both are
/// silent — the only symptom is a comparison that disagrees with what the two values print as.
///
/// Reported by Martin Honnen: a round-tripped standalone text node in the xdm-persistence node
/// map compared unequal to an identical text node, failing tests/test-map-nodes-roundtrip.xsl.
/// </summary>
public class DeepEqualTextNodeItemTests
{
    private static async Task<string> Run(string body)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
                            exclude-result-prefixes="#all">
              <xsl:function name="f:text" as="item()*">
                <xsl:param name="s" as="xs:string"/>
                <xsl:value-of select="$s"/>
              </xsl:function>
              <xsl:variable name="helper" as="element()"><item>abc</item></xsl:variable>
              <xsl:variable name="stored" as="text()" select="$helper/text()"/>
              <xsl:template match="/" name="xsl:initial-template">
                <out>{body}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync("<in/>").ConfigureAwait(true);
    }

    [Fact]
    public async Task FunctionResultTextNode_EqualsStoredTextNode_WithSameContent()
    {
        (await Run("""<xsl:value-of select="deep-equal($stored, f:text('abc'))"/>""")
            .ConfigureAwait(true)).Should().Be("<out>true</out>");
    }

    [Fact]
    public async Task FunctionResultTextNode_EqualsAnotherFunctionResultTextNode()
    {
        (await Run("""<xsl:value-of select="deep-equal(f:text('abc'), f:text('abc'))"/>""")
            .ConfigureAwait(true)).Should().Be("<out>true</out>");
    }

    [Fact]
    public async Task FunctionResultTextNode_DiffersWhenContentDiffers()
    {
        (await Run("""<xsl:value-of select="deep-equal(f:text('abc'), f:text('xyz'))"/>""")
            .ConfigureAwait(true)).Should().Be("<out>false</out>");
    }

    [Fact]
    public async Task FunctionResultTextNode_IsNotDeepEqualToAnAtomicString()
    {
        // The other half of the defect: the atomizing arm made a text node compare equal to a
        // plain string. F&O 14.2.2 — a node and an atomic value are never deep-equal.
        (await Run("""<xsl:value-of select="deep-equal(f:text('abc'), 'abc')"/>""")
            .ConfigureAwait(true)).Should().Be("<out>false</out>");
    }

    [Fact]
    public async Task StoredTextNode_IsNotDeepEqualToAnAtomicString()
    {
        // Same rule for the representation that always was a node, so the two agree.
        (await Run("""<xsl:value-of select="deep-equal($stored, 'abc')"/>""")
            .ConfigureAwait(true)).Should().Be("<out>false</out>");
    }

    [Fact]
    public async Task FunctionResultTextNode_IsNotDeepEqualToAnElementWithSameStringValue()
    {
        (await Run("""<xsl:value-of select="deep-equal(f:text('abc'), $helper)"/>""")
            .ConfigureAwait(true)).Should().Be("<out>false</out>");
    }

    [Fact]
    public async Task SequencesOfFunctionResultTextNodes_CompareElementwise()
    {
        (await Run(
            """<xsl:value-of select="deep-equal((f:text('a'), f:text('b')), (f:text('a'), f:text('b')))"/>""")
            .ConfigureAwait(true)).Should().Be("<out>true</out>");
    }
}
