using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The content of a constructed element stays in the output buffer where it was written, and the
/// start tag, known only after the content, is moved before it (#639). It was cut out as a string
/// and written back after the start tag, so each level of a deep recursion copied all the content
/// below it. Every reader of a scope of that buffer still finds what it found before: each case's
/// expected result is the output before the change, byte for byte.
/// </summary>
[Collection(nameof(ElementContentInPlaceSerial))]
public sealed class ElementContentInPlaceTests
{
    // Each case runs with every element's content in place (limit 0), and with the default, which
    // cuts out content shorter than DefaultXsltExecutionContext.ShortContentLimit: the same bytes.
    private static async Task<string> RunAsync(string stylesheet, string source, bool inPlace)
    {
        var saved = DefaultXsltExecutionContext.ShortContentLimit;
        DefaultXsltExecutionContext.ShortContentLimit = inPlace ? 0 : saved;
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet);
            return await transformer.TransformAsync(source);
        }
        finally
        {
            DefaultXsltExecutionContext.ShortContentLimit = saved;
        }
    }

    private const string Source =
        """
        <r xmlns:p="urn:p"><a n="1"><p:b>t1</p:b><c/></a><a n="2"><p:b>t2</p:b></a><e/></r>
        """;

    public static TheoryData<string, bool> Cases => Combine("where-populated", "on-empty", "try-rollback", "result-document", "temp-tree", "sequence-copy", "streaming", "prefix-fixup");

    private static TheoryData<string, bool> Combine(params string[] names)
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in names)
        {
            data.Add(name, true);
            data.Add(name, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_scope_reader_finds_the_content_it_found_before(string name, bool inPlace)
    {
        var (stylesheet, expected) = Case(name);

        (await RunAsync(stylesheet, Source, inPlace)).Should().Be(expected);
    }

    // A global variable that the static order missed (it is reached through a function) is
    // initialized when its value is first asked for, here inside the content of elements of
    // another global; the code that does it reads the StringBuilder under the output buffer
    // itself, so the buffer makes its insertions at once while it runs (OutputBuffer.OpenRaw).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_global_initialized_inside_element_content_reads_the_buffer_with_its_insertions_made(bool inPlace)
    {
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="f">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:variable name="first">
                <a n="1"><b><c>deep</c></b><d><xsl:copy-of select="f:second()"/></d><e/></a>
              </xsl:variable>
              <xsl:function name="f:second"><xsl:sequence select="$second"/></xsl:function>
              <xsl:variable name="second">
                <x><y><z>from second</z></y><w/></x>
              </xsl:variable>
              <xsl:template match="/"><out><xsl:copy-of select="$first"/></out></xsl:template>
            </xsl:stylesheet>
            """, "<r/>", inPlace);

        result.Should().Be("<out><a n=\"1\"><b><c>deep</c></b><d><x><y><z>from second</z></y><w/></x></d><e/></a></out>");
    }

    // A copy that is an item of a function result is read back from the buffer, from where the
    // element begins, and parsed, to give it the source element's base URI (XSLT 3.0 §11.9.1);
    // with the start tag moved before the content, the element begins where its content began.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_copy_in_a_function_result_is_read_back_from_where_its_start_tag_is(bool inPlace)
    {
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="f">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:function name="f:c" as="element()*"><xsl:param name="r"/><xsl:for-each select="$r/a"><xsl:copy><xsl:copy-of select="@*, node()"/></xsl:copy></xsl:for-each></xsl:function>
              <xsl:template match="/r"><xsl:variable name="s" select="f:c(.)"/><out><xsl:for-each select="$s"><c base="{base-uri(.)}"><xsl:copy-of select="."/></c></xsl:for-each></out></xsl:template>
            </xsl:stylesheet>
            """, """
            <r xml:base="http://example.org/base/"><a n="1"><b>t1</b><c/></a><a n="2"><b>t2</b></a></r>
            """, inPlace);

        result.Should().Be("""
            <out><c base="http://example.org/base/"><a n="1"><b>t1</b><c/></a></c><c base="http://example.org/base/"><a n="2"><b>t2</b></a></c></out>
            """);
    }

    private static (string Stylesheet, string Expected) Case(string name) => name switch
    {
        // xsl:where-populated drops an element whose content is empty or only attributes, and keeps one with element content.
        "where-populated" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r">
                <out>
                  <xsl:where-populated><empty><xsl:copy-of select="e/*"/></empty></xsl:where-populated>
                  <xsl:where-populated><full><xsl:for-each select="a"><xsl:copy><xsl:attribute name="k" select="@n"/><xsl:copy-of select="p:b"/></xsl:copy></xsl:for-each></full></xsl:where-populated>
                  <xsl:where-populated><xsl:element name="w"><xsl:attribute name="only">1</xsl:attribute></xsl:element></xsl:where-populated>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out xmlns:p="urn:p"><full><a k="1"><p:b>t1</p:b></a><a k="2"><p:b>t2</p:b></a></full></out>
            """),
        // xsl:on-non-empty and xsl:on-empty, with elements in the content and in the alternative.
        "on-empty" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r">
                <out>
                  <list><xsl:on-non-empty><head/></xsl:on-non-empty><xsl:for-each select="a"><item n="{@n}"><xsl:value-of select="."/></item></xsl:for-each><xsl:on-empty><none/></xsl:on-empty></list>
                  <list><xsl:on-non-empty><head/></xsl:on-non-empty><xsl:for-each select="e/*"><item/></xsl:for-each><xsl:on-empty><none><deep><x/></deep></none></xsl:on-empty></list>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out><list><head/><item n="1">t1</item><item n="2">t2</item></list><list><none><deep><x/></deep></none></list></out>
            """),
        // xsl:try with rollback-output discards an element whose content was partly written when the error came.
        "try-rollback" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r">
                <out>
                  <before><x/></before>
                  <xsl:try rollback-output="yes">
                    <attempt><inner a="1"><deep>text</deep><xsl:sequence select="error((), 'stop')"/></inner></attempt>
                    <xsl:catch><caught><xsl:value-of select="$err:description" xmlns:err="http://www.w3.org/2005/xqt-errors"/></caught></xsl:catch>
                  </xsl:try>
                  <xsl:try><ok><y><xsl:copy-of select="a[1]"/></y></ok><xsl:catch><never/></xsl:catch></xsl:try>
                  <after/>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out><before><x/></before><caught>stop</caught><ok><y><a xmlns:p="urn:p" n="1"><p:b>t1</p:b><c/></a></y></ok><after/></out>
            """),
        // xsl:result-document between two elements of the principal result.
        "result-document" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r">
                <out>
                  <first><xsl:copy-of select="a[1]"/></first>
                  <xsl:result-document href="side.xml"><side><xsl:copy-of select="a"/></side></xsl:result-document>
                  <last><xsl:copy-of select="a[2]"/></last>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out><first><a xmlns:p="urn:p" n="1"><p:b>t1</p:b><c/></a></first><last><a xmlns:p="urn:p" n="2"><p:b>t2</p:b></a></last></out>
            """),
        // temporary trees: attributes and a namespace node added by the content, nested copies, a typed variable.
        "temp-tree" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" xmlns:q="urn:q" exclude-result-prefixes="q">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r">
                <xsl:variable name="t">
                  <xsl:for-each select="a">
                    <xsl:element name="item">
                      <xsl:attribute name="n" select="@n"/>
                      <xsl:namespace name="q">urn:q</xsl:namespace>
                      <xsl:copy><xsl:copy-of select="@*"/><xsl:apply-templates/></xsl:copy>
                      <q:lit><xsl:attribute name="later">yes</xsl:attribute><xsl:value-of select="p:b"/></q:lit>
                    </xsl:element>
                  </xsl:for-each>
                </xsl:variable>
                <xsl:variable name="typed" as="element()*">
                  <xsl:for-each select="a"><xsl:copy><xsl:attribute name="typed" select="@n"/><xsl:copy-of select="*"/></xsl:copy></xsl:for-each>
                  <xsl:for-each select="e"><xsl:copy/></xsl:for-each>
                </xsl:variable>
                <out count="{count($t//*)}" typed="{count($typed)}"><xsl:copy-of select="$t"/><xsl:copy-of select="$typed"/><s><xsl:value-of select="serialize($t)"/></s></out>
              </xsl:template>
              <xsl:template match="p:b"><xsl:copy><xsl:value-of select="upper-case(.)"/></xsl:copy></xsl:template>
              <xsl:template match="*"><xsl:copy><xsl:apply-templates/></xsl:copy></xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out xmlns:p="urn:p" count="9" typed="3"><item xmlns:q="urn:q" n="1"><a n="1"><p:b>T1</p:b><c/></a><q:lit later="yes">t1</q:lit></item><item xmlns:q="urn:q" n="2"><a n="2"><p:b>T2</p:b></a><q:lit later="yes">t2</q:lit></item><a typed="1"><p:b>t1</p:b><c/></a><a typed="2"><p:b>t2</p:b></a><e/><s>&lt;item xmlns:q="urn:q" n="1"&gt;&lt;a xmlns:p="urn:p" n="1"&gt;&lt;p:b&gt;T1&lt;/p:b&gt;&lt;c/&gt;&lt;/a&gt;&lt;q:lit xmlns:p="urn:p" later="yes"&gt;t1&lt;/q:lit&gt;&lt;/item&gt;&lt;item xmlns:q="urn:q" n="2"&gt;&lt;a xmlns:p="urn:p" n="2"&gt;&lt;p:b&gt;T2&lt;/p:b&gt;&lt;/a&gt;&lt;q:lit xmlns:p="urn:p" later="yes"&gt;t2&lt;/q:lit&gt;&lt;/item&gt;</s></out>
            """),
        // xsl:copy whose result is an item of a function result (the copy is read back from the buffer).
        "sequence-copy" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="f xs">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:function name="f:copies" as="item()*">
                <xsl:param name="nodes" as="element()*"/>
                <xsl:for-each select="$nodes"><xsl:copy><xsl:attribute name="i" select="position()"/><xsl:copy-of select="node()"/></xsl:copy></xsl:for-each>
                <xsl:sequence select="1, 2"/>
              </xsl:function>
              <xsl:template match="/r">
                <xsl:variable name="s" select="f:copies(a)"/>
                <out n="{count($s)}"><xsl:for-each select="$s"><item><xsl:copy-of select="."/></item></xsl:for-each></out>
              </xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out n="4"><item><a xmlns:p="urn:p" i="1"><p:b>t1</p:b><c/></a></item><item><a xmlns:p="urn:p" i="2"><p:b>t2</p:b></a></item><item>1</item><item>2</item></out>
            """),
        // streamed xsl:copy and xsl:element with attributes added by the content. The output is the
        // one the transformation gives without streaming: <c/> with no namespace declaration, and
        // p:b with none either, as <r> declares p (xslt#159: the streamed copy redeclared it).
        "streaming" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
              <xsl:template match="a"><xsl:copy><xsl:attribute name="seen">yes</xsl:attribute><xsl:apply-templates/></xsl:copy></xsl:template>
              <xsl:template match="p:b"><xsl:copy><wrapped><xsl:value-of select="."/></wrapped></xsl:copy></xsl:template>
              <xsl:template match="e"><xsl:element name="was-e"><xsl:attribute name="x">1</xsl:attribute></xsl:element></xsl:template>
            </xsl:stylesheet>
            """,
            """
            <r xmlns:p="urn:p"><a seen="yes"><p:b><wrapped>t1</wrapped></p:b><c/></a><a seen="yes"><p:b><wrapped>t2</wrapped></p:b></a><was-e x="1"/></r>
            """),
        // the element prefix renamed for a namespace node in the content; empty elements; attributes only.
        "prefix-fixup" => (
            """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/r">
                <out>
                  <xsl:element name="p:x" namespace="urn:p"><xsl:namespace name="p">urn:other</xsl:namespace><child/></xsl:element>
                  <xsl:element name="p:y" namespace="urn:p"><xsl:namespace name="p">urn:another</xsl:namespace><xsl:attribute name="p:a" namespace="urn:p">v</xsl:attribute><c>t</c></xsl:element>
                  <xsl:for-each select="a[1]"><xsl:copy><xsl:namespace name="z">urn:z</xsl:namespace><xsl:attribute name="z:at" namespace="urn:z">1</xsl:attribute><d/></xsl:copy></xsl:for-each>
                  <empty1/><xsl:element name="empty2"/><xsl:for-each select="e"><xsl:copy/></xsl:for-each>
                  <attrs-only><xsl:attribute name="a">1</xsl:attribute></attrs-only>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """,
            """
            <out xmlns:p="urn:p"><p_0:x xmlns:p_0="urn:p" xmlns:p="urn:other"><child xmlns:p="urn:p"/></p_0:x><p_1:y xmlns:p_1="urn:p" xmlns:p="urn:another" xmlns:p_0="urn:p" p_0:a="v"><c xmlns:p="urn:p">t</c></p_1:y><a xmlns:z="urn:z" z:at="1"><d/></a><empty1/><empty2/><e/><attrs-only a="1"/></out>
            """),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}

[CollectionDefinition(nameof(ElementContentInPlaceSerial), DisableParallelization = true)]
public sealed class ElementContentInPlaceSerial;
