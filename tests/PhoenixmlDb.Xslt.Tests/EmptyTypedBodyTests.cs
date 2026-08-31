using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A typed variable body that produces nothing must yield the EMPTY SEQUENCE, not a zero-length
/// text node.
///
/// The delivery path manufactured a zero-length text node whenever a node-ish typed body
/// finished with no items and no characters. That exists for a real reason — XSLT 3.0 11.4.3
/// says xsl:value-of "creates a new text node", so
/// <c>&lt;xsl:variable as="text()"&gt;&lt;xsl:value-of select="()"/&gt;&lt;/xsl:variable&gt;</c>
/// genuinely does hold one — but the condition could not tell that apart from a body that
/// produced nothing at all:
///
///   &lt;xsl:variable as="item()*"&gt;&lt;xsl:sequence select="()"/&gt;&lt;/xsl:variable&gt;
///   count($v)  ->  1, holding an empty text node
///
/// The fix counts instructions that ALWAYS produce a text node (today just xsl:value-of) across
/// the body and requires at least one before manufacturing. A delta is used rather than an
/// absolute, because a nested typed body would otherwise inherit an outer body's count.
///
/// Found through XSpec: an x:expect with no @select compiles to an expected value of (), and
/// x:result was coming back as one empty text node, so deep-equal((), text-node) was false and
/// every "Empty sequence" expectation failed. uqname-utils went 22 pass / 5 fail to 26 / 1.
/// </summary>
public class EmptyTypedBodyTests
{
    private static async Task<string> Run(string body)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:template name="main">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("item()*")]
    [InlineData("node()*")]
    [InlineData("text()*")]
    public async Task EmptySequenceBody_YieldsTheEmptySequence(string declared)
    {
        var result = await Run("""
            <xsl:variable name="v" as="DECLARED"><xsl:sequence select="()"/></xsl:variable>
            <out count="{count($v)}"/>
            """.Replace("DECLARED", declared, StringComparison.Ordinal));
        result.Should().Contain("count=\"0\"");
    }

    /// <summary>
    /// The half that must keep working: xsl:value-of always creates a text node, even a
    /// zero-length one, so this variable genuinely holds one item. A fix that simply deleted the
    /// manufacture would pass the tests above and break this.
    /// </summary>
    [Fact]
    public async Task EmptyValueOfBody_StillYieldsAZeroLengthTextNode()
    {
        var result = await Run("""
            <xsl:variable name="v" as="text()"><xsl:value-of select="()"/></xsl:variable>
            <out count="{count($v)}" isText="{$v[1] instance of text()}" len="{string-length($v)}"/>
            """);
        result.Should().Contain("count=\"1\"").And.Contain("isText=\"true\"").And.Contain("len=\"0\"");
    }

    /// <summary>
    /// The count is a DELTA across this body, not an absolute. An xsl:value-of in an enclosing
    /// typed body must not make an inner empty body manufacture a node.
    /// </summary>
    [Fact]
    public async Task AnOuterValueOfDoesNotLeakIntoAnInnerEmptyBody()
    {
        var result = await Run("""
            <xsl:variable name="outer" as="item()*">
              <xsl:value-of select="'x'"/>
              <xsl:variable name="inner" as="item()*"><xsl:sequence select="()"/></xsl:variable>
              <xsl:sequence select="count($inner)"/>
            </xsl:variable>
            <out innerCount="{$outer[last()]}"/>
            """);
        result.Should().Contain("innerCount=\"0\"");
    }

    /// <summary>
    /// An expectation shaped exactly like XSpec's: a function returning () captured into an
    /// as="item()*" variable, compared against (). This is the comparison that was failing.
    /// </summary>
    [Fact]
    public async Task EmptyFunctionResultDeepEqualsTheEmptySequence()
    {
        var result = await Run("""
            <xsl:variable name="r" as="item()*"><xsl:sequence select="()"/></xsl:variable>
            <out equal="{deep-equal($r, ())}"/>
            """);
        result.Should().Contain("equal=\"true\"");
    }
}
