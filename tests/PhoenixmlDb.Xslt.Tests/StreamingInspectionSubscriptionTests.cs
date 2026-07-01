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

    // CONSUMING OUTERMOST: a for-each over outermost(//WEIGHT) whose body descends into
    // (or atomizes) the matched node's subtree (string(.) reads its full text) is NOT
    // inspection-only, so the empty-children inspection snapshot cannot serve it. But
    // because outermost matches never nest, materialize-and-skip is SOUND: the consuming
    // dispatch buffers each matched WEIGHT subtree (capturing its text/children) and
    // evaluates the body against it, yielding the correct atomized value. (Formerly this
    // punted to an empty <out/> — a conservative non-answer; the consuming-outermost path
    // now produces the genuinely correct result.)
    [Fact]
    public async Task DescendantConsumingBody_OutermostStreamsCorrectly()
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
        // string(.) is the matched WEIGHT's text value: 8 and 2.
        result.Trim().Should().Be("<out><v>8</v><v>2</v></out>");
    }

    // Input where each matched WEIGHT HAS element children. The consuming-outermost
    // materialize-and-skip gives the body full subtree access, so child-axis reads
    // resolve to their true values. Each test below pins one body position (choose/when
    // test, LRE AVT, xsl:variable select) that navigates the matched subtree.
    private const string ChildrenXml = """
        <BOOKLIST>
          <ITEM CAT="P"><WEIGHT UNIT="oz"><G>8</G></WEIGHT></ITEM>
          <ITEM CAT="H"><WEIGHT UNIT="lb"><G>2</G></WEIGHT></ITEM>
        </BOOKLIST>
        """;

    // xsl:choose / xsl:when test predicate reads child axis. Every WEIGHT has a <G>
    // child, so the consuming-outermost dispatch yields "has" per match.
    [Fact]
    public async Task ChooseWhenTest_ChildAxis_OutermostStreamsCorrectly()
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
        result.Trim().Should().Be("<out><v>has</v><v>has</v></out>");
    }

    // literal-result-element AVT attribute reads child axis; count(child::*) is 1.
    [Fact]
    public async Task LreAvtAttribute_ChildAxis_OutermostStreamsCorrectly()
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
        result.Trim().Should().Be("<out><v count=\"1\"/><v count=\"1\"/></out>");
    }

    // xsl:variable select reads child axis; the body atomizes count($k) == 1.
    [Fact]
    public async Task VariableSelect_ChildAxis_OutermostStreamsCorrectly()
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
        result.Trim().Should().Be("<out><v>1</v><v>1</v></out>");
    }

    // ---- Consuming outermost: self-atomizing body (sx-arithmetic-006/007/008) ----
    //
    // A for-each over outermost(//PRICE) whose body atomizes the bare context item
    // `.` (the streamed leaf's own text value) combined with an upward climb
    // (../@CAT) is NOT inspection-only — the empty-children inspection snapshot would
    // atomize `.` to "". But because outermost matches never nest, materialize-and-skip
    // is sound: the consuming dispatch buffers the matched leaf (capturing its text),
    // synthesizes the ancestor chain (so ../@CAT resolves), and evaluates the arithmetic
    // per match. Regression pin for sx-ArithmeticExpr-006 (. + string-length(../@CAT)).

    private const string PricesXml = """
        <BOOKLIST>
          <BOOKS OWNER="MHK">
            <ITEM CAT="MMP"><PRICE>4.95</PRICE></ITEM>
            <ITEM CAT="P"><PRICE>6.58</PRICE></ITEM>
            <ITEM CAT="H"><PRICE>16.47</PRICE></ITEM>
          </BOOKS>
        </BOOKLIST>
        """;

    [Fact]
    public async Task OutermostForEach_SelfAtomizePlusAncestorAttr_Streams()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//PRICE)"><a><xsl:value-of select="format-number(. + string-length(../@CAT), '0.00')"/></a></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, PricesXml, "b.xml");
        // 4.95+3=7.95 (MMP), 6.58+1=7.58 (P), 16.47+1=17.47 (H).
        result.Trim().Should().Be("<out><a>7.95</a><a>7.58</a><a>17.47</a></out>");
    }

    // sx-arithmetic-008 shape: a let-bound self-atomization + a global variable.
    [Fact]
    public async Task OutermostForEach_SelfAtomizeLetPlusGlobal_Streams()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:param name="two" as="xs:integer" select="2"/>
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//PRICE)"><a><xsl:value-of select="(let $p := round(number(.)) return max(($p, $p - 1))) + $two"/></a></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, PricesXml, "b.xml");
        // round(4.95)=5,max(5,4)+2=7; round(6.58)=7,max(7,6)+2=9; round(16.47)=16,max(16,15)+2=18.
        result.Trim().Should().Be("<out><a>7</a><a>9</a><a>18</a></out>");
    }

    // SOUNDNESS: a self-atomizing body under outermost must still honor the outermost
    // dedup — a PRICE nested inside another PRICE is NOT outermost and must be skipped
    // (its subtree is swallowed by the outer match's materialize). Only the OUTER
    // PRICE's atomized value (which, per XDM, is the concatenation of all descendant
    // text: "9" + "5" = "95") is emitted.
    [Fact]
    public async Task OutermostForEach_SelfAtomize_NestedMatch_ExcludesInner()
    {
        var nested = "<BOOKLIST><PRICE>9<PRICE>5</PRICE></PRICE></BOOKLIST>";
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:for-each select="outermost(//PRICE)"><a><xsl:value-of select="."/></a></xsl:for-each></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, nested, "b.xml");
        result.Trim().Should().Be("<out><a>95</a></out>");
    }
}
