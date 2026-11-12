using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The functions an xsl:evaluate target expression can name (XSLT 3.0 §10.4.1): the F&amp;O
/// library, constructors, and user-defined functions that are not private or hidden. The
/// XSLT-defined functions (Appendix G.2) are deliberately left out, except json-to-xml,
/// xml-to-json and collation-key, which F&amp;O 3.1 defines too. Naming anything else is XTDE3160.
/// </summary>
/// <remarks>
/// Only current, current-output-uri and system-property were refused. A stylesheet function with
/// no visibility attribute, which is private to its package, could be called (W3C evaluate-045),
/// as could document() (evaluate-047), key() and the grouping functions. Saxon refuses all of
/// them. A function item obtained outside and passed in is a value, not a name in the target
/// expression's static context, so it stays callable.
/// </remarks>
public sealed class EvaluateFunctionAvailabilityTests
{
    private static async Task<string> EvaluateAsync(string xpath, string withParam = "")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:f="urn:f" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:param name="xp" as="xs:string"/>
              <xsl:key name="k" match="*" use="name()"/>
              <xsl:function name="f:pub" visibility="public" as="xs:integer">
                <xsl:param name="x" as="xs:integer"/><xsl:sequence select="$x + 1"/>
              </xsl:function>
              <xsl:function name="f:unstated" as="xs:integer">
                <xsl:param name="x" as="xs:integer"/><xsl:sequence select="$x * 2"/>
              </xsl:function>
              <xsl:function name="f:priv" visibility="private" as="xs:integer">
                <xsl:param name="x" as="xs:integer"/><xsl:sequence select="$x * 3"/>
              </xsl:function>
              <xsl:template match="/">
                <xsl:evaluate xpath="$xp" context-item=".">{withParam}</xsl:evaluate>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetParameter("xp", xpath);
        return (await t.TransformAsync("<doc><a/></doc>")).Trim();
    }

    [Theory]
    [InlineData("f:unstated(1)")]
    [InlineData("f:unstated#1(1)")]
    [InlineData("f:priv(1)")]
    public async Task PrivateStylesheetFunction_IsXTDE3160(string xpath)
    {
        var act = () => EvaluateAsync(xpath);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3160");
    }

    [Theory]
    [InlineData("document('x.xml')")]
    [InlineData("key('k', 'a')")]
    [InlineData("current-group()")]
    [InlineData("regex-group(1)")]
    [InlineData("unparsed-entity-uri('e')")]
    public async Task XsltDefinedFunction_IsXTDE3160(string xpath)
    {
        var act = () => EvaluateAsync(xpath);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3160");
    }

    [Theory]
    [InlineData("f:pub(1)", "2")]
    [InlineData("count(json-to-xml('[1,2]')//*:number)", "2")]
    [InlineData("map:size(map{1:2})", "1")]
    public async Task AvailableFunctions_StillWork(string xpath, string expected)
        => (await EvaluateAsync(xpath)).Should().Be(expected);

    /// <summary>A private function's item passed in from outside is a value, and callable.</summary>
    [Fact]
    public async Task PrivateFunctionItemPassedIn_IsCallable()
        => (await EvaluateAsync("$fn(21)", """<xsl:with-param name="fn" select="f:unstated#1"/>""")).Should().Be("42");

    // fn:function-lookup reaches the same functions by a dynamic name. Inside the target it found
    // and called document(), which a direct call refuses: two spellings of one call disagreed
    // (W3C evaluate-048, spec bug 30049). The document is a local file, so no test needs a network.
    private static string LocalDocumentUri()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phx-eval-lookup-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, "<found/>");
        return new Uri(path).AbsoluteUri;
    }

    private const string Fn = "'http://www.w3.org/2005/xpath-functions'";

    [Theory]
    [InlineData("document")]
    [InlineData("key")]
    [InlineData("current")]
    [InlineData("system-property")]
    public async Task FunctionLookupOfXsltFunction_InTarget_IsXTDE3160(string local)
    {
        var uri = LocalDocumentUri();
        var call = local switch
        {
            "document" => $"('{uri}')",
            "key" => "('k', 'a')",
            "current" => "()",
            _ => "('xsl:version')",
        };
        var arity = local switch { "key" => 2, "current" => 0, _ => 1 };
        var act = () => EvaluateAsync($"function-lookup(QName({Fn}, '{local}'), {arity}){call}");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3160");
    }

    [Fact]
    public async Task FunctionLookupOfFoFunction_InTarget_StillWorks()
        => (await EvaluateAsync($"function-lookup(QName({Fn}, 'abs'), 1)(-2)")).Should().Be("2");

    /// <summary>document#1 made in the stylesheet and passed in is a value, and callable.</summary>
    [Fact]
    public async Task DocumentItemPassedIn_IsCallable()
    {
        var uri = LocalDocumentUri();
        (await EvaluateAsync($"name($d('{uri}')/*)", """<xsl:with-param name="d" select="document#1"/>"""))
            .Should().Be("found");
    }

    /// <summary>function-lookup#2 made in the stylesheet has the stylesheet's static context.</summary>
    [Fact]
    public async Task FunctionLookupItemPassedIn_FindsDocument()
    {
        var uri = LocalDocumentUri();
        (await EvaluateAsync($"name($fl(QName({Fn}, 'document'), 1)('{uri}')/*)",
                """<xsl:with-param name="fl" select="function-lookup#2"/>"""))
            .Should().Be("found");
    }
}
