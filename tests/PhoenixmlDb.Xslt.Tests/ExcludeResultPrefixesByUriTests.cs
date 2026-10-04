using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// exclude-result-prefixes excludes namespace URIs: each prefix is resolved where the attribute
/// appears, and a binding on a literal result element is excluded when its URI is
/// (XSLT 3.0 §11.1.3).
/// </summary>
/// <remarks>
/// Exclusions were kept as prefix names and inherited as names, so an ancestor's
/// exclude-result-prefixes="c" (c.uri) also removed a descendant's own xmlns:c="e.uri"
/// (W3C namespace-0911).
/// </remarks>
public sealed class ExcludeResultPrefixesByUriTests
{
    private static async Task<string> RunAsync(string body, string rootAttributes = "")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" {rootAttributes}>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync("<doc/>");
    }

    [Fact]
    public async Task RedeclaredPrefix_WithAnotherUri_IsKept()
    {
        var output = await RunAsync("""
            <alpha xmlns:a="a.uri" xmlns:b="b.uri" xmlns:c="c.uri" xsl:exclude-result-prefixes="a c">
              <beta xmlns:a="c.uri" xmlns:b="d.uri" xmlns:c="e.uri" xsl:exclude-result-prefixes="b"/>
            </alpha>
            """);
        output.Should().Contain("""<beta xmlns:c="e.uri"/>""")       // c=e.uri kept; a=c.uri, b=d.uri excluded
            .And.Contain("""xmlns:b="b.uri""")
            .And.NotContain("a.uri").And.NotContain("c.uri").And.NotContain("d.uri");
    }

    [Fact]
    public async Task StylesheetLevelExclusion_IsByUri()
        => (await RunAsync("""<r><s xmlns:x="other.uri"/></r>""",
                rootAttributes: """xmlns:x="x.uri" exclude-result-prefixes="x" """))
            .Should().Contain("""<s xmlns:x="other.uri"/>""").And.NotContain("x.uri");

    [Fact]
    public async Task ExcludedUri_UnderAnotherPrefix_IsAlsoExcluded()
        => (await RunAsync("""<r xmlns:y="x.uri"/>""",
                rootAttributes: """xmlns:x="x.uri" exclude-result-prefixes="x" """))
            .Should().NotContain("x.uri");

    [Fact]
    public async Task PlainExclusion_StillRemovesTheBinding()
        => (await RunAsync("""<r xmlns:p="p.uri" xsl:exclude-result-prefixes="p"><q/></r>"""))
            .Should().NotContain("p.uri");
}
