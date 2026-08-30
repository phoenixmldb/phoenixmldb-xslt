using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An accumulator declared with a sequence type keeps a SEQUENCE, and a node-typed one keeps
/// NODES. Two separate bugs in <c>CoerceAccumulatorValue</c> broke both halves of that.
///
/// 1. The <c>ZeroOrMore</c>/<c>OneOrMore</c> branch returned <c>List&lt;object?&gt;</c>. The
///    engine tells an XDM array from a sequence purely by CLR type — PhysicalOperators has
///    <c>ItemType.Array =&gt; item is List&lt;object?&gt;</c> — so every sequence-valued
///    accumulator came back as a ONE-ITEM ARRAY. <c>count()</c> answered 1 no matter how many
///    items had accumulated.
///
/// 2. <c>CoerceAtomicValue</c> atomized unconditionally. That is right for
///    <c>as="xs:double"</c> and wrong for <c>as="element()*"</c>, which must keep its nodes.
///    The elements became xs:untypedAtomic, which is invisible until something asks the value
///    for an axis step — and then reports the confusing "context item is not a node".
///
/// Both are silent: nothing throws, the accumulator just reports the wrong thing.
/// </summary>
public class AccumulatorSequenceValueTests
{
    private static async Task<string> Run(string body, string input)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:mode use-accumulators="#all"/>
              {body}
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        return await t.TransformAsync(input);
    }

    private const string Doc = "<r><v n='a'/><v n='b'/><v n='c'/><probe/></r>";

    private const string NodeAccumulator = """
        <xsl:accumulator name="stacked" as="element()*" initial-value="()">
          <xsl:accumulator-rule match="v" select="$value, ."/>
        </xsl:accumulator>
        """;

    [Fact]
    public async Task NodeValuedAccumulator_KeepsEveryItem_NotAOneItemArray()
    {
        var result = await Run($"""
            {NodeAccumulator}
            <xsl:template match="probe"><n><xsl:value-of select="count(accumulator-before('stacked'))"/></n></xsl:template>
            <xsl:template match="text()"/>
            """, Doc);
        result.Should().Contain("<n>3</n>", "three v elements precede the probe");
    }

    [Fact]
    public async Task NodeValuedAccumulator_IsNotAnArray()
    {
        var result = await Run($"""
            {NodeAccumulator}
            <xsl:template match="probe"><a><xsl:value-of select="accumulator-before('stacked') instance of array(*)"/></a></xsl:template>
            <xsl:template match="text()"/>
            """, Doc);
        result.Should().Contain("<a>false</a>");
    }

    /// <summary>
    /// The half that atomization broke: the accumulated items must still be nodes, so an axis
    /// step off them works. This is what XSpec does — it stores the x:variable elements and
    /// later reads @name off each one.
    /// </summary>
    [Fact]
    public async Task NodeValuedAccumulator_KeepsNodes_SoAnAxisStepStillWorks()
    {
        var result = await Run($"""
            {NodeAccumulator}
            <xsl:template match="probe"><s><xsl:value-of select="accumulator-before('stacked')/@n" separator=","/></s></xsl:template>
            <xsl:template match="text()"/>
            """, Doc);
        result.Should().Contain("<s>a,b,c</s>");
    }

    /// <summary>
    /// An atomic declared type must still atomize — the fix skips coercion only for node,
    /// item() and function/map/array types. Without this the guard could be written too broadly
    /// and the earlier "accumulator silently never updates" bug would come back.
    /// </summary>
    [Fact]
    public async Task AtomicValuedAccumulator_StillAtomizesAndCasts()
    {
        var result = await Run("""
            <xsl:accumulator name="total" as="xs:double" initial-value="0">
              <xsl:accumulator-rule match="v" select="$value + 1"/>
            </xsl:accumulator>
            <xsl:template match="probe"><t><xsl:value-of select="accumulator-before('total')"/></t></xsl:template>
            <xsl:template match="text()"/>
            """, Doc);
        result.Should().Contain("<t>3</t>");
    }

    /// <summary>
    /// One accumulator reading another. This is the shape that first exposed the atomization
    /// bug, because reading a node-valued accumulator from inside another rule is the earliest
    /// point where the atomized value is asked for an attribute.
    /// </summary>
    [Fact]
    public async Task AccumulatorReadingAnotherAccumulator_SeesNodes()
    {
        var result = await Run($"""
            {NodeAccumulator}
            <xsl:accumulator name="names" as="xs:string*" initial-value="()">
              <xsl:accumulator-rule match="v" select="accumulator-before('stacked') ! string(@n)"/>
            </xsl:accumulator>
            <xsl:template match="probe"><s><xsl:value-of select="accumulator-before('names')" separator=","/></s></xsl:template>
            <xsl:template match="text()"/>
            """, Doc);
        // Deliberately asserts only that the names came back as readable strings, which is what
        // this test is for: the nodes survived into the other rule and @n could be read off
        // them. It does NOT pin the boundary - we currently emit "a,b,c", meaning the 'stacked'
        // rule for the CURRENT node has already run when 'names' reads accumulator-before at
        // that same node. Whether the before-value should still exclude the current node is a
        // separate question, tracked on its own; asserting either answer here would bake in an
        // unverified reading of XSLT 3.0 18.2.
        result.Should().Contain("a,b", "the accumulated items must still be nodes with a readable @n");
    }
}
