using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A variable or parameter whose declared occurrence is <c>ExactlyOne</c> or <c>ZeroOrOne</c> must
/// raise XTTE0570 when its body produces the wrong number of items. Three of the four binding
/// seams did not check, and they failed in opposite directions:
///
/// <list type="bullet">
/// <item>local, node type — checked correctly (the model the other three now copy)</item>
/// <item>local, atomic type — bound every item, silently</item>
/// <item>global, node type — bound the EMPTY SEQUENCE, silently</item>
/// <item>global, atomic type — bound every item, silently</item>
/// </list>
///
/// The global-node case is the dangerous one. Binding empty raises nothing anywhere: <c>empty()</c>
/// returns true, <c>xsl:if</c> takes the other branch, and <c>xsl:for-each</c> never runs its body,
/// so the stylesheet emits output with a section missing. A test asserting the bound VALUE would
/// have passed while that persisted, which is why the guards below assert what the value then
/// does — that a <c>for-each</c> body actually executes — and not merely what it holds.
///
/// The global-atomic cell also moved once before: #132 changed it from binding one concatenated
/// value to binding both items. Both were non-conformant; this is the first version that raises.
/// </summary>
public sealed class TypedVariableCardinalityEnforcementTests
{
    private static async Task<string> RunAsync(string stylesheet, string source = "<doc><x/></doc>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    private static async Task ShouldRaise0570Async(string stylesheet)
    {
        var act = async () => await RunAsync(stylesheet);
        (await act.Should().ThrowAsync<System.Exception>())
            .Which.Message.Should().Contain("XTTE0570");
    }

    private const string Head = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
        """;

    // ---------- the three cells that were silent ----------

    /// <summary>Global, node type, two elements — bound the empty sequence with nothing raised.</summary>
    [Fact]
    public Task GlobalNodeType_TwoElements_Raises() => ShouldRaise0570Async(Head + """
          <xsl:variable name="v" as="element()"><a/><b/></xsl:variable>
          <xsl:template match="/"><xsl:value-of select="count($v)"/></xsl:template>
        </xsl:stylesheet>
        """);

    /// <summary>Global, atomic type, two items — bound both (and before #132, one value "1 2").</summary>
    [Fact]
    public Task GlobalAtomicType_TwoItems_Raises() => ShouldRaise0570Async(Head + """
          <xsl:variable name="v" as="xs:integer">
            <xsl:sequence select="1"/><xsl:sequence select="2"/>
          </xsl:variable>
          <xsl:template match="/"><xsl:value-of select="count($v)"/></xsl:template>
        </xsl:stylesheet>
        """);

    /// <summary>Local, atomic type, two items — bound both, silently.</summary>
    [Fact]
    public Task LocalAtomicType_TwoItems_Raises() => ShouldRaise0570Async(Head + """
          <xsl:template match="/">
            <xsl:variable name="v" as="xs:integer">
              <xsl:sequence select="1"/><xsl:sequence select="2"/>
            </xsl:variable>
            <xsl:value-of select="count($v)"/>
          </xsl:template>
        </xsl:stylesheet>
        """);

    /// <summary>ZeroOrOne given two items is equally a failure — at most one is permitted.</summary>
    [Fact]
    public Task GlobalOptionalAtomic_TwoItems_Raises() => ShouldRaise0570Async(Head + """
          <xsl:variable name="v" as="xs:integer?">
            <xsl:sequence select="1"/><xsl:sequence select="2"/>
          </xsl:variable>
          <xsl:template match="/"><xsl:value-of select="count($v)"/></xsl:template>
        </xsl:stylesheet>
        """);

    /// <summary>The cell that was already correct, and must stay correct.</summary>
    [Fact]
    public Task LocalNodeType_TwoElements_StillRaises() => ShouldRaise0570Async(Head + """
          <xsl:template match="/">
            <xsl:variable name="v" as="element()"><a/><b/></xsl:variable>
            <xsl:value-of select="count($v)"/>
          </xsl:template>
        </xsl:stylesheet>
        """);

    // ---------- guards: valid shapes must be untouched ----------

    /// <summary>
    /// The downstream guard. A correctly-bound single-element global must still be iterable — this
    /// is the assertion that would have caught the silent-empty mode, and the one that keeps it
    /// from returning. Asserting <c>count($v)</c> alone would not.
    /// </summary>
    [Fact]
    public async Task GlobalNodeType_OneElement_ForEachBodyStillRuns()
    {
        var r = await RunAsync(Head + """
              <xsl:variable name="v" as="element()"><a/></xsl:variable>
              <xsl:template match="/">
                <xsl:text>[</xsl:text>
                <xsl:for-each select="$v"><xsl:text>RAN</xsl:text></xsl:for-each>
                <xsl:text>]</xsl:text>
                <xsl:if test="$v"><xsl:text>IF</xsl:text></xsl:if>
                <xsl:value-of select="'|empty=' || empty($v)"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        r.Should().Be("[RAN]IF|empty=false");
    }

    /// <summary>ExactlyOne with exactly one item is the common case and must not move.</summary>
    [Fact]
    public async Task GlobalAtomicType_OneItem_Unchanged() =>
        (await RunAsync(Head + """
              <xsl:variable name="v" as="xs:integer"><xsl:sequence select="41"/></xsl:variable>
              <xsl:template match="/"><xsl:value-of select="($v + 1) || '|' || ($v instance of xs:integer)"/></xsl:template>
            </xsl:stylesheet>
            """)).Should().Be("42|true");

    /// <summary>Sequence types accept many items — the check must not fire for * or +.</summary>
    [Fact]
    public async Task SequenceTypes_AcceptMultipleItems() =>
        (await RunAsync(Head + """
              <xsl:variable name="n" as="xs:integer*">
                <xsl:sequence select="1"/><xsl:sequence select="2"/><xsl:sequence select="3"/>
              </xsl:variable>
              <xsl:variable name="e" as="element()*"><a/><b/></xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($n) || '/' || count($e)"/></xsl:template>
            </xsl:stylesheet>
            """)).Should().Be("3/2");

    /// <summary>
    /// ZeroOrOne with NO items is legal and must not raise — the check fires on "more than one",
    /// never on "none". A guard that would break if the two conditions were merged.
    /// </summary>
    [Fact]
    public async Task OptionalType_WithNoItems_DoesNotRaise() =>
        (await RunAsync(Head + """
              <xsl:variable name="v" as="xs:integer?">
                <xsl:if test="false()"><xsl:sequence select="1"/></xsl:if>
              </xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($v) || '|' || empty($v)"/></xsl:template>
            </xsl:stylesheet>
            """)).Should().Be("0|true");

    /// <summary>
    /// Regression guard for #132: a global <c>as="xs:anyURI?"</c> whose body yields the EMPTY
    /// string must bind one item, not the empty sequence. That fix and this check meet in the same
    /// branch, so this pins them against each other.
    /// </summary>
    [Fact]
    public async Task EmptyStringTypedGlobal_StillBindsOneItem() =>
        (await RunAsync(Head + """
              <xsl:variable name="v" as="xs:anyURI?"><xsl:sequence select="xs:anyURI('')"/></xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($v) || '|' || string-length($v)"/></xsl:template>
            </xsl:stylesheet>
            """)).Should().Be("1|0");

    /// <summary>A global with content and no <c>as</c> is a temporary tree and never cardinality-checked.</summary>
    [Fact]
    public async Task UntypedGlobal_IsUnaffected() =>
        (await RunAsync(Head + """
              <xsl:variable name="v"><xsl:text>a</xsl:text><xsl:value-of select="'b'"/></xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($v) || '|' || string($v)"/></xsl:template>
            </xsl:stylesheet>
            """)).Should().Be("1|ab");
}
