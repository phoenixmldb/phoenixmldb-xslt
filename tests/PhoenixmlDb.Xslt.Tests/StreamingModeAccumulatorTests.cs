using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// W3C attr/mode conformance cases mode-1107a/b/c: use of accumulators that are / are not
/// applicable to the initial mode under streaming. Stylesheet mode-1106.xsl declares an
/// accumulator <c>counter</c> (initial 0, rule match="*" select="$value+1") and modes
/// X (use-accumulators="counter"), Y (use-accumulators=""), Z (use-accumulators="#all").
/// The template match="/" mode="X Y Z #unnamed" outputs
/// <c>&lt;out&gt;{accumulator-after('counter')}&lt;/out&gt;</c>.
/// Source has 3 elements (doc, str, yet) so counter-after on the document node = 3.
/// </summary>
public class StreamingModeAccumulatorTests
{
    private const string Mode1106 = """
        <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
           xmlns:xs="http://www.w3.org/2001/XMLSchema">
           <xsl:output method="xml" encoding="UTF-8" indent="no"/>
           <xsl:param name="STREAMABLE" select="false()" static="true"/>
           <xsl:accumulator name="counter" initial-value="0" as="xs:integer" _streamable="{$STREAMABLE}">
              <xsl:accumulator-rule match="*" select="$value + 1"/>
           </xsl:accumulator>
           <xsl:accumulator name="counter2" initial-value="0" as="xs:integer" _streamable="{$STREAMABLE}">
              <xsl:accumulator-rule match="*" select="$value + 1"/>
           </xsl:accumulator>
           <xsl:mode name="Q" _streamable="{$STREAMABLE}"/>
           <xsl:mode name="X" use-accumulators="counter" _streamable="{$STREAMABLE}"/>
           <xsl:mode name="Y" use-accumulators="" _streamable="{$STREAMABLE}"/>
           <xsl:mode name="Z" use-accumulators="#all" _streamable="{$STREAMABLE}"/>
           <xsl:mode use-accumulators="counter" _streamable="{$STREAMABLE}"/>
           <xsl:template match="/" mode="X&#x9;Y&#xa;Z&#xd;#unnamed">
              <out><xsl:value-of select="accumulator-after('counter')"/></out>
           </xsl:template>
        </xsl:transform>
        """;

    private const string Source = "<doc><str>brown-fox</str><yet>lazy-dog</yet></doc>";

    private static async Task<string> Run(string mode, bool streamable)
    {
        var t = new XsltTransformer();
        // Match the W3C harness exactly: it forwards the <param select="true()"/> literally,
        // which the parser maps to the streamable value "yes"/"no".
        var staticParams = new Dictionary<string, string> { ["STREAMABLE"] = streamable ? "true()" : "false()" };
        await t.LoadStylesheetAsync(Mode1106, null, staticParams);
        t.SetInitialMode(mode);
        return await t.TransformAsync(Source);
    }

    // ---- Streaming variants (STREAMABLE=true) : the actual mode-1107a/b/c cases ----

    [Fact] // mode-1107a
    public async Task Mode1107a_StreamingModeX_CounterApplicable_Yields3()
    {
        (await Run("X", streamable: true)).Should().Contain(">3</out>");
    }

    [Fact] // mode-1107b
    public async Task Mode1107b_StreamingModeY_CounterNotApplicable_RaisesXTDE3362()
    {
        var act = async () => await Run("Y", streamable: true);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE3362");
    }

    [Fact] // mode-1107c
    public async Task Mode1107c_StreamingModeZ_AllApplicable_Yields3()
    {
        (await Run("Z", streamable: true)).Should().Contain(">3</out>");
    }

    // ---- Non-streaming variants (STREAMABLE=false): prove the buffered path already works ----

    [Fact]
    public async Task NonStreaming_ModeX_Yields3()
    {
        (await Run("X", streamable: false)).Should().Contain(">3</out>");
    }

    [Fact]
    public async Task NonStreaming_ModeY_RaisesXTDE3362()
    {
        var act = async () => await Run("Y", streamable: false);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE3362");
    }

    [Fact]
    public async Task NonStreaming_ModeZ_Yields3()
    {
        (await Run("Z", streamable: false)).Should().Contain(">3</out>");
    }
}
