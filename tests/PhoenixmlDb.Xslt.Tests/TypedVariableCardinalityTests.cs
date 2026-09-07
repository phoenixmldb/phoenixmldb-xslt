using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A typed <c>xsl:variable</c> whose declared type requires at least one item must raise
/// XTTE0570 when the supplied value is the empty sequence (XSLT 3.0 §9.3).
/// </summary>
/// <remarks>
/// <para>
/// The engine checked cardinality in the "too many items" direction only. Nothing checked "too
/// few": <c>as="xs:string+"</c> bound to a filter that matched nothing simply succeeded, and the
/// variable held the empty sequence in defiance of its own declaration. Every per-type branch of
/// the validator began by assuming there was an item to inspect, so an empty value slipped past
/// all of them.
/// </para>
/// <para>
/// The check is deliberately type-independent — how many items there are is decided before what
/// type they are. A zero-length string is explicitly NOT empty: it is a perfectly good single
/// xs:string item, and treating it as empty would reject correct stylesheets.
/// </para>
/// <para>
/// Found through XSpec's xspec-name suite, which relies on the error for control flow: its
/// x:xspec-name function declares <c>as="xs:string+"</c> over in-scope prefixes and expects
/// XTTE0570 when an element has none. Without the error the function returned normally, so the
/// suite's <c>?err?code</c> lookup hit a string and aborted the whole run with XPTY0004.
/// </para>
/// </remarks>
public class TypedVariableCardinalityTests
{
    private static async Task<Exception?> RunAsync(string variable)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\"><xsl:template name=\"main\"><out>"
            + variable + "<xsl:text>ok</xsl:text></out></xsl:template></xsl:stylesheet>";
        return await Record.ExceptionAsync(async () =>
        {
            var t = new PhoenixmlDb.Xslt.XsltTransformer();
            await t.LoadStylesheetAsync(xsl);
            t.SetInitialTemplate("main");
            await t.TransformAsync((string?)null);
        });
    }

    /// <summary>as="TYPE+" with an explicit empty sequence.</summary>
    [Fact]
    public async Task OneOrMore_WithEmptySequence_RaisesXTTE0570()
    {
        var ex = await RunAsync("<xsl:variable name=\"v\" as=\"xs:string+\" select=\"()\"/>");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTTE0570").And.Contain("$v");
    }

    /// <summary>
    /// The realistic shape: a predicate that filters everything away. This is what
    /// x:xspec-name does, and it is why the defect mattered in practice.
    /// </summary>
    [Fact]
    public async Task OneOrMore_FilteredToEmpty_RaisesXTTE0570()
    {
        var ex = await RunAsync(
            "<xsl:variable name=\"v\" as=\"xs:string+\" select=\"('a','b')[. eq 'zz']\"/>");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTTE0570");
    }

    /// <summary>as="TYPE" (exactly one) is equally unsatisfied by the empty sequence.</summary>
    [Fact]
    public async Task ExactlyOne_WithEmptySequence_RaisesXTTE0570()
    {
        var ex = await RunAsync("<xsl:variable name=\"v\" as=\"xs:string\" select=\"()\"/>");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTTE0570");
    }

    /// <summary>An empty sequence-constructor body is the same violation as select="()".</summary>
    [Fact]
    public async Task OneOrMore_WithEmptyBody_RaisesXTTE0570()
    {
        var ex = await RunAsync(
            "<xsl:variable name=\"v\" as=\"xs:string+\"><xsl:sequence select=\"()\"/></xsl:variable>");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTTE0570");
    }

    /// <summary>A zero-length string is one item, not an empty sequence. Must NOT raise.</summary>
    [Fact]
    public async Task ExactlyOne_WithZeroLengthString_IsAccepted()
    {
        var ex = await RunAsync("<xsl:variable name=\"v\" as=\"xs:string\" select=\"''\"/>");
        ex.Should().BeNull();
    }

    /// <summary>Types that permit zero items are unaffected.</summary>
    [Theory]
    [InlineData("xs:string*")]
    [InlineData("xs:string?")]
    [InlineData("element()*")]
    [InlineData("item()*")]
    public async Task TypesAllowingZero_AcceptTheEmptySequence(string declaredType)
    {
        var ex = await RunAsync(
            "<xsl:variable name=\"v\" as=\"" + declaredType + "\" select=\"()\"/>");
        ex.Should().BeNull();
    }

    /// <summary>A satisfied declaration still binds normally.</summary>
    [Fact]
    public async Task OneOrMore_WithItems_IsAccepted()
    {
        var ex = await RunAsync(
            "<xsl:variable name=\"v\" as=\"xs:string+\" select=\"('a','b')\"/>");
        ex.Should().BeNull();
    }
}
