using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xsl:key use expression resolves its prefixes against the xsl:key element's in-scope
/// namespaces, including the implicitly bound xml prefix.
/// </summary>
/// <remarks>
/// The use expression was parsed with no namespace context, so its name tests were resolved at
/// transform time against the stylesheet's declared namespaces, where the implicitly bound xml
/// prefix never appears. use="@xml:id" then meant an unprefixed id attribute: key('id', 'x') found nothing in a document that uses
/// xml:id, and found the element anyway if it also happened to carry a plain id="x". DocBook
/// xslTNG declares exactly that key and resolves every link with it, so each
/// &lt;link linkend="..."/&gt; was reported as "Link to non-existent ID" and rendered as
/// [???id???]. Saxon resolves all of them.
/// </remarks>
public sealed class KeyUseNamespaceTests
{
    private static async Task<string> RunAsync(string keyDeclaration, string lookup, string source)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              {keyDeclaration}
              <xsl:template match="/"><xsl:value-of select="{lookup}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync(source)).Trim();
    }

    private const string XmlIdOnly = """<doc><x xml:id="a">A</x><y xml:id="b">B</y></doc>""";

    [Fact]
    public async Task XmlIdKey_FindsTheElement()
        => (await RunAsync("""<xsl:key name="id" match="*" use="@xml:id"/>""",
            "key('id', 'b')", XmlIdOnly)).Should().Be("B");

    /// <summary>A plain id attribute is a different attribute; it must not satisfy @xml:id.</summary>
    [Fact]
    public async Task XmlIdKey_DoesNotMatchAPlainIdAttribute()
        => (await RunAsync("""<xsl:key name="id" match="*" use="@xml:id"/>""",
            "count(key('id', 'b'))", """<doc><y id="b">B</y></doc>""")).Should().Be("0");

    /// <summary>The xslTNG shape: the key looked up for each link, in a namespaced document.</summary>
    [Fact]
    public async Task XmlIdKey_ResolvesLinkendsInADocBookDocument()
        => (await RunAsync("""<xsl:key name="id" match="*" use="@xml:id"/>""",
            "string-join(//*:link ! key('id', @linkend)/*:title, '|')",
            """
            <article xmlns="http://docbook.org/ns/docbook" xml:id="a1">
              <section xml:id="s1"><title>One</title><para><link linkend="s2"/></para></section>
              <section xml:id="s2"><title>Two</title><para><link linkend="s1"/></para></section>
            </article>
            """)).Should().Be("Two|One");

    /// <summary>
    /// A prefix declared on xsl:key itself, not on the stylesheet root. This already worked; it
    /// guards the change of namespace context for the use expression.
    /// </summary>
    [Fact]
    public async Task PrefixDeclaredOnTheKeyElement_IsInScopeForUse()
        => (await RunAsync("""<xsl:key name="k" match="*" use="@p:ref" xmlns:p="urn:p"/>""",
            "key('k', 'r1')", """<doc xmlns:q="urn:p"><e q:ref="r1">E</e><e ref="r1">wrong</e></doc>"""))
            .Should().Be("E");

    /// <summary>The match pattern already resolved @xml:id; this keeps it that way.</summary>
    [Fact]
    public async Task XmlIdInTheMatchPattern_StillWorks()
        => (await RunAsync("""<xsl:key name="k" match="*[@xml:id = 'b']" use="local-name()"/>""",
            "key('k', 'y')", XmlIdOnly)).Should().Be("B");
}
