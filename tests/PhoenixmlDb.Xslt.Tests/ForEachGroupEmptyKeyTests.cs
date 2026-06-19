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
}
