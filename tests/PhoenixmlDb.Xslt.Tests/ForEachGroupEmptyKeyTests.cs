using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §19.2: when an item's group-by expression atomizes to the EMPTY
/// SEQUENCE, that item contributes ZERO grouping keys and joins NO group.
/// Regression fixtures for the Martin Honnen report where PhoenixmlDb instead
/// synthesized a single group with an empty ("") grouping key.
/// </summary>
public class ForEachGroupEmptyKeyTests
{
    private const string Input = """
        <root>
            <item><name>item 1</name><category>cat1</category></item>
            <item><name>item 2</name><category>cat2</category></item>
            <item><name>item 3</name><category>cat1</category></item>
        </root>
        """;

    // group-by="@category" but items only have a <category> CHILD ELEMENT,
    // so @category is the empty sequence for EVERY item: NO group is formed.
    private static string AllEmptyKeysStylesheet(bool streamable) => $$"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            exclude-result-prefixes="#all">
            <xsl:mode on-no-match="shallow-copy" streamable="{{(streamable ? "yes" : "no")}}"/>
            <xsl:template match="root">
                <xsl:copy>
                    <xsl:for-each-group select="item" group-by="@category">
                        <category name="{current-grouping-key()}">
                            <xsl:apply-templates select="current-group()"/>
                        </category>
                    </xsl:for-each-group>
                </xsl:copy>
            </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachGroup_GroupBy_AllEmptyKeys_ProducesNoGroups(bool streamable)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(AllEmptyKeysStylesheet(streamable));
        var result = await transformer.TransformAsync(Input);

        // Body never runs: no <category> group elements, no synthetic name="".
        result.Should().NotContain("<category", $"actual:\n{result}");
        result.Should().NotContain("name=\"\"", $"actual:\n{result}");
    }

    // Some items have a non-empty key, some atomize to empty: only the non-empty
    // ones are grouped; the empty-key items are excluded (not lumped into "").
    private static string MixedKeysStylesheet(bool streamable) => $$"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            exclude-result-prefixes="#all">
            <xsl:mode on-no-match="shallow-copy" streamable="{{(streamable ? "yes" : "no")}}"/>
            <xsl:template match="root">
                <xsl:copy>
                    <xsl:for-each-group select="item" group-by="@category">
                        <group key="{current-grouping-key()}" count="{count(current-group())}"/>
                    </xsl:for-each-group>
                </xsl:copy>
            </xsl:template>
        </xsl:stylesheet>
        """;

    private const string MixedInput = """
        <root>
            <item category="a"><name>i1</name></item>
            <item><name>i2</name></item>
            <item category="b"><name>i3</name></item>
            <item category="a"><name>i4</name></item>
            <item><name>i5</name></item>
        </root>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachGroup_GroupBy_MixedKeys_ExcludesEmptyKeyItems(bool streamable)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(MixedKeysStylesheet(streamable));
        var result = await transformer.TransformAsync(MixedInput);

        // Exactly two groups: key="a" (2 items) and key="b" (1 item).
        // The two empty-key items must NOT appear in any group, and must NOT
        // form a key="" group.
        var groupCount = System.Text.RegularExpressions.Regex.Count(result, "<group ");
        groupCount.Should().Be(2, $"actual:\n{result}");
        result.Should().Contain("key=\"a\"", $"actual:\n{result}");
        result.Should().Contain("key=\"b\"", $"actual:\n{result}");
        result.Should().NotContain("key=\"\"", $"actual:\n{result}");
        result.Should().Contain("count=\"2\"", $"actual:\n{result}");
        result.Should().Contain("count=\"1\"", $"actual:\n{result}");
    }

    // ---- group-adjacent, which is NOT the same rule as group-by ----------------
    //
    // An empty group-by key means "this item joins no group" (above). An empty
    // group-adjacent key is an ERROR: XSLT 3.0 requires the group-adjacent
    // expression to evaluate to exactly one atomic value [XTTE1100].
    //
    // Martin Honnen's report (phoenixmldb/phoenixmldb-xslt#147) is the streamed
    // half: `group-adjacent="self::Line"` selects nothing on a non-Line sibling,
    // atomizes to empty, and the STREAMED executor had no cardinality check at
    // all — so the empty key flowed on and only surfaced later, as
    // `XTDE1071: current-grouping-key() called when there is no current grouping
    // key`, naming something that was not the problem. The buffered executor had
    // raised XTTE1100 correctly all along.
    //
    // The [Theory] over `streamable` is the point of this fixture: the two
    // executors must agree, and an assertion that only runs against one of them
    // is what let the pair drift apart in the first place.
    private static string AdjacentEmptyKeyStylesheet(bool streamable) => $$"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            exclude-result-prefixes="#all">
            <xsl:mode on-no-match="deep-skip" streamable="{{(streamable ? "yes" : "no")}}"/>
            <xsl:template match="root">
                <out>
                    <xsl:for-each-group select="*" group-adjacent="self::item">
                        <g/>
                    </xsl:for-each-group>
                </out>
            </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachGroup_GroupAdjacent_EmptyKey_RaisesXTTE1100(bool streamable)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(AdjacentEmptyKeyStylesheet(streamable));

        Func<Task> act = () => transformer.TransformAsync(AdjacentInput);

        (await act.Should().ThrowAsync<Exception>(
                "an empty group-adjacent key is XTTE1100 in both executors"))
            .Which.Message.Should().Contain("XTTE1100",
                streamable
                    ? "the streamed executor reported XTDE1071 before #147 — the wrong error, "
                      + "because it had no cardinality check to reach"
                    : "the buffered executor has always reported this correctly");
    }

    // `boolean(self::item)` is what the reporter MEANT to write. It yields a real
    // atomic value for every item, so it must keep working — a cardinality rule
    // drawn too broadly (rejecting anything mentioning a self step) would break
    // this, and it is the control that catches that.
    private static string AdjacentBooleanKeyStylesheet(bool streamable) => $$"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            exclude-result-prefixes="#all">
            <xsl:mode on-no-match="deep-skip" streamable="{{(streamable ? "yes" : "no")}}"/>
            <xsl:template match="root">
                <out>
                    <xsl:for-each-group select="*" group-adjacent="boolean(self::item)">
                        <g k="{current-grouping-key()}" n="{count(current-group())}"/>
                    </xsl:for-each-group>
                </out>
            </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachGroup_GroupAdjacent_BooleanKey_StillGroups(bool streamable)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(AdjacentBooleanKeyStylesheet(streamable));
        var result = await transformer.TransformAsync(AdjacentInput);

        // head, head, item, item  ->  two adjacent runs: false(2) then true(2).
        var groupCount = System.Text.RegularExpressions.Regex.Count(result, "<g ");
        groupCount.Should().Be(2, $"actual:\n{result}");
        result.Should().Contain("n=\"2\"", $"actual:\n{result}");
    }

    // An EMPTY ARRAY key. `data([])` is `()` (XSLT 3.0 §19.2 atomizes the grouping
    // key before the cardinality rule applies), so `[]` is an empty sequence by the
    // time XTTE1100 bites — even though an array is a single ITEM.
    //
    // The buffered executor raised this correctly all along, via a loose
    // IEnumerable test. A first revision of the shared helper carved arrays out as
    // "one item, never empty", which silently removed that. Caught in review
    // (parsers2 on #152); this test is why it cannot be removed again by accident.
    private static string AdjacentEmptyArrayKeyStylesheet(bool streamable) => $$"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            exclude-result-prefixes="#all">
            <xsl:mode on-no-match="deep-skip" streamable="{{(streamable ? "yes" : "no")}}"/>
            <xsl:template match="root">
                <out>
                    <xsl:for-each-group select="*" group-adjacent="[]">
                        <g/>
                    </xsl:for-each-group>
                </out>
            </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachGroup_GroupAdjacent_EmptyArrayKey_RaisesXTTE1100(bool streamable)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(AdjacentEmptyArrayKeyStylesheet(streamable));

        Func<Task> act = () => transformer.TransformAsync(AdjacentInput);

        (await act.Should().ThrowAsync<Exception>(
                "data([]) is the empty sequence, so an empty array key is XTTE1100"))
            .Which.Message.Should().Contain("XTTE1100");
    }

    private const string AdjacentInput = """
        <root>
            <head>h1</head>
            <head>h2</head>
            <item>i1</item>
            <item>i2</item>
        </root>
        """;
}
