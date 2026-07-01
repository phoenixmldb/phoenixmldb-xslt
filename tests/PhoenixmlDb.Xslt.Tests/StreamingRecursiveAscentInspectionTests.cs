using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Recursive streamable stylesheet functions (W3C strm su-inspection-002 /
/// su-ascent-005) that walk the ancestor axis of a streamed node until they reach
/// the top of the tree.
/// <para>
/// su-inspection-002: an arity-1 <c>streamability="inspection"</c> function
/// <c>f:depth</c> recurses on <c>$input/..</c> until
/// <c>$input/.. instance of document-node()</c>. Terminating requires the streamed
/// node's ancestor chain to top out at a synthetic document node (matching
/// non-streaming ancestor semantics); a null-terminated chain makes
/// <c>instance of document-node()</c> never true and the function recurses forever.
/// </para>
/// <para>
/// su-ascent-005: a recursive <c>streamability="ascent"</c> function returning the
/// outermost ancestor <c>section</c> of a streamed node, walking <c>ancestor::section</c>.
/// </para>
/// </summary>
public class StreamingRecursiveAscentInspectionTests
{
    private static async Task<string> Transform(string stylesheet, string inputXml, string file, string initialTemplate)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-recur-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate(initialTemplate, "http://www.w3.org/1999/XSL/Transform");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    // BOOKLIST/BOOKS/ITEM — ITEM sits 3 elements below the document node.
    private const string BooksXml = """
        <BOOKLIST>
          <BOOKS OWNER="MHK">
            <ITEM CAT="MMP"><WEIGHT UNIT="oz">6.1</WEIGHT></ITEM>
            <ITEM CAT="P"><WEIGHT UNIT="oz">11.2</WEIGHT></ITEM>
          </BOOKS>
        </BOOKLIST>
        """;

    // su-inspection-002: recursive inspection function counting ancestors up to
    // the document node. Must return 3 per ITEM and MUST NOT infinitely recurse.
    [Fact]
    public async Task RecursiveInspection_DepthToDocumentNode_TerminatesWithCorrectDepth()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:f="http://www.w3.org/xslt30tests/functions" exclude-result-prefixes="xs f">
              <xsl:mode streamable="yes"/>
              <xsl:function name="f:depth" as="xs:integer" streamability="inspection">
                <xsl:param name="input" as="node()"/>
                <xsl:sequence select="if ($input/.. instance of document-node()) then 1 else f:depth($input/..) + 1"/>
              </xsl:function>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <out><xsl:value-of select="(/BOOKLIST/BOOKS/ITEM) ! f:depth(.)"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, BooksXml, "books.xml", "initial-template");
        result.Should().Contain("<out>3 3</out>");
    }

    // recursive.xml: nested <section> elements, each <foot> deep inside.
    private const string RecursiveXml = """
        <chapter>
          <section id="1">
            <head>Section 1</head>
            <section id="1.1">
              <foot>End of Section 1.1</foot>
            </section>
            <section id="1.2">
              <section id="1.2.1">
                <foot>End of Section 1.2.1</foot>
              </section>
            </section>
          </section>
        </chapter>
        """;

    // su-ascent-005: recursive ascent function returning the OUTERMOST ancestor
    // section. Every <foot> resolves to section id="1". Must not infinitely recurse.
    [Fact]
    public async Task RecursiveAscent_OutermostAncestorSection_ReturnsRootSectionId()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:f="http://www.w3.org/xslt30tests/functions" exclude-result-prefixes="f">
              <xsl:mode streamable="yes"/>
              <xsl:function name="f:outermost-section" as="element(section)" streamability="ascent">
                <xsl:param name="input" as="node()"/>
                <xsl:sequence select="if ($input/ancestor::section) then f:outermost-section($input/ancestor::section[1]) else $input"/>
              </xsl:function>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="rec.xml">
                  <out><xsl:value-of select="//foot ! f:outermost-section(.) ! @id"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(sheet, RecursiveXml, "rec.xml", "initial-template");
        result.Should().Contain("<out>1 1</out>");
    }
}
