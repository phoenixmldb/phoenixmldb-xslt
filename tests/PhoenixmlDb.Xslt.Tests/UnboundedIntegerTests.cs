using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xs:integer</c> has no upper bound in XSD, so a value wider than <c>long</c> must survive.
/// </summary>
/// <remarks>
/// <para>
/// The engine represents an integer as <c>long</c> where it fits and as <c>BigInteger</c> where
/// it does not. <c>MatchesItemType</c> already accepted a BigInteger for <c>xs:integer</c>, and
/// <c>xs:unsignedLong()</c> already returned one for values above <c>long.MaxValue</c>. The
/// <c>xs:integer()</c> constructor was the only step in that chain that refused to carry it.
/// </para>
/// <para>
/// It first crashed outright — <c>Convert.ToInt64</c> on a BigInteger raises
/// InvalidCastException, because BigInteger does not implement IConvertible, and the catch
/// clauses did not cover it. A stylesheet author saw a raw CLR message.
/// </para>
/// </remarks>
public class UnboundedIntegerTests
{
    private static async Task<string> RunAsync(string body)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\">" + body + "</xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>
    /// ulong.MaxValue, the value XSpec's report-sequence suite reports. It is wider than long.
    /// </summary>
    [Fact]
    public async Task XsInteger_AcceptsAValueWiderThanLong()
    {
        var result = await RunAsync(
            "<out><xsl:value-of select=\"xs:integer('18446744073709551615')\"/></out>");
        result.Should().Be("<out>18446744073709551615</out>");
    }

    /// <summary>Round-tripping xs:unsignedLong through xs:integer — the path that crashed.</summary>
    [Fact]
    public async Task XsInteger_AcceptsTheResultOfXsUnsignedLong()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"u\" select=\"xs:unsignedLong('18446744073709551615')\"/>"
            + "<out><xsl:value-of select=\"xs:integer($u)\"/></out>");
        result.Should().Be("<out>18446744073709551615</out>");
    }

    /// <summary>A wide value is still an xs:integer, and still a number.</summary>
    [Fact]
    public async Task AWideInteger_IsStillAnIntegerAndStillCompares()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"b\" select=\"xs:integer('18446744073709551615')\"/>"
            + "<out isint=\"{$b instance of xs:integer}\" big=\"{$b > 9223372036854775807}\"/>");
        result.Should().Be("<out isint=\"true\" big=\"true\"/>");
    }

    /// <summary>Values that fit in long are unchanged.</summary>
    [Fact]
    public async Task NarrowIntegers_AreUnaffected()
    {
        var result = await RunAsync(
            "<out a=\"{xs:integer('42')}\" b=\"{xs:integer(-7)}\" c=\"{xs:integer('9223372036854775807')}\"/>");
        result.Should().Be("<out a=\"42\" b=\"-7\" c=\"9223372036854775807\"/>");
    }
}
