using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Stylesheets that XSLT 3.0 §19.8 finds guaranteed-streamable, and that read their whole input
/// into memory with no message (xslt#582): a rule that inspects <c>root()</c> (§19.8.9.18: a climb
/// to the document node, motionless when inspected), and a rule for the document node that reads
/// its URI. They now stream, with the result they give without streaming. And an expression in an
/// attribute of a literal result element was not examined by the check that sends a rule which
/// reads outside its match to the tree: <c>&lt;b v="{last()}"/&gt;</c> streamed and gave 1.
/// </summary>
public sealed class StreamedRootAndDocumentReadTests
{
    private const string SourceUri = "file:///data/in/books.xml";

    private const string Books = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?xml-model href="price.sch" type="application/xml"?>
        <books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book><book price="7"><title>C</title></book></books>
        """;

    // Each streams, and gives what the tree gives.
    public static TheoryData<string> Streamed => new()
    {
        // root() whose node is only inspected.
        """<xsl:template match="book"><b><xsl:if test="exists(root())">y</xsl:if></b></xsl:template>""",
        """<xsl:template match="book"><b><xsl:attribute name="d" select="base-uri(root())"/></b></xsl:template>""",
        """<xsl:template match="book"><b><xsl:if test="(document-uri(.), base-uri(root()))[1]"><xsl:attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/></xsl:if></b></xsl:template>""",
        """<xsl:template match="book"><b><xsl:value-of select="root() is .., root() is root(.)"/></b></xsl:template>""",
        // The rule for "/" reads the document node's URI: in an attribute, an xsl:attribute, a catch.
        """<xsl:template match="/"><r doc="{document-uri(.)}" base="{base-uri(.)}"><xsl:apply-templates/></r></xsl:template><xsl:template match="book"><b/></xsl:template>""",
        """<xsl:template match="/"><r><xsl:attribute name="doc" select="document-uri(.)"/><xsl:apply-templates/></r></xsl:template><xsl:template match="book"><b/></xsl:template>""",
        """<xsl:template match="/"><out><xsl:try><xsl:apply-templates/><xsl:catch><e doc="{document-uri(.)}"/></xsl:catch></xsl:try></out></xsl:template><xsl:template match="book"><b/></xsl:template>""",
        // A loop of the rule for "/" over a path of child steps reads the URI of its items.
        """<xsl:template match="/"><out><xsl:iterate select="books/book"><b base="{base-uri(.)}"/></xsl:iterate></out></xsl:template>""",
        """<xsl:template match="/"><out><xsl:for-each-group select="books/book" group-adjacent="@price > 100"><g base="{base-uri(current-group()[1])}"/></xsl:for-each-group></out></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(Streamed))]
    public async Task A_guaranteed_streamable_read_of_the_root_or_the_document_streams(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    // Each reads what has passed or is still to come: the tree answers, as before.
    public static TheoryData<string> NotStreamed => new()
    {
        // In an attribute of a literal result element: it streamed, and gave 1 for every book.
        """<xsl:template match="book"><b v="{last()}"/></xsl:template>""",
        """<xsl:template match="book"><b v="{count(root()/*)}"/></xsl:template>""",
        // root() whose value is read, not inspected.
        """<xsl:template match="book"><b><xsl:value-of select="string(root())"/></b></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(NotStreamed))]
    public async Task A_read_outside_the_match_is_answered_from_a_tree(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, disableStreaming: false, streamingFallback: true);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeFalse();
    }

    [Fact]
    public async Task The_xslt_582_validator_streams_with_the_report_it_gives_without_streaming()
    {
        // Martin Honnen's SchXslt2 1.11.4 validator of xslt#582, as given: its rules read
        // base-uri(root()), and its catch reads the document node.
        var tree = await RunAsync(Validator, disableStreaming: true);
        var streamed = await RunAsync(Validator, disableStreaming: false);

        tree.Output.Should().Contain($"""<svrl:fired-rule context="book" document="{SourceUri}"/>""");
        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    private static string Stylesheet(string rules) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
          <xsl:mode name="v" streamable="yes" on-no-match="shallow-skip"/>
          {{rules}}
        </xsl:stylesheet>
        """;

    private static async Task<(string Output, bool Streamed)> RunAsync(string stylesheet, bool disableStreaming,
        bool streamingFallback = false)
    {
        var transformer = new XsltTransformer { DisableStreaming = disableStreaming, StreamingFallback = streamingFallback };
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetSourceDocumentUri(new Uri(SourceUri));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Books));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed);
    }

    // The stylesheet of xslt#582 as Martin Honnen gave it (SchXslt2 1.11.4, schxslt:streamable).
    private const string Validator = """
        <?xml version="1.0" encoding="UTF-8"?>
        <stylesheet xmlns="http://www.w3.org/1999/XSL/Transform"
                    xmlns:svrl="http://purl.oclc.org/dsdl/svrl"
                    xmlns:map="http://www.w3.org/2005/xpath-functions/map"
                    xmlns:schxslt="http://dmaus.name/ns/2023/schxslt"
                    version="3.0"
                    expand-text="false"
                    exclude-result-prefixes="#all">
           <variable name="Q{http://dmaus.name/ns/2023/schxslt}phase"
                     as="Q{http://www.w3.org/2001/XMLSchema}string"
                     select="'#ALL'"/>
           <output indent="true"/>
           <mode use-accumulators="" streamable="true"/>
           <mode name="Q{http://dmaus.name/ns/2023/schxslt}validate"
                 streamable="true"
                 use-accumulators=""
                 on-no-match="shallow-skip"/>
           <mode name="group.d6e2"
                 on-no-match="shallow-skip"
                 streamable="true"
                 use-accumulators=""/>
           <template match="* | root()" mode="group.d6e2" priority="-10">
              <apply-templates select="@*" mode="#current"/>
              <apply-templates select="node()" mode="#current"/>
           </template>
           <template match="book" mode="group.d6e2" priority="0">
              <param name="Q{http://dmaus.name/ns/2023/schxslt}pattern"
                     as="Q{http://www.w3.org/2001/XMLSchema}string*"
                     select="()"/>
              <choose>
                 <when test="'d6e2' = $Q{http://dmaus.name/ns/2023/schxslt}pattern">
                    <svrl:suppressed-rule context="book">
                       <if test="(document-uri(.), base-uri(root()))[1]">
                          <attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/>
                       </if>
                    </svrl:suppressed-rule>
                    <next-match>
                       <with-param name="Q{http://dmaus.name/ns/2023/schxslt}pattern"
                                   as="Q{http://www.w3.org/2001/XMLSchema}string*"
                                   select="$Q{http://dmaus.name/ns/2023/schxslt}pattern"/>
                    </next-match>
                 </when>
                 <otherwise>
                    <svrl:fired-rule context="book">
                       <if test="(document-uri(.), base-uri(root()))[1]">
                          <attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/>
                       </if>
                    </svrl:fired-rule>
                    <variable name="Q{http://dmaus.name/ns/2023/schxslt}severity"
                              as="map(Q{http://www.w3.org/2001/XMLSchema}string, Q{http://www.w3.org/2001/XMLSchema}integer)">
                       <map>
                          <map-entry key="'d6e6'">
                             <variable name="severity"
                                       as="Q{http://www.w3.org/2001/XMLSchema}string"
                                       select="'fatal'"/>
                             <sequence select="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity($severity)"/>
                          </map-entry>
                          <map-entry key="'d6e9'">
                             <variable name="severity"
                                       as="Q{http://www.w3.org/2001/XMLSchema}string"
                                       select="'fatal'"/>
                             <sequence select="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity($severity)"/>
                          </map-entry>
                       </map>
                    </variable>
                    <for-each select=".">
                       <choose>
                          <when test="map:get($schxslt:severity, 'd6e6') ge Q{http://dmaus.name/ns/2023/schxslt}numeric-severity('info')">
                             <if test="not(@price &gt; 10)">
                                <variable name="failed-assert" as="element(svrl:failed-assert)">
                                   <svrl:failed-assert test="@price &gt; 10">
                                      <svrl:text>The book price is too small</svrl:text>
                                   </svrl:failed-assert>
                                </variable>
                                <sequence select="$failed-assert"/>
                             </if>
                          </when>
                          <otherwise>
                             <svrl:skipped-assert severityThreshold="info" test="@price &gt; 10"/>
                          </otherwise>
                       </choose>
                       <choose>
                          <when test="map:get($schxslt:severity, 'd6e9') ge Q{http://dmaus.name/ns/2023/schxslt}numeric-severity('info')">
                             <if test="@price &gt; 1000">
                                <variable name="successful-report" as="element(svrl:successful-report)">
                                   <svrl:successful-report test="@price &gt; 1000">
                                      <svrl:text>The book price is too big</svrl:text>
                                   </svrl:successful-report>
                                </variable>
                                <sequence select="$successful-report"/>
                             </if>
                          </when>
                          <otherwise>
                             <svrl:skipped-report severityThreshold="info" test="@price &gt; 1000"/>
                          </otherwise>
                       </choose>
                    </for-each>
                    <next-match>
                       <with-param name="Q{http://dmaus.name/ns/2023/schxslt}pattern"
                                   as="Q{http://www.w3.org/2001/XMLSchema}string*"
                                   select="('d6e2', $Q{http://dmaus.name/ns/2023/schxslt}pattern)"/>
                    </next-match>
                 </otherwise>
              </choose>
           </template>
           <template match="root()" as="element(svrl:schematron-output)">
              <svrl:schematron-output phase="#ALL"><!--SchXslt2 1.11.4-->
                 <try>
                    <apply-templates select="." mode="Q{http://dmaus.name/ns/2023/schxslt}validate"/>
                    <catch>
                       <svrl:error code="{$Q{http://www.w3.org/2005/xqt-errors}code}">
                          <if test="(document-uri(.), base-uri(root()))[1]">
                             <attribute name="document" select="(document-uri(.), base-uri(root()))[1]"/>
                          </if>
                          <if test="$Q{http://www.w3.org/2005/xqt-errors}line-number">
                             <attribute name="line-number"
                                        select="$Q{http://www.w3.org/2005/xqt-errors}line-number"/>
                          </if>
                          <if test="$Q{http://www.w3.org/2005/xqt-errors}column-number">
                             <attribute name="column-number"
                                        select="$Q{http://www.w3.org/2005/xqt-errors}column-number"/>
                          </if>
                          <if test="$Q{http://www.w3.org/2005/xqt-errors}description">
                             <value-of select="$Q{http://www.w3.org/2005/xqt-errors}description"/>
                          </if>
                       </svrl:error>
                       <variable name="message"
                                 as="Q{http://www.w3.org/2001/XMLSchema}string+"
                                 expand-text="yes">
                            Running the ISO Schematron validation failed with a dynamic error.
                            Error code: {$Q{http://www.w3.org/2005/xqt-errors}code} Reason: {$Q{http://www.w3.org/2005/xqt-errors}description}
                          </variable>
                       <message terminate="yes"
                                error-code="Q{{http://dmaus.name/ns/2023/schxslt}}ValidationError">
                          <text/>
                          <value-of select="normalize-space(string-join($message))"/>
                       </message>
                    </catch>
                 </try>
              </svrl:schematron-output>
           </template>
           <template match="root()"
                     as="element()*"
                     mode="Q{http://dmaus.name/ns/2023/schxslt}validate">
              <svrl:active-pattern/>
              <apply-templates select="." mode="group.d6e2"/>
           </template>
           <function name="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity"
                     as="Q{http://www.w3.org/2001/XMLSchema}integer">
              <param name="severity" as="Q{http://www.w3.org/2001/XMLSchema}string"/>
              <sequence select="(index-of(('info', 'warning', 'error', 'fatal'), $severity), 4)[1]"/>
           </function>
        </stylesheet>
        """;
}
