using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:transform</c> could not find a named template whose name is in a namespace, and said so
/// by quoting the local name of the template it had just failed to qualify.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PhoenixmlDb.Core.QName"/> equality compares the interned <c>NamespaceId</c>, not the
/// URI. That is right and fast for names the parser interned from one stylesheet, where equal URIs
/// get equal ids. A QName built at runtime by <c>fn:QName()</c> carries a hash-based id that never
/// equals the parser's, so the id-keyed <c>NamedTemplates</c> lookup missed.
/// </para>
/// <para>
/// This cost sixteen XSpec suites - every <c>external_*</c> one, since those invoke the stylesheet
/// under test through <c>fn:transform</c> and XSpec's entry points live in its own
/// <c>mirror:</c> namespace.
/// </para>
/// </remarks>
public class NamespacedInitialTemplateTests
{
    private const string Module = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:m="x-urn:test:m" version="3.0">
          <xsl:template name="m:greet" as="item()"><xsl:sequence select="'called-ok'"/></xsl:template>
          <xsl:template name="plain"  as="item()"><xsl:sequence select="'plain-ok'"/></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> Run(string initialTemplateExpr)
    {
        var dir = Path.Combine(Path.GetTempPath(), "phx-nsit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "mod.xsl"), Module);
            var caller = $$"""
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:m="x-urn:test:m" version="3.0">
                  <xsl:output method="text"/>
                  <xsl:template name="go">
                    <xsl:value-of select="transform(map{
                      'stylesheet-location': 'mod.xsl',
                      'initial-template': {{initialTemplateExpr}},
                      'delivery-format': 'raw'})?output"/>
                  </xsl:template>
                </xsl:stylesheet>
                """;
            var path = Path.Combine(dir, "caller.xsl");
            await File.WriteAllTextAsync(path, caller);

            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(caller, new Uri(path));
            t.SetInitialTemplate("go");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The reported shape: a namespaced entry point reached through fn:transform.</summary>
    [Fact]
    public async Task NamespacedInitialTemplateIsFound()
        => (await Run("QName('x-urn:test:m','greet')")).Should().Contain("called-ok");

    /// <summary>A prefixed lexical form denotes the same expanded name and must behave alike.</summary>
    [Fact]
    public async Task PrefixedLexicalFormIsEquivalent()
        => (await Run("QName('x-urn:test:m','m:greet')")).Should().Contain("called-ok");

    /// <summary>The no-namespace case always worked; it must keep working.</summary>
    [Fact]
    public async Task UnnamespacedInitialTemplateStillWorks()
        => (await Run("QName('','plain')")).Should().Contain("plain-ok");
}
