using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>key('prefix:name', …)</c> must find a key whose name was declared in a module that binds
/// that prefix to its own URI.
///
/// <c>XsltStylesheet.Namespaces</c> is a single prefix -> URI map written last-wins during
/// parsing, so a prefix bound to DIFFERENT URIs in different modules keeps only one of them and
/// the lookup builds the wrong expanded name. Whether a key resolved therefore depended on
/// module order alone: swapping two <c>xsl:include</c> lines flipped it between working and
/// <c>XTDE1260</c>.
///
/// XSpec is the motivating case — a dozen of its modules each declare their own
/// <c>xmlns:local</c> with a distinct URI, and <c>key('local:scenarios')</c> failed in 27 of its
/// 162 suites for exactly this reason.
/// </summary>
public sealed class KeyNameModulePrefixTests
{
    private const string Source = "<doc><item id='a'>A</item><item id='b'>B</item></doc>";

    private static async Task<string> RunAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(Source);
    }

    [Fact]
    public async Task Prefixed_key_resolves_when_another_prefix_binding_would_win_the_global_map()
    {
        // Two modules, both binding `local`, to DIFFERENT URIs. The key lives in one of them.
        // Inlined via a single stylesheet with two namespace scopes to keep the test self
        // contained; the failing shape is the same one xsl:include produces.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                            xmlns:local="urn:winner:local">
              <xsl:output method="text"/>
              <xsl:key name="other" match="item" use="@id"/>
              <xsl:template match="/" xmlns:local="urn:declarer:local">
                <xsl:value-of select="count(key('local:byid','a'))"/>
              </xsl:template>
              <xsl:key name="local:byid" match="item" use="@id" xmlns:local="urn:declarer:local"/>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Trim().Should().Be("1");
    }

    [Fact]
    public async Task Unprefixed_keys_are_unaffected()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:key name="byid" match="item" use="@id"/>
              <xsl:template match="/"><xsl:value-of select="count(key('byid','a'))"/></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Trim().Should().Be("1");
    }

    [Fact]
    public async Task An_undeclared_key_still_raises_XTDE1260()
    {
        // The fallback must not turn a genuinely missing key into a silent success.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:key name="byid" match="item" use="@id"/>
              <xsl:template match="/"><xsl:value-of select="count(key('nosuchkey','a'))"/></xsl:template>
            </xsl:stylesheet>
            """;
        var act = async () => await RunAsync(ss);
        await act.Should().ThrowAsync<Exception>().Where(e => e.Message.Contains("XTDE1260"));
    }
}
