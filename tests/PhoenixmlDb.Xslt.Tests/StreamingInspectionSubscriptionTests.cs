using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Group B streaming: a streamable <c>xsl:for-each</c> over <c>outermost(//X)</c> /
/// <c>//X</c> whose per-match body is INSPECTION-ONLY (ancestor/ancestor-or-self/
/// parent/self/attribute navigation + atomize + set-ops over those — no child/
/// descendant axis, no copy-of/string of the matched node's subtree) now streams
/// via a non-consuming inspection subscription: the body dispatches per match
/// against an ancestor-synthesized snapshot WITHOUT materialize-and-skip, so the
/// forward pass continues into descendants where deeper matches fire.
/// <para>
/// The inspection-only guard is the soundness enforcement point: a
/// descendant-consuming body must NOT be dispatched non-consuming (it would
/// produce silently-wrong output). The negative fixtures pin that boundary.
/// </para>
/// </summary>
public class StreamingInspectionSubscriptionTests
{
    private static async Task<string> Transform(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Flat input: each WEIGHT under a distinct ITEM with @CAT; body inspects ancestor @CAT.
    private const string FlatXml = """
        <BOOKLIST>
          <ITEM CAT="P"><WEIGHT UNIT="oz">8</WEIGHT></ITEM>
          <ITEM CAT="H"><WEIGHT UNIT="lb">2</WEIGHT></ITEM>
        </BOOKLIST>
        """;

    // outermost(//WEIGHT) for-each with an ancestor-axis inspection body.
    [Fact]
    public async Task OutermostForEach_AncestorBody_StreamsPerMatch()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><v><xsl:value-of select="../@CAT"/></v></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, FlatXml, "b.xml");
        result.Trim().Should().Be("<out><v>P</v><v>H</v></out>");
    }

    // The conformance set-op shape: ancestor-or-self::*/@CAT intersect ../@*.
    [Fact]
    public async Task OutermostForEach_AncestorSetOp_StreamsPerMatch()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><v><xsl:value-of select="ancestor-or-self::*/@CAT intersect ../@*"/></v></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, FlatXml, "b.xml");
        result.Trim().Should().Be("<out><v>P</v><v>H</v></out>");
    }

    // Nesting: a WEIGHT inside a WEIGHT — outermost must yield only the OUTER one.
    [Fact]
    public async Task OutermostForEach_NestedMatch_ExcludesInner()
    {
        var nested = "<BOOKLIST><WEIGHT CAT=\"out\"><WEIGHT CAT=\"in\">x</WEIGHT></WEIGHT></BOOKLIST>";
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><v><xsl:value-of select="@CAT"/></v></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, nested, "b.xml");
        result.Trim().Should().Be("<out><v>out</v></out>");
    }

    // SOUNDNESS NEGATIVE: a descendant-consuming body (string(.) reads the matched
    // node's subtree text) must NOT be dispatched via the non-consuming inspection
    // path. The inspection-only guard (BodyConsumptionDetector.Consumes == true for
    // string(.)) keeps it UNREGISTERED, so it stays on its failing baseline (empty
    // <out/>) rather than producing the silently-WRONG output that unsound
    // non-consuming dispatch would.
    //
    // Why empty <out/> is the safe (not merely tolerated) outcome: the inspection
    // snapshot carries the matched element with EMPTY children (descendants are not
    // consumed). If string(.) were dispatched non-consuming against it, it would
    // atomize to "" per match and silently emit <out><v/><v/></out> — a
    // plausible-looking but WRONG result (the real values are 8 and 2). The guard
    // refuses to register it, so that wrong output is never produced. The construct
    // remains in the streaming baseline (a descendant-consuming // for-each is a
    // separate, out-of-scope effort), surfacing as the empty <out/>.
    [Fact]
    public async Task DescendantConsumingBody_NotStreamedUnsound()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><v><xsl:value-of select="string(.)"/></v></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, FlatXml, "b.xml");
        // It must NOT have been dispatched non-consuming: the unsound output would be
        // two empty <v/> elements. Assert that wrong shape is absent (the guard held).
        result.Should().NotContain("<v/>").And.NotContain("<v></v>");
        // It stays on its failing baseline — the unregistered // for-each emits the
        // empty wrapper, not the (impossible-here) correct 8/2 values.
        result.Trim().Should().Be("<out/>");
    }

    // Input where each matched WEIGHT HAS children. If a consuming body is
    // misclassified inspection-only and dispatched against the EMPTY-CHILDREN
    // snapshot, the child-reading position yields a wrong-but-plausible answer
    // ("none" / "0"). The hardened guard refuses to register such a body, so the
    // wrong value is never produced — the construct stays on its // for-each
    // baseline (empty <out/>). Each test below pins one previously-uninspected
    // visitor position.
    private const string ChildrenXml = """
        <BOOKLIST>
          <ITEM CAT="P"><WEIGHT UNIT="oz"><G>8</G></WEIGHT></ITEM>
          <ITEM CAT="H"><WEIGHT UNIT="lb"><G>2</G></WEIGHT></ITEM>
        </BOOKLIST>
        """;

    // HOLE 1: xsl:choose / xsl:when test predicate reads child axis.
    // Real answer: "has" (every WEIGHT has a <G> child). Empty snapshot → "none".
    [Fact]
    public async Task ChooseWhenTest_ChildAxis_NotStreamedUnsound()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><xsl:choose><xsl:when test="child::*"><v>has</v></xsl:when><xsl:otherwise><v>none</v></xsl:otherwise></xsl:choose></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, ChildrenXml, "b.xml");
        // The wrong-but-plausible empty-snapshot output is <v>none</v> per match.
        // The guard must keep that from being produced.
        result.Should().NotContain("none");
        result.Trim().Should().Be("<out/>");
    }

    // HOLE 2: literal-result-element AVT attribute reads child axis.
    // Real answer: count = 1. Empty snapshot → count = 0.
    [Fact]
    public async Task LreAvtAttribute_ChildAxis_NotStreamedUnsound()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><v count="{count(child::*)}"/></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, ChildrenXml, "b.xml");
        // The wrong-but-plausible empty-snapshot output is count="0" per match.
        result.Should().NotContain("count=\"0\"");
        result.Trim().Should().Be("<out/>");
    }

    // HOLE 3: xsl:variable select reads child axis; the body atomizes count($k).
    // Real answer: 1. Empty snapshot → 0.
    [Fact]
    public async Task VariableSelect_ChildAxis_NotStreamedUnsound()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//WEIGHT)"><xsl:variable name="k" select="*"/><v><xsl:value-of select="count($k)"/></v></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, ChildrenXml, "b.xml");
        // The wrong-but-plausible empty-snapshot output is <v>0</v> per match.
        result.Should().NotContain("<v>0</v>");
        result.Trim().Should().Be("<out/>");
    }
}
