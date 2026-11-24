using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// What #639 computes once per node or per level instead of by a walk of all levels above it: a
/// copied source element's in-scope namespaces and base URI (kept per element, from its parent's),
/// the default namespace in force in the output (kept on each output scope), and the tunnel
/// parameters a template invocation inherits (from the nearest invocation that inherited). The
/// results are those of the walks they replace.
/// </summary>
public sealed class PerLevelWalkTests
{
    private static async Task<string> RunAsync(string stylesheet, string source)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(source);
    }

    private const string NamespaceSource =
        """<r xmlns="urn:d" xmlns:p="urn:p1"><p:a xmlns:q="urn:q"><b xmlns=""><c xmlns:p="urn:p2"><p:d/><q:e xmlns:z="urn:p1"/></c></b></p:a><e xmlns="urn:e2"><f/><g xmlns=""/></e></r>""";

    [Fact]
    public async Task A_copied_element_declares_its_source_elements_namespaces_in_the_same_order()
    {
        // Each element copied on its own, at the top level of the output: the copy declares every
        // namespace in scope for the source element. First deepest first, so that an element's
        // namespaces are computed with those of all elements above it; then in document order,
        // which finds them kept.
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <out>
                  <xsl:for-each select="reverse(//*)"><xsl:copy/></xsl:for-each>
                  <xsl:for-each select="//*"><xsl:copy/></xsl:for-each>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """, NamespaceSource);

        result.Should().Be(ExpectedCopies);
    }

    // The output before #639, byte for byte.
    private const string ExpectedCopies =
        """
        <out><g xmlns:p="urn:p1"/><f xmlns="urn:e2" xmlns:p="urn:p1"/><e xmlns="urn:e2" xmlns:p="urn:p1"/><q:e xmlns:z="urn:p1" xmlns:p="urn:p2" xmlns:q="urn:q"/><p:d xmlns:p="urn:p2" xmlns:q="urn:q"/><c xmlns:p="urn:p2" xmlns:q="urn:q"/><b xmlns:q="urn:q" xmlns:p="urn:p1"/><p:a xmlns:q="urn:q" xmlns:p="urn:p1" xmlns="urn:d"/><r xmlns:p="urn:p1" xmlns="urn:d"/><r xmlns:p="urn:p1" xmlns="urn:d"/><p:a xmlns:q="urn:q" xmlns:p="urn:p1" xmlns="urn:d"/><b xmlns:q="urn:q" xmlns:p="urn:p1"/><c xmlns:p="urn:p2" xmlns:q="urn:q"/><p:d xmlns:p="urn:p2" xmlns:q="urn:q"/><q:e xmlns:z="urn:p1" xmlns:p="urn:p2" xmlns:q="urn:q"/><e xmlns="urn:e2" xmlns:p="urn:p1"/><f xmlns="urn:e2" xmlns:p="urn:p1"/><g xmlns:p="urn:p1"/></out>
        """;

    [Fact]
    public async Task A_copy_has_its_source_elements_base_URI_through_3000_relative_xml_base_levels()
    {
        const int depth = 3_000;
        var source = "<r xml:base='http://example.org/'>" + string.Concat(Enumerable.Repeat("<e xml:base='d/'>", depth))
            + string.Concat(Enumerable.Repeat("</e>", depth)) + "</r>";

        // The deepest element is copied first: its base URI needs all 3,000 levels above it.
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="deepest"><xsl:for-each select="(//e)[last()]"><xsl:copy/></xsl:for-each></xsl:variable>
                <xsl:variable name="level500"><xsl:for-each select="(//e)[500]"><xsl:copy/></xsl:for-each></xsl:variable>
                <xsl:value-of select="base-uri($deepest/e), base-uri($level500/e)" separator="|"/>
              </xsl:template>
            </xsl:stylesheet>
            """, source);

        result.Should().Be("http://example.org/" + string.Concat(Enumerable.Repeat("d/", depth))
            + "|http://example.org/" + string.Concat(Enumerable.Repeat("d/", 500)));
    }

    [Fact]
    public async Task The_default_namespace_in_force_follows_declarations_and_undeclarations_60_levels_deep()
    {
        // Levels alternate: an element that declares a default namespace, one that undeclares it,
        // one that declares nothing. At each level a no-namespace element is constructed and a
        // no-namespace source element copied: each needs xmlns="" exactly where a default is in force.
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/"><xsl:call-template name="level"><xsl:with-param name="i" select="0"/></xsl:call-template></xsl:template>
              <xsl:template name="level">
                <xsl:param name="i"/>
                <xsl:if test="$i lt 60">
                  <xsl:choose>
                    <xsl:when test="$i mod 3 = 0"><d xmlns="urn:d"><xsl:call-template name="content"><xsl:with-param name="i" select="$i"/></xsl:call-template></d></xsl:when>
                    <xsl:when test="$i mod 3 = 1"><u xmlns=""><xsl:call-template name="content"><xsl:with-param name="i" select="$i"/></xsl:call-template></u></xsl:when>
                    <xsl:otherwise><xsl:element name="k" namespace="urn:k"><xsl:call-template name="content"><xsl:with-param name="i" select="$i"/></xsl:call-template></xsl:element></xsl:otherwise>
                  </xsl:choose>
                </xsl:if>
              </xsl:template>
              <xsl:template name="content">
                <xsl:param name="i"/>
                <xsl:element name="n"/>
                <xsl:copy-of select="/r/s"/>
                <xsl:call-template name="level"><xsl:with-param name="i" select="$i + 1"/></xsl:call-template>
              </xsl:template>
            </xsl:stylesheet>
            """, "<r><s/></r>");

        result.Should().Be(ExpectedDefaults);
    }

    // The output before #639, byte for byte: at each level the element, then the two no-namespace
    // elements with xmlns="" (also, redundantly, inside an element that undeclared the default).
    private static readonly string ExpectedDefaults = Defaults(60);

    private static string Defaults(int levels)
    {
        var open = new System.Text.StringBuilder();
        var close = new System.Text.StringBuilder();
        for (var i = 0; i < levels; i++)
        {
            var (start, end) = (i % 3) switch
            {
                0 => ("<d xmlns=\"urn:d\">", "</d>"),
                1 => ("<u xmlns=\"\">", "</u>"),
                _ => ("<k xmlns=\"urn:k\">", "</k>"),
            };
            open.Append(start).Append("<n xmlns=\"\"/><s xmlns=\"\"/>");
            close.Insert(0, end);
        }
        return open.Append(close).ToString();
    }

    [Fact]
    public async Task Tunnel_parameters_reach_every_level_of_a_deep_recursion_and_stop_at_a_function()
    {
        // A chain of 60 elements, then a sibling. Every 10th level from 5 passes a new value of the
        // tunnel parameter; level 20 goes on inside a stylesheet function, where no tunnel parameter
        // passes; the sibling after the chain sees the value its parent passed, not the chain's.
        var source = "<r>" + string.Concat(Enumerable.Repeat("<a>", 60)) + string.Concat(Enumerable.Repeat("</a>", 60)) + "<b/></r>";
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:apply-templates select="r"><xsl:with-param name="t" select="'root'" tunnel="yes"/></xsl:apply-templates></xsl:template>
              <xsl:template match="r"><xsl:apply-templates/></xsl:template>
              <xsl:template match="a">
                <xsl:param name="t" tunnel="yes" select="'none'"/>
                <xsl:variable name="d" select="count(ancestor::a)"/>
                <xsl:value-of select="concat($d, '=', $t, ' ')"/>
                <xsl:choose>
                  <xsl:when test="$d mod 10 = 5"><xsl:apply-templates><xsl:with-param name="t" select="'L' || $d" tunnel="yes"/></xsl:apply-templates></xsl:when>
                  <xsl:when test="$d = 20"><xsl:sequence select="f:on(.)"/></xsl:when>
                  <xsl:otherwise><xsl:apply-templates/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
              <xsl:template match="b"><xsl:param name="t" tunnel="yes" select="'none'"/><xsl:value-of select="'b=' || $t"/></xsl:template>
              <xsl:function name="f:on"><xsl:param name="n"/><xsl:apply-templates select="$n/*"/></xsl:function>
            </xsl:stylesheet>
            """, source);

        var expected = new System.Text.StringBuilder();
        for (var d = 0; d < 60; d++)
        {
            var t = d <= 5 ? "root" : d <= 20 ? $"L{(d - 6) / 10 * 10 + 5}" : d <= 25 ? "none" : $"L{(d - 6) / 10 * 10 + 5}";
            expected.Append(System.Globalization.CultureInfo.InvariantCulture, $"{d}={t} ");
        }
        expected.Append("b=root");
        result.Should().Be(expected.ToString());
    }

    [Fact]
    public async Task A_tunnel_parameter_no_caller_passed_takes_its_default_at_every_level_above_the_one_that_passes_it()
    {
        // 60 levels, each declaring tunnel parameters t (passed at the top) and u (passed by level
        // 40 only). A lookup of u above level 40 finds nothing, and ends at the nearest invocation.
        var source = "<r>" + string.Concat(Enumerable.Repeat("<a>", 60)) + string.Concat(Enumerable.Repeat("</a>", 60)) + "</r>";
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:apply-templates select="r"><xsl:with-param name="t" select="'top'" tunnel="yes"/></xsl:apply-templates></xsl:template>
              <xsl:template match="r"><xsl:apply-templates/></xsl:template>
              <xsl:template match="a">
                <xsl:param name="t" tunnel="yes" select="'no-t'"/>
                <xsl:param name="u" tunnel="yes" select="'no-u'"/>
                <xsl:variable name="d" select="count(ancestor::a)"/>
                <xsl:value-of select="concat($d, ':', $t, ':', $u, ' ')"/>
                <xsl:choose>
                  <xsl:when test="$d = 40"><xsl:apply-templates><xsl:with-param name="u" select="'u40'" tunnel="yes"/></xsl:apply-templates></xsl:when>
                  <xsl:otherwise><xsl:apply-templates/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:stylesheet>
            """, source);

        result.Should().Be(string.Concat(Enumerable.Range(0, 60).Select(d => $"{d}:top:{(d <= 40 ? "no-u" : "u40")} ")));
    }

    [Fact]
    public async Task Tunnel_parameters_pass_through_a_tail_called_template_to_a_deep_recursion()
    {
        // The named template calls itself as its last instruction (a tail call, which does not
        // nest); at the end it applies templates down a chain of 40 elements, which see the tunnel
        // parameter the first call received, and one level's new value below that level.
        var source = "<r>" + string.Concat(Enumerable.Repeat("<a>", 40)) + string.Concat(Enumerable.Repeat("</a>", 40)) + "</r>";
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:call-template name="loop"><xsl:with-param name="i" select="100"/><xsl:with-param name="t" select="'start'" tunnel="yes"/></xsl:call-template></xsl:template>
              <xsl:template name="loop">
                <xsl:param name="i"/>
                <xsl:choose>
                  <xsl:when test="$i gt 0"><xsl:call-template name="loop"><xsl:with-param name="i" select="$i - 1"/></xsl:call-template></xsl:when>
                  <xsl:otherwise><xsl:apply-templates select="/r/a"/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
              <xsl:template match="a">
                <xsl:param name="t" tunnel="yes" select="'none'"/>
                <xsl:variable name="d" select="count(ancestor::a)"/>
                <xsl:value-of select="$t || ' '"/>
                <xsl:choose>
                  <xsl:when test="$d = 30"><xsl:apply-templates><xsl:with-param name="t" select="'deep'" tunnel="yes"/></xsl:apply-templates></xsl:when>
                  <xsl:otherwise><xsl:apply-templates/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:stylesheet>
            """, source);

        result.Should().Be(string.Concat(Enumerable.Repeat("start ", 31)) + string.Concat(Enumerable.Repeat("deep ", 9)));
    }
}
