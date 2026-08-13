using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Found 2026-08-13 while generating DocBook xslTNG's <c>param.xsl</c> with our own engine
/// (<c>tools/generate-parameters.xsl</c>). Adding a namespace node for a (prefix, uri) the
/// element under construction already has is a NO-OP, not a duplicate declaration —
/// <c>XTDE0430</c> covers only the case where the URIs DIFFER. The engine emitted both,
/// producing two identical <c>xmlns:p</c> attributes on one start tag: output that is not
/// well-formed XML and cannot be reparsed at all.
///
/// The generator is an unusually good stress test for this: <c>xsl:namespace-alias</c> puts
/// the result root in the XSL namespace and <c>&lt;xsl:namespace name="xsl"&gt;</c> then
/// re-declares that same binding.
/// </summary>
public sealed class NamespaceDuplicateDeclarationTests
{
    private static async Task<string> RunAsync(string stylesheet, string source = "<r/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(source);
    }

    /// <summary>The xslTNG generate-parameters.xsl shape.</summary>
    [Fact]
    public async Task NamespaceAlias_PlusExplicitXslNamespace_DeclaresBindingOnce()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:x="urn:not-xslt" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:namespace-alias stylesheet-prefix="x" result-prefix="xsl"/>
              <xsl:template match="/">
                <x:stylesheet version="3.0">
                  <xsl:namespace name="xsl" select="'http://www.w3.org/1999/XSL/Transform'"/>
                  <xsl:namespace name="db" select="'urn:docbook'"/>
                </x:stylesheet>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);

        r.Split("xmlns:xsl=").Length.Should().Be(2, "the xsl binding must be declared exactly once");
        r.Should().Contain("""xmlns:db="urn:docbook" """.TrimEnd(), "the other namespace node is unaffected");
    }

    /// <summary>
    /// The general case: an <c>xsl:namespace</c> repeating a declaration the literal result
    /// element already carries.
    /// </summary>
    [Fact]
    public async Task XslNamespace_RepeatingLreDeclaration_DeclaresBindingOnce()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <out xmlns:c="urn:c"><xsl:namespace name="c" select="'urn:c'"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);

        r.Split("xmlns:c=").Length.Should().Be(2);
    }

    /// <summary>Two identical xsl:namespace instructions on one element collapse to one.</summary>
    [Fact]
    public async Task TwoIdenticalXslNamespaces_DeclareBindingOnce()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <out>
                  <xsl:namespace name="b" select="'urn:b'"/>
                  <xsl:namespace name="b" select="'urn:b'"/>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);

        r.Split("xmlns:b=").Length.Should().Be(2);
        r.Should().Contain("""xmlns:b="urn:b" """.TrimEnd());
    }

    /// <summary>
    /// The dedup must not swallow a genuine conflict: same prefix, DIFFERENT uri is still
    /// XTDE0430.
    /// </summary>
    [Fact]
    public async Task XslNamespace_ConflictingUri_StillRaisesXTDE0430()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:a="urn:a" version="3.0">
              <xsl:template match="/">
                <a:x><xsl:namespace name="a" select="'urn:different'"/></a:x>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var act = async () => await RunAsync(ss);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0430");
    }
}
