using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Defects found by running the ISO Schematron skeleton (the three-step compile NEMSIS and the state EMS
/// agencies publish against) through this engine and comparing every compiled validator and every SVRL
/// report with Saxon-HE 12.10 (phx-schematron conformance harness). Each case is a reduction of a construct
/// the real state schematrons use; the expected values are Saxon's.
/// </summary>
public sealed class SchematronSkeletonConformanceTests
{
    private const string Source = """<r xmlns="urn:x"><a/><b>S29</b><c>A48.3</c><t><t1>2026-01-01T10:05:00-05:00</t1></t><d>2026-01-01T10:02:00-05:00</d></r>""";

    private static async Task<string> RunAsync(string stylesheet, string source = Source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    private static string Probe(string select) => $$"""
        <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:m="urn:x">
          <xsl:output method="text"/>
          <xsl:template match="/"><xsl:for-each select="m:r/m:a"><xsl:value-of select="{{select}}"/></xsl:for-each></xsl:template>
        </xsl:stylesheet>
        """;

    // ---- Namespaced name tests inside some/every ----------------------------------------------------------
    // The stylesheet-namespace walkers had no QuantifiedExpression case, so a prefixed or EQName name test in
    // a binding or satisfies clause was never bound to its URI and matched only no-namespace nodes: `some` was
    // false and `every` true for any data. Seen as wrong accept/reject decisions in the AL.EMS and PA.EMS
    // state schematrons (e.g. every $element in nem:eVitals.VitalGroup satisfies …).

    [Theory]
    [InlineData("some $e in ../m:b satisfies $e = 'S29'", "true")]
    [InlineData("some $e in ../(m:b, m:c) satisfies matches($e, '^(S|T(0\\d|1[0-4]))')", "true")]
    [InlineData("some $e in ../Q{urn:x}b satisfies true()", "true")]
    [InlineData("some $e in //m:b satisfies true()", "true")]
    [InlineData("some $x in (1) satisfies exists(../m:b)", "true")]
    [InlineData("some $x in .. satisfies exists($x/m:b)", "true")]
    [InlineData("every $e in ../m:b satisfies false()", "false")]
    [InlineData("every $e in (ancestor::m:r/m:t/(m:t1))[. != ''] satisfies xs:dateTime($e) &lt;= xs:dateTime(../m:d)", "false")]
    public async Task Quantifier_NamespacedNameTest_SelectsTheNamespacedNodes(string select, string expected)
    {
        var xs = Probe(select).Replace("xmlns:m=\"urn:x\"", "xmlns:m=\"urn:x\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"");
        (await RunAsync(xs)).Should().Be(expected, "`{0}` must bind m: inside the quantifier (Saxon: {1})", select, expected);
    }

    // ---- Lexical variable scope across template invocations -----------------------------------------------
    // A local xsl:variable is visible only to its following siblings and their descendants (XSLT 3.0 §9.9).
    // Lookups searched every scope on the stack, so a template invoked by apply-templates, call-template or
    // next-match saw the caller's local instead of the global of the same name. The ISO skeleton compiles each
    // Schematron rule to a template that declares its sch:let variables and then applies templates to the
    // children, so a descendant rule read an ancestor rule's value for a schema-level default.

    private const string ScopeStylesheet = """
        <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:m="urn:x" xmlns:f="urn:f">
          <xsl:output method="text"/>
          <xsl:variable name="v" select="'GLOBAL'"/>
          <xsl:function name="f:read"><xsl:sequence select="$v"/></xsl:function>
          <xsl:template match="/">
            <xsl:apply-templates select="m:r" mode="apply"/>|<xsl:apply-templates select="m:r" mode="call"/>|<xsl:apply-templates select="m:r" mode="next"/>|<xsl:apply-templates select="m:r" mode="function"/>
          </xsl:template>

          <xsl:template match="m:r" mode="apply"><xsl:variable name="v" select="'LOCAL'"/><xsl:apply-templates select="m:a" mode="apply"/></xsl:template>
          <xsl:template match="m:a" mode="apply"><xsl:value-of select="$v"/></xsl:template>

          <xsl:template match="m:r" mode="call"><xsl:variable name="v" select="'LOCAL'"/><xsl:call-template name="callee"/></xsl:template>
          <xsl:template name="callee"><xsl:value-of select="$v"/></xsl:template>

          <xsl:template match="m:r" mode="next" priority="2"><xsl:variable name="v" select="'LOCAL'"/><xsl:next-match/></xsl:template>
          <xsl:template match="m:r" mode="next" priority="1"><xsl:value-of select="$v"/></xsl:template>

          <xsl:template match="m:r" mode="function"><xsl:variable name="v" select="'LOCAL'"/><xsl:value-of select="f:read()"/></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task InvokedTemplatesAndFunctions_SeeTheGlobal_NotTheCallersLocal()
    {
        var parts = (await RunAsync(ScopeStylesheet)).Split('|').Select(p => p.Trim()).ToArray();
        parts.Should().Equal(["GLOBAL", "GLOBAL", "GLOBAL", "GLOBAL"],
            "apply-templates, call-template, next-match and a stylesheet function each resolve $v lexically (Saxon: GLOBAL×4)");
    }

    [Fact]
    public async Task SkeletonRuleShape_DescendantRuleReadsTheSchemaLevelDefault()
    {
        // The compiled shape of <sch:let name="limit" value="10"/> at schema level, a rule on m:r overriding
        // it locally, and a rule on m:a reading $limit.
        const string ss = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:m="urn:x">
              <xsl:output method="text"/>
              <xsl:variable name="limit" select="10"/>
              <xsl:template match="/"><xsl:apply-templates select="/" mode="M0"/></xsl:template>
              <xsl:template match="m:r" priority="1001" mode="M0">
                <xsl:variable name="limit" select="0"/>
                <xsl:apply-templates select="*" mode="M0"/>
              </xsl:template>
              <xsl:template match="m:a" priority="1000" mode="M0"><xsl:value-of select="$limit"/></xsl:template>
              <xsl:template match="text()" priority="-1" mode="M0"/>
              <xsl:template match="@*|node()" priority="-2" mode="M0"><xsl:apply-templates select="*" mode="M0"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("10");
    }

    [Theory]
    [InlineData("<xsl:choose><xsl:when test=\"$limit = 10\">10</xsl:when><xsl:otherwise>leaked</xsl:otherwise></xsl:choose>")]
    [InlineData("<xsl:if test=\"$limit = 10\">10</xsl:if><xsl:if test=\"not($limit = 10)\">leaked</xsl:if>")]
    [InlineData("<xsl:if test=\"number(.) &lt;= $limit\">10</xsl:if><xsl:if test=\"not(number(.) &lt;= $limit)\">leaked</xsl:if>")]
    public async Task SkeletonRuleShape_ConditionsReadTheSchemaLevelDefault(string body)
    {
        // The skeleton evaluates each assert as <xsl:choose><xsl:when test="…"/>. Reading $limit through a
        // condition must resolve lexically too — xsl:value-of did while xsl:when still saw the ancestor
        // rule's local (phx-schematron micro-suite case 27, a false reject).
        var ss = $$"""
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:m="urn:x">
              <xsl:output method="text"/>
              <xsl:variable name="limit" select="10"/>
              <xsl:template match="/"><xsl:apply-templates select="/" mode="M0"/></xsl:template>
              <xsl:template match="m:r" priority="1001" mode="M0">
                <xsl:variable name="limit" select="0"/>
                <xsl:apply-templates select="*" mode="M0"/>
              </xsl:template>
              <xsl:template match="m:a" priority="1000" mode="M0">{{body}}</xsl:template>
              <xsl:template match="text()" priority="-1" mode="M0"/>
              <xsl:template match="@*|node()" priority="-2" mode="M0"><xsl:apply-templates select="*" mode="M0"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, """<r xmlns="urn:x"><a>5</a></r>""")).Should().Be("10");
    }

    [Fact]
    public async Task WithParamForwardedByBuiltInRules_IsEvaluatedWhereItWasWritten()
    {
        // Reduction of W3C insn/merge merge-096: a function parameter (and, second, a template local) passed by
        // with-param through on-no-match="shallow-copy" built-in rules to a template several levels down. The
        // built-in rules forwarded the unevaluated select, which lexical scope then could not resolve.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:mode name="construct" on-no-match="shallow-copy"/>
              <xsl:template match="/"><xsl:value-of select="f:run(/r)"/>|<xsl:call-template name="local"/></xsl:template>
              <xsl:function name="f:run" as="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema">
                <xsl:param name="nodes"/>
                <xsl:variable name="out"><xsl:apply-templates select="root($nodes)" mode="construct"><xsl:with-param name="nodes" select="$nodes"/></xsl:apply-templates></xsl:variable>
                <xsl:sequence select="string($out)"/>
              </xsl:function>
              <xsl:template name="local">
                <xsl:variable name="v" select="/r"/>
                <xsl:apply-templates select="/" mode="construct"><xsl:with-param name="nodes" select="$v"/></xsl:apply-templates>
              </xsl:template>
              <xsl:template match="a" mode="construct"><xsl:param name="nodes"/>[<xsl:value-of select="count($nodes)"/>]</xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, "<r><a/></r>")).Should().Be("[1]|[1]");
    }

    // ---- extension-element-prefixes on xsl:stylesheet ----------------------------------------------------
    // An extension namespace is not copied to literal result elements (XSLT 3.0 §11.1.3). The stylesheet-level
    // declaration was ignored, so every validator compiled from iso_schematron_skeleton_for_saxon.xsl carried
    // xmlns:exsl — the only difference between this engine's compiled validators and Saxon's.

    [Fact]
    public async Task StylesheetLevelExtensionElementPrefixes_AreExcludedFromLiteralResultElements()
    {
        const string ss = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:exsl="http://exslt.org/common" xmlns:ex="urn:excluded" xmlns:keep="urn:keep"
                extension-element-prefixes="exsl" exclude-result-prefixes="ex">
              <xsl:template match="/"><out/></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().NotContain("exslt.org", "exsl is an extension namespace declared on xsl:stylesheet");
        r.Should().NotContain("urn:excluded");
        r.Should().Contain("urn:keep");
    }

    [Fact]
    public async Task SkeletonNamespaceAliasShape_GeneratedStylesheetHasNoExtensionNamespace()
    {
        const string ss = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:axsl="http://www.w3.org/1999/XSL/TransformAlias"
                xmlns:exsl="http://exslt.org/common" xmlns:iso="http://purl.oclc.org/dsdl/schematron"
                extension-element-prefixes="exsl">
              <xsl:namespace-alias stylesheet-prefix="axsl" result-prefix="xsl"/>
              <xsl:template match="/"><axsl:stylesheet version="2.0"><axsl:template match="/"/></axsl:stylesheet></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().NotContain("exslt.org");
        r.Should().Contain("http://purl.oclc.org/dsdl/schematron", "ordinary stylesheet namespaces are still copied (Saxon keeps xmlns:iso)");
    }
}
