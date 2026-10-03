using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// key() matches a node when one of its key values equals a requested value "under the rules
/// appropriate to the XPath eq operator", and values that are not comparable are not equal
/// (XSLT 3.0 §20.2.2). eq compares xs:untypedAtomic as xs:string, so an untyped key value never
/// equals a number.
/// </summary>
/// <remarks>
/// An untyped key value was cast to a number when the requested value was numeric, so
/// key('k', 7) also returned the nodes whose key was xs:untypedAtomic("7") (W3C key-088; Saxon
/// returns only the nodes keyed by the integer 7).
/// </remarks>
public sealed class KeyValueComparisonTests
{
    private const string Source = """<doc><e n="7" s="abcdefg">A</e><e n="8" s="abcdefgh">B</e></doc>""";

    private static async Task<string> RunAsync(string use, string lookup)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:key name="k" match="e" use="{use}"/>
              <xsl:template match="/"><xsl:value-of select="string-join(key('k', {lookup}), ',')"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(Source);
    }

    [Theory]
    [InlineData("@n", "7")]                                  // untypedAtomic key, integer lookup
    [InlineData("xs:untypedAtomic(string-length(@s))", "7")]
    [InlineData("string(@n)", "7")]                          // xs:string key, integer lookup
    [InlineData("string-length(@s)", "'7'")]                 // integer key, string lookup
    [InlineData("string-length(@s)", "doc/e[1]/@n")]         // integer key, untypedAtomic lookup
    public async Task StringAgainstNumber_DoesNotMatch(string use, string lookup)
        => (await RunAsync(use, lookup)).Should().BeEmpty();

    [Theory]
    [InlineData("@n", "'7'", "A")]                           // untyped key as string
    [InlineData("@n", "doc/e[1]/@n", "A")]                   // untyped against untyped
    [InlineData("string-length(@s)", "7", "A")]              // integer against integer
    [InlineData("string-length(@s)", "7.0e0", "A")]          // numeric promotion still applies
    [InlineData("xs:integer(@n)", "8", "B")]
    public async Task ComparableValues_StillMatch(string use, string lookup, string expected)
        => (await RunAsync(use, lookup)).Should().Be(expected);
}
