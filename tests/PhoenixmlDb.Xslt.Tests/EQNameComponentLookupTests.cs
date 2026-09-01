using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Naming a namespaced component through the string API, using EQName syntax.
/// </summary>
/// <remarks>
/// <c>SetInitialTemplate</c> and <c>SetInitialMode</c> take a string. EQName —
/// <c>Q{uri}local</c> — is the only way to name a component in a namespace through them,
/// because no prefix is in scope to bind.
///
/// <para>
/// The name was parsed by splitting on the first colon to find a prefix. A URI almost always
/// contains one, so <c>Q{http://example/x}main</c> split into the prefix <c>Q{http</c> and the
/// local name <c>//example/x}main</c>, and the lookup failed quoting a name the caller never
/// wrote.
/// </para>
/// </remarks>
public class EQNameComponentLookupTests
{
    private const string Stylesheet =
        "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
        + "xmlns:t=\"http://example/x\" version=\"3.0\" exclude-result-prefixes=\"#all\">"
        + "<xsl:template name=\"t:main\"><out>ran</out></xsl:template>"
        + "<xsl:template name=\"plain\"><out>plain</out></xsl:template>"
        + "</xsl:stylesheet>";

    /// <summary>An EQName whose URI contains a colon — the case that broke.</summary>
    [Fact]
    public async Task SetInitialTemplate_AcceptsAnEQName()
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        t.SetInitialTemplate("Q{http://example/x}main");
        (await t.TransformAsync((string?)null)).Should().Be("<out>ran</out>");
    }

    /// <summary>An EQName with an empty URI names a component in no namespace.</summary>
    [Fact]
    public async Task SetInitialTemplate_AcceptsAnEQNameWithNoNamespace()
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        t.SetInitialTemplate("Q{}plain");
        (await t.TransformAsync((string?)null)).Should().Be("<out>plain</out>");
    }

    /// <summary>A plain NCName still works. The EQName check must not capture this path.</summary>
    [Fact]
    public async Task SetInitialTemplate_StillAcceptsAPlainName()
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        t.SetInitialTemplate("plain");
        (await t.TransformAsync((string?)null)).Should().Be("<out>plain</out>");
    }
}
