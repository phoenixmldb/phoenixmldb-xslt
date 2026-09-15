using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A stylesheet reaches the PhoeniXML extension functions by declaring xmlns:phx. The URI-to-id table mapped only the
/// W3C namespaces, so any other prefix got a dynamic id that no function carries: ft:stem was "not found" and
/// function-available('ft:stem', 1) was false, while Q{uri}stem worked. Needs PhoenixmlDb.XQuery with the phx
/// consolidation (functions in https://schemas.phoenixml.dev/2026/functions).
/// </summary>
public sealed class PhxExtensionFunctionTests
{
    private const string PhxNs = "xmlns:phx=\"https://schemas.phoenixml.dev/2026/functions\"";

    private static async Task<string> RunAsync(string namespaces, string expression)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" {{namespaces}} exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="{{expression}}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Fact]
    public async Task A_prefixed_phx_call_reaches_the_function()
        => (await RunAsync(PhxNs, "string-join(phx:tokenize('alpha beta'), ',')")).Should().Contain("alpha");

    [Fact]
    public async Task Function_available_sees_a_prefixed_phx_function()
        => (await RunAsync(PhxNs, "function-available('phx:stem', 1)")).Should().Be("true");

    [Fact]
    public async Task The_expanded_name_still_reaches_the_function()
        => (await RunAsync("", "string-join(Q{https://schemas.phoenixml.dev/2026/functions}tokenize('alpha beta'), ',')")).Should().Contain("alpha");

    [Fact]
    public async Task The_retired_ft_namespace_holds_nothing()
        => (await RunAsync("xmlns:ft=\"http://www.w3.org/2007/xpath-full-text\"", "function-available('ft:stem', 1)")).Should().Be("false");
}
