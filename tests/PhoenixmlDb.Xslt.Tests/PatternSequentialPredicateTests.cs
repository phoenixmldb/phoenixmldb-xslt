using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression tests for chained (sequential) predicates in match patterns.
///
/// XPath filter semantics require that each predicate in <c>step[P1][P2]...[Pn]</c>
/// be applied to the sequence that survived the earlier predicates: <c>position()</c>
/// and <c>last()</c> inside <c>[P2]</c> are relative to the nodes kept by <c>[P1]</c>,
/// not to the original candidate set. The pattern matcher previously computed a single
/// (position, size) for the node under test and ANDed every predicate against it, which
/// is wrong whenever an earlier predicate re-indexes the sequence.
///
/// These are the shapes behind W3C match-021..028.
/// </summary>
public class PatternSequentialPredicateTests
{
    private static async Task<string> RunAsync(string match, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <out><xsl:apply-templates select="doc/*"/></out>
              </xsl:template>
              <xsl:template match="{{match}}"><hit><xsl:value-of select="."/></hit></xsl:template>
              <xsl:template match="*"/>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task Chained_positional_reindexes_after_first_predicate()
    {
        // x[position() > 1][1] selects the SECOND x:
        //   S0 = {x1,x2,x3}; [position()>1] -> {x2,x3} (reindexed 1,2); [1] -> x2.
        // The buggy single-position model matches nothing (no x has pos=1 AND pos>1).
        var result = await RunAsync("x[position() > 1][1]",
            "<doc><x>1</x><x>2</x><x>3</x></doc>");

        result.Should().Be("<out><hit>2</hit></out>");
    }

    [Fact]
    public async Task Chained_two_positional_predicates()
    {
        // x[(position() mod 2)=1][position() > 3] on eight x's:
        //   S0 = x1..x8; [(pos mod 2)=1] -> {x1,x3,x5,x7} (reindexed 1..4);
        //   [position()>3] -> the 4th survivor = x7.
        // The buggy model matches BOTH x5 and x7.
        var result = await RunAsync("x[(position() mod 2)=1][position() > 3]",
            "<doc><x>1</x><x>2</x><x>3</x><x>4</x><x>5</x><x>6</x><x>7</x><x>8</x></doc>");

        result.Should().Be("<out><hit>7</hit></out>");
    }

    [Fact]
    public async Task Boolean_predicate_reindexes_following_positional()
    {
        // foo[@k='c'][2] : filter to k='c' first, THEN take the 2nd of those.
        //   S0 = all foo; [@k='c'] -> {foo(2),foo(4),foo(5)}; [2] -> foo(4).
        // The buggy model applies [2] against the original index and matches foo(2).
        var result = await RunAsync("foo[@k='c'][2]",
            "<doc><foo k='a'>1</foo><foo k='c'>2</foo><foo k='b'>3</foo><foo k='c'>4</foo><foo k='c'>5</foo></doc>");

        result.Should().Be("<out><hit>4</hit></out>");
    }
}
