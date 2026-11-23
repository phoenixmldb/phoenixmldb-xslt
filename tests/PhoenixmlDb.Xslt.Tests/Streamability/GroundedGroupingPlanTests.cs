using AwesomeAssertions;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Streamability;

/// <summary>
/// xsl:for-each-group over a grounded population (<c>item!copy-of()</c>) with a key that steps down
/// from the member (<c>group-adjacent="category"</c>) streams the input (BUGS.md #140, Martin
/// Honnen's grouping in xslt#575). The classifier reported any downward step as consuming and
/// refused a key that is not motionless, so the plan for the rule was BufferWholeInput: the whole
/// input was read into a tree before the rule ran (peak RSS 1439 MiB for 300 000 items, against
/// 101 MiB for a plain shallow copy). A step down from a member in memory reads nothing of the
/// stream (XSLT 3.0 §19.8.8.9).
/// </summary>
public class GroundedGroupingPlanTests
{
    private static readonly StreamingContext Streamed = new(Posture.Striding, InStreamedScope: true);

    private static string Stylesheet(string select, string key) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
          <xsl:output method="xml" indent="no"/>
          <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
          <xsl:template match="root">
            <xsl:copy>
              <xsl:for-each-group select="{select}" group-adjacent="{key}">
                <category name="{"{"}current-grouping-key(){"}"}"><xsl:apply-templates select="current-group()"/></category>
              </xsl:for-each-group>
            </xsl:copy>
          </xsl:template>
          <xsl:template match="item/category"/>
        </xsl:stylesheet>
        """;

    private const string Input = """
        <root><item><name>a</name><category>c1</category></item><item><name>b</name><category>c1</category></item><item><name>c</name><category>c2</category></item><item><name>d</name><category>c1</category></item></root>
        """;

    private static async Task<StreamingPlan> PlanOfRootRuleAsync(string select, string key)
    {
        var compiled = await CompiledStylesheet.CompileAsync(Stylesheet(select, key));
        XsltTemplate root = compiled.Stylesheet.Templates.Single(t => t.Body.Instructions.OfType<XsltCopy>().Any());
        return StreamingPlanner.Plan(root.Body, Streamed);
    }

    [Fact]
    public async Task AKeyThatStepsDownFromAGroundedMemberStreams()
        => (await PlanOfRootRuleAsync("item!copy-of()", "category")).Should().Be(StreamingPlan.StreamInline);

    [Fact]
    public async Task AKeyThatReadsTheStreamItselfIsStillBuffered()
    {
        // An absolute path reaches the streamed document, not the member in hand.
        var plan = await PlanOfRootRuleAsync("item!copy-of()", "/root/item[1]/category");

        plan.Should().BeOneOf(StreamingPlan.BufferMatchedSubtree, StreamingPlan.BufferWholeInput);
    }

    [Fact]
    public async Task TheStreamedGroupingGivesWhatTheTreeGives()
    {
        var sheet = Stylesheet("item!copy-of()", "category");
        var streamed = new XsltTransformer();
        await streamed.LoadStylesheetAsync(sheet);
        var tree = new XsltTransformer { DisableStreaming = true };
        await tree.LoadStylesheetAsync(sheet);

        var viaStream = await streamed.TransformAsync(Input);
        var viaTree = await tree.TransformAsync(Input);

        streamed.LastTransformStreamed.Should().BeTrue();
        tree.LastTransformStreamed.Should().BeFalse();
        viaStream.Should().Be(viaTree);
        viaStream.Should().EndWith(
            """<root><category name="c1"><item><name>a</name></item><item><name>b</name></item></category><category name="c2"><item><name>c</name></item></category><category name="c1"><item><name>d</name></item></category></root>""");
    }
}
