using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §11.6: an <c>xsl:map-entry</c> key is the ATOMIZED value of the key expression.
/// </summary>
/// <remarks>
/// <c>CreateMapEntryAsync</c> stored the evaluated key raw, so <c>key="AUTHOR"</c> stored the
/// element. The lookup side atomizes (correctly, per XPath 3.1 §17.1), so a node key could never
/// meet its lookup — including a lookup passing the very same node, which atomizes too. The entry
/// was unreachable by any key while <c>map:size</c>, <c>map:keys</c> and <c>map:for-each</c> all
/// reported it correctly. phoenixmldb-xslt#124.
/// </remarks>
public sealed class MapEntryKeyAtomizationTests : IDisposable
{
    private readonly string _dir;

    public MapEntryKeyAtomizationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxmapkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string Head = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:map="http://www.w3.org/2005/xpath-functions/map"
          xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:template name="main">
            <xsl:variable name="doc" as="element()"><ITEM><AUTHOR>Jane Austen</AUTHOR></ITEM></xsl:variable>
            <xsl:for-each select="$doc">
              <xsl:variable name="m" as="map(*)">
                <xsl:map><xsl:map-entry key="AUTHOR" select="'THE-VALUE'"/></xsl:map>
              </xsl:variable>
        """;

    private const string Tail = """
            </xsl:for-each>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Head + body + Tail, new Uri(Path.Combine(_dir, "s.xsl"))).ConfigureAwait(false);
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<in/>").ConfigureAwait(false);
    }

    /// <summary>The defect: a node-derived key was unreachable by the string it atomizes to.</summary>
    [Fact]
    public async Task A_node_derived_key_is_retrievable_by_its_atomized_string()
    {
        var r = await RunAsync("""<out v="{$m('Jane Austen')}"/>""");
        r.Should().Contain("THE-VALUE");
    }

    /// <summary>
    /// And unreachable by the node itself, which is what made it a data-loss bug rather than a
    /// type-coercion nuisance: there was no working form of the lookup.
    /// </summary>
    [Fact]
    public async Task A_node_derived_key_is_retrievable_by_the_node_too()
    {
        var r = await RunAsync("""<out v="{$m(AUTHOR)}"/>""");
        r.Should().Contain("THE-VALUE");
    }

    [Fact]
    public async Task The_stored_key_is_an_atomic_value_not_a_node()
    {
        var r = await RunAsync("""
            <out atomic="{map:keys($m) instance of xs:anyAtomicType}"
                 node="{map:keys($m) instance of node()}"/>
            """);
        r.Should().Contain("atomic=\"true\"");
        r.Should().Contain("node=\"false\"");
    }

    [Fact]
    public async Task Map_contains_agrees_with_the_lookup()
    {
        var r = await RunAsync("""<out c="{map:contains($m,'Jane Austen')}"/>""");
        r.Should().Contain("c=\"true\"");
    }

    /// <summary>
    /// The readers that worked BEFORE the fix must still work. map:for-each reached the entry when
    /// no lookup could, so it is the one place a regression would be invisible to the other tests.
    /// </summary>
    [Fact]
    public async Task Iteration_and_size_are_unchanged()
    {
        var r = await RunAsync("""
            <out size="{map:size($m)}" keys="{map:keys($m)}"
                 v="{map:for-each($m, function($k,$v){$v})}"/>
            """);
        r.Should().Contain("size=\"1\"");
        r.Should().Contain("keys=\"Jane Austen\"");
        r.Should().Contain("v=\"THE-VALUE\"");
    }

    /// <summary>
    /// THE DELIBERATE BEHAVIOUR CHANGE, pinned so it is a decision rather than a discovery.
    /// Before the fix, map:for-each handed the callback a NODE as $k — so a stylesheet doing
    /// name($k) or $k/.. worked. It now receives an atomic value, and that code breaks. This test
    /// exists to make that visible in review rather than in someone's stylesheet months later.
    /// </summary>
    [Fact]
    public async Task Iteration_now_yields_an_atomic_key_not_a_node()
    {
        var r = await RunAsync("""
            <out k="{map:for-each($m, function($k,$v){if ($k instance of node()) then 'NODE' else 'ATOMIC'})}"/>
            """);
        r.Should().Contain("k=\"ATOMIC\"", "atomizing at storage changes $k for code that currently works");
    }

    /// <summary>
    /// xsl:map now uses XdmMapKeyComparer like every other map constructor. Cross-type matching
    /// already worked via MapKeyHelper's rescan-on-miss, so this pins behaviour that must NOT
    /// change while the linear scan goes away.
    /// </summary>
    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1.0", "1")]
    [InlineData("'x'", "xs:untypedAtomic('x')")]
    [InlineData("xs:untypedAtomic('x')", "'x'")]
    public async Task Cross_type_keys_still_match(string key, string lookup)
    {
        var t = new XsltTransformer();
        var xsl = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template name="main">
                <xsl:variable name="m" as="map(*)">
                  <xsl:map><xsl:map-entry key="{{key}}" select="'V'"/></xsl:map>
                </xsl:variable>
                <out v="{$m({{lookup}})}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        await t.LoadStylesheetAsync(xsl, new Uri(Path.Combine(_dir, "c.xsl")));
        t.SetInitialTemplate("main");
        (await t.TransformAsync("<in/>")).Should().Contain("V");
    }

    /// <summary>A key expression yielding several items is an error, not a silent first-item pick.</summary>
    [Fact]
    public async Task A_multi_item_key_is_an_error()
    {
        var act = async () => await RunAsync("""
            <xsl:variable name="m2" as="map(*)">
              <xsl:map><xsl:map-entry key="(1,2)" select="'V'"/></xsl:map>
            </xsl:variable>
            <out v="{map:size($m2)}"/>
            """);
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XPTY0004*");
    }
}
