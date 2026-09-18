using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §24.2 names the constructs that impose temporary output state exactly: "xsl:variable,
/// xsl:param, xsl:with-param, xsl:function, xsl:key, xsl:sort, xsl:accumulator-rule, and
/// xsl:merge-key always evaluate the instructions in their contained sequence constructor in
/// temporary output state". Two of the eight did not — xsl:sort and xsl:key.
///
/// The symptom was not silence: xsl:result-document ran, wrote a final result tree per sorted item
/// or per keyed node, and the SECOND write reported XTDE1490, "two result trees with the same URI".
/// A true statement about a state that should have been unreachable, which is why it read as a
/// duplicate-output bug rather than a missing-check one (W3C result-document-1137, -1141).
/// </summary>
public sealed class TemporaryOutputStateTests
{
    private static async Task<string> RunExpectingErrorAsync(string stylesheet, string source = "<doc><i>2</i><i>1</i></doc>")
    {
        var t = new XsltTransformer();
        var act = async () =>
        {
            await t.LoadStylesheetAsync(stylesheet);
            await t.TransformAsync(source);
        };
        return (await act.Should().ThrowAsync<Exception>()).Which.Message;
    }

    /// <summary>
    /// A sort key built by a sequence constructor. Two items, so an unrejected
    /// xsl:result-document writes "out.xml" twice and the duplicate-URI rule fires second —
    /// which is what makes the assertion discriminating rather than just "some error".
    /// </summary>
    [Fact]
    public async Task ResultDocument_InASortKeySequenceConstructor_IsXTDE1480()
    {
        var message = await RunExpectingErrorAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out>
                  <xsl:perform-sort select="/doc/i">
                    <xsl:sort>
                      <xsl:result-document href="out.xml"><boo/></xsl:result-document>
                      <xsl:value-of select="."/>
                    </xsl:sort>
                  </xsl:perform-sort>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        message.Should().Contain("XTDE1480");
        message.Should().NotContain("XTDE1490");
    }

    /// <summary>The same for an xsl:key whose use-expression is a sequence constructor.</summary>
    [Fact]
    public async Task ResultDocument_InAKeyUseSequenceConstructor_IsXTDE1480()
    {
        var message = await RunExpectingErrorAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="k" match="i">
                <xsl:result-document href="out.xml"><boo/></xsl:result-document>
                <xsl:value-of select="."/>
              </xsl:key>
              <xsl:template match="/">
                <out><xsl:sequence select="key('k', '1')"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        message.Should().Contain("XTDE1480");
        message.Should().NotContain("XTDE1490");
    }

    /// <summary>
    /// Guard: the construct that already worked still does, so the new wrapper is not what any of
    /// this is being credited to.
    /// </summary>
    [Fact]
    public async Task ResultDocument_InAVariableBody_IsStillXTDE1480()
        => (await RunExpectingErrorAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:variable name="v">
                  <xsl:result-document href="out.xml"><boo/></xsl:result-document>
                </xsl:variable>
                <out><xsl:sequence select="$v"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """)).Should().Contain("XTDE1480");

    /// <summary>
    /// Guard: temporary output state is imposed on the CONTAINED SEQUENCE CONSTRUCTOR, not on the
    /// select attribute — a select expression writes nothing, so it has no output state to set.
    /// An ordinary sort with select must still sort, and the depth must be back to zero afterwards
    /// so that a later xsl:result-document at top level is still allowed.
    /// </summary>
    [Fact]
    public async Task SortBySelect_StillSortsAndLeavesFinalOutputState()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:perform-sort select="/doc/i">
                  <xsl:sort select="number(.)" data-type="number"/>
                </xsl:perform-sort>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("<doc><i>2</i><i>1</i><i>3</i></doc>")).Trim().Should().Be("123");
    }

    /// <summary>
    /// Guard on the same property for a sort key built by content: the sort still happens and the
    /// key values are still the constructed ones. Raising the depth around the evaluation must not
    /// change what the evaluation returns.
    /// </summary>
    [Fact]
    public async Task SortByContent_StillSorts()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:perform-sort select="/doc/i">
                  <xsl:sort><xsl:value-of select="."/></xsl:sort>
                </xsl:perform-sort>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("<doc><i>2</i><i>1</i><i>3</i></doc>")).Trim().Should().Be("123");
    }

    /// <summary>Guard: a key whose use is a sequence constructor still indexes correctly.</summary>
    [Fact]
    public async Task KeyByContent_StillIndexes()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:key name="k" match="i"><xsl:value-of select="@id"/></xsl:key>
              <xsl:template match="/">
                <xsl:value-of select="key('k', 'b')"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("""<doc><i id="a">1</i><i id="b">2</i></doc>""")).Trim().Should().Be("2");
    }
}
