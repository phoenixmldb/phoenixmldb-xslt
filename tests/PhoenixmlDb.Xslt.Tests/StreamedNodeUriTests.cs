using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed run gives what the same stylesheet gives without streaming (xslt#652, xslt#582).
/// <list type="bullet">
/// <item>base-uri() and document-uri() of a streamed node were empty: the document nodes the
/// streaming pass builds had no URI, and the root of a buffered subtree had no parent and no base
/// URI. Without streaming they give the URI of the input, resolved with each xml:base.</item>
/// <item>An accumulator rule match="/" never ran in a streamed run of a stylesheet with no rule
/// for the document node: every node read the initial value.</item>
/// <item>A streamed comment or processing instruction had no parent, so it was the root of its own
/// tree: its parent, ancestors and base URI were empty, match="root()" matched it, and before the
/// document element its rule read the rest of the input as its children.</item>
/// </list>
/// </summary>
public sealed class StreamedNodeUriTests
{
    private const string SourceUri = "file:///data/in/books.xml";

    private const string Books = """
        <?xml version="1.0" encoding="UTF-8"?>
        <books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book></books>
        """;

    private const string BooksWithXmlBase = """
        <books xml:base="dir/"><shelf xml:base="s1/"><book price="1" xml:base="b/"><title>t</title></book><book price="2"><title>u</title></book></shelf></books>
        """;

    // A rule of a streamable mode, and the path that answers it: each must stream and give what
    // the tree gives.
    public static TheoryData<string> Rules => new()
    {
        // Read on the live reader.
        """<xsl:template match="book"><b base="{base-uri(.)}" a="{base-uri(@price)}"/></xsl:template>""",
        """<xsl:template match="book"><b doc="{document-uri(root())}" base="{base-uri(root())}"/></xsl:template>""",
        """<xsl:template match="title/text()"><t base="{base-uri(.)}"/></xsl:template>""",
        // The body reads the element's children: the element is read whole, with no parent.
        """<xsl:template match="book"><b base="{base-uri(.)}"><xsl:for-each select="title"><t base="{base-uri(.)}"/></xsl:for-each></b></xsl:template>""",
        // A rule for every element that applies templates to its children, under a rule for "/".
        """<xsl:template match="/"><out><xsl:apply-templates select="." mode="v"/></out></xsl:template><xsl:template match="* | root()" mode="v" priority="-10"><xsl:apply-templates select="node()" mode="v"/></xsl:template><xsl:template match="book" mode="v"><b base="{base-uri(.)}" up="{string-join(ancestor::node() ! (if (. instance of document-node()) then '#doc' else name()), '/')}"/></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task Base_uri_of_a_streamed_node_is_the_uri_of_the_input(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, Books, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Books, disableStreaming: false);

        tree.Output.Should().Contain(SourceUri);
        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    // The paths above below elements with xml:base, relative so that the URI of the input takes
    // part, and a streamed grouping.
    public static TheoryData<string> XmlBaseRules => new()
    {
        """<xsl:template match="book"><b base="{base-uri(.)}" a="{base-uri(@price)}"/></xsl:template>""",
        """<xsl:template match="title/text()"><t base="{base-uri(.)}"/></xsl:template>""",
        """<xsl:template match="book"><b base="{base-uri(.)}"><xsl:for-each select="title"><t base="{base-uri(.)}"/></xsl:for-each></b></xsl:template>""",
        """<xsl:template match="/"><out><xsl:apply-templates select="." mode="v"/></out></xsl:template><xsl:template match="* | root()" mode="v" priority="-10"><xsl:apply-templates select="node()" mode="v"/></xsl:template><xsl:template match="book" mode="v"><b base="{base-uri(.)}"/></xsl:template>""",
        """<xsl:template match="shelf"><xsl:for-each-group select="book" group-adjacent="@price > 0"><g base="{base-uri(current-group()[1])}" second="{base-uri(current-group()[2])}"/></xsl:for-each-group></xsl:template>""",
        """<xsl:template match="books"><xsl:for-each select="shelf"><xsl:for-each-group select="book" group-adjacent="@price > 0"><g base="{base-uri(current-group()[1])}" second="{base-uri(current-group()[2])}"/></xsl:for-each-group></xsl:for-each></xsl:template>""",
        // A child that a streamed xsl:for-each or xsl:iterate reads whole is linked to its parent.
        """<xsl:template match="books"><xsl:for-each select="shelf"><s parent="{name(..)}" base="{base-uri(.)}"/></xsl:for-each></xsl:template>""",
        """<xsl:template match="shelf"><xsl:iterate select="book"><b parent="{name(..)}" base="{base-uri(.)}"/></xsl:iterate></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(XmlBaseRules))]
    public async Task Xml_base_of_the_ancestors_is_resolved_as_in_a_tree(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, BooksWithXmlBase, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, BooksWithXmlBase, disableStreaming: false);

        tree.Output.Should().Contain("file:///data/in/dir/s1/");
        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    // An element rule that groups its children: each member is read whole, with no parent.
    public static TheoryData<string> DescentRules => new()
    {
        """<xsl:template match="books"><xsl:for-each-group select="book" group-starting-with="book[@price > 100]"><g base="{base-uri(current-group()[1])}"/></xsl:for-each-group></xsl:template>""",
        """<xsl:template match="books"><xsl:for-each-group select="book" group-adjacent="@price > 100"><g base="{base-uri(current-group()[1])}"/></xsl:for-each-group></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(DescentRules))]
    public async Task Base_uri_of_a_group_member_is_the_uri_of_the_input(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, Books, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Books, disableStreaming: false);

        tree.Output.Should().Contain(SourceUri);
        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    [Theory]
    [InlineData("""<xsl:template match="book"><b base="{base-uri(.)}" doc="{document-uri(root())}"/></xsl:template>""")]
    [InlineData("""<xsl:template match="/"><out><xsl:apply-templates/></out></xsl:template><xsl:template match="book"><b base="{base-uri(.)}" doc="{document-uri(root())}"/></xsl:template>""")]
    public async Task The_engine_takes_the_uri_from_its_options_when_the_reader_has_none(string rules)
    {
        // XsltTransformEngine.TransformAsync(XmlReader, TextWriter, XsltTransformOptions): a reader
        // created with no base URI, and the URI in the options, as the tree path takes it; with no
        // rule for "/", and with one.
        var stylesheet = Stylesheet(rules);
        var engine = new Engine.XsltTransformEngine((await CompiledStylesheet.CompileAsync(stylesheet)).Stylesheet);
        using var reader = System.Xml.XmlReader.Create(new StringReader(Books.Trim()), new System.Xml.XmlReaderSettings { Async = true });
        using var output = new StringWriter();

        await engine.TransformAsync(reader, output, new Engine.XsltTransformOptions { SourceDocumentUri = new Uri(SourceUri) });

        var tree = await RunAsync(stylesheet, Books, disableStreaming: true);
        tree.Output.Should().Contain($"""<b base="{SourceUri}" doc="{SourceUri}"/>""");
        output.ToString().Should().Be(tree.Output);
    }

    [Fact]
    public async Task An_accumulator_rule_for_a_streamed_source_document_reads_its_uri()
    {
        var dir = Directory.CreateTempSubdirectory("phx-652").FullName;
        try
        {
            var path = Path.Combine(dir, "books.xml");
            await File.WriteAllTextAsync(path, Books);
            var uri = new Uri(path).AbsoluteUri;
            string StylesheetFor(string streamable) => $$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:mode name="m" streamable="{{streamable}}" on-no-match="shallow-skip" use-accumulators="doc"/>
                  <xsl:accumulator name="doc" initial-value="()" streamable="yes">
                    <xsl:accumulator-rule match="/" select="(base-uri(.), document-uri(.))"/>
                  </xsl:accumulator>
                  <xsl:template name="xsl:initial-template">
                    <out><xsl:source-document href="{{uri}}" streamable="{{streamable}}" use-accumulators="doc"><xsl:apply-templates mode="m"/></xsl:source-document></out>
                  </xsl:template>
                  <xsl:template match="book" mode="m"><b doc="{accumulator-before('doc')}"/></xsl:template>
                </xsl:stylesheet>
                """;

            var tree = await RunNamedAsync(StylesheetFor("no"));
            var streamed = await RunNamedAsync(StylesheetFor("yes"));

            tree.Should().Contain($"""<b doc="{uri} {uri}"/>""");
            streamed.Should().Be(tree);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Base_uri_in_a_streamed_source_document_is_its_uri()
    {
        var dir = Directory.CreateTempSubdirectory("phx-652").FullName;
        try
        {
            var path = Path.Combine(dir, "books.xml");
            await File.WriteAllTextAsync(path, Books);
            var uri = new Uri(path).AbsoluteUri;
            string StylesheetFor(string streamable) => $$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:mode name="m" streamable="{{streamable}}" on-no-match="shallow-skip"/>
                  <xsl:template name="xsl:initial-template">
                    <out><xsl:source-document href="{{uri}}" streamable="{{streamable}}"><xsl:apply-templates mode="m"/></xsl:source-document></out>
                  </xsl:template>
                  <xsl:template match="book" mode="m"><b base="{base-uri(.)}" doc="{document-uri(root())}"/></xsl:template>
                </xsl:stylesheet>
                """;

            var tree = await RunNamedAsync(StylesheetFor("no"));
            var streamed = await RunNamedAsync(StylesheetFor("yes"));

            tree.Should().Contain(uri);
            streamed.Should().Be(tree);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_accumulator_rule_for_the_document_node_runs_with_no_rule_for_it()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="#all"/>
              <xsl:accumulator name="doc" initial-value="'init'" streamable="yes">
                <xsl:accumulator-rule match="/" select="($value, 'start', base-uri(.))"/>
                <xsl:accumulator-rule match="book" select="($value, 'book')"/>
              </xsl:accumulator>
              <xsl:template match="book"><b doc="{accumulator-before('doc')}"/></xsl:template>
            </xsl:stylesheet>
            """;

        var tree = await RunAsync(stylesheet, Books, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Books, disableStreaming: false);

        tree.Output.Should().Contain($"init start {SourceUri} book");
        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    private const string CommentsAndInstructions = """
        <?xml-model href="books.sch"?>
        <!-- before -->
        <books><!-- in --><?pi v?><book price="1"/></books>
        <!-- after -->
        """;

    public static TheoryData<string> CommentAndInstructionRules => new()
    {
        """<xsl:template match="comment() | processing-instruction()"><n name="{name()}" parent="{exists(..)}" doc="{root() instance of document-node()}" base="{base-uri(.)}" up="{count(ancestor::node())}"/></xsl:template>""",
        // A comment or an instruction is not the root of its tree; the document node is.
        """<xsl:template match="root()"><r><xsl:apply-templates/></r></xsl:template><xsl:template match="book"><b/></xsl:template>""",
        """<xsl:template match="/"><out><xsl:apply-templates select="." mode="v"/></out></xsl:template><xsl:template match="* | root()" mode="v" priority="-10"><xsl:apply-templates select="node()" mode="v"/></xsl:template><xsl:template match="book" mode="v"><b up="{string-join(ancestor::node() ! (if (. instance of document-node()) then '#doc' else name()), '/')}"/></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(CommentAndInstructionRules))]
    public async Task A_streamed_comment_or_instruction_has_its_parent(string rules)
    {
        var stylesheet = Stylesheet(rules);

        var tree = await RunAsync(stylesheet, CommentsAndInstructions, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, CommentsAndInstructions, disableStreaming: false);

        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    [Fact]
    public async Task A_stylesheet_instruction_before_the_document_element_is_processed_in_its_place()
    {
        // An input that starts with <?xml-stylesheet?>, with a rule for "/" and one for
        // processing instructions: the instruction is a child of the document node, and the
        // document element is not read as its child.
        var input = """
            <?xml-stylesheet type="text/xsl" href="view.xsl"?>
            <books><book price="1"/><book price="2"/></books>
            """;
        var stylesheet = Stylesheet("""
            <xsl:template match="/"><out><xsl:apply-templates/></out></xsl:template>
            <xsl:template match="processing-instruction()"><pi name="{name()}" parent="{if (.. instance of document-node()) then '#doc' else name(..)}"><xsl:apply-templates/></pi></xsl:template>
            <xsl:template match="book"><b price="{@price}" up="{string-join(ancestor::node() ! (if (. instance of document-node()) then '#doc' else name()), '/')}"/></xsl:template>
            """);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        tree.Output.Should().Contain("""<pi name="xml-stylesheet" parent="#doc"/>""")
            .And.Contain("""<b price="2" up="#doc/books"/>""");
        streamed.Output.Should().Be(tree.Output);
    }

    [Fact]
    public async Task A_schxslt2_validator_streams_with_its_document_attributes()
    {
        // The validator of xslt#582 with its root() reads written as base-uri(.) and its catch
        // without them (the form that streams before xslt#582's own fixes), on the input of the
        // issue, which starts with <?xml-model?>. The report must be the unstreamed one, with a
        // document attribute on each fired-rule and the books in the document node.
        var tree = await RunAsync(Schxslt2Validator, Schxslt2Input, disableStreaming: true);
        var streamed = await RunAsync(Schxslt2Validator, Schxslt2Input, disableStreaming: false);

        tree.Output.Should().Contain($"""<svrl:fired-rule context="book" document="{SourceUri}" up="#doc/books"/>""");
        Occurrences(tree.Output, "<svrl:fired-rule ").Should().Be(4);
        Occurrences(tree.Output, "<svrl:failed-assert ").Should().Be(1);
        Occurrences(tree.Output, "<svrl:successful-report ").Should().Be(1);
        streamed.Output.Should().Be(tree.Output);
        streamed.Streamed.Should().BeTrue();
    }

    [Theory]
    // Declared on an ancestor of the element the rule reads whole, and on the element itself.
    [InlineData("""<r xmlns:up="urn:up"><Pool xmlns:my="urn:my"><Loan><x my:v="1" up:w="2"/></Loan><Loan xmlns:own="urn:own"><y own:z="3"/></Loan></Pool></r>""")]
    // W3C streamable-010: declared on a descendant, beside an attribute that uses it.
    [InlineData("""<r><Pool><Loan><e my:value="22" xmlns:my="http://my.com/ns"><!--c-->242<?PI?></e></Loan></Pool></r>""")]
    public async Task A_copy_of_an_element_read_whole_for_a_rule_keeps_its_namespaces(string input)
    {
        // A rule for Pool applies templates to its children, so each Loan is read whole for its
        // rule; the rule copies it into a temporary tree and reads the tree. The element read had
        // no namespace declarations: on main the copy named prefixes it did not declare (the
        // output was not namespace-well-formed); with base URIs on streamed nodes the temporary
        // tree is read back from that text, which failed with XPTY0019.
        var stylesheet = Stylesheet("""
            <xsl:template match="Pool"><p><xsl:apply-templates/></p></xsl:template>
            <xsl:template match="Loan"><xsl:variable name="l"><xsl:copy-of select="."/></xsl:variable><r n="{count($l/Loan//*)}"><xsl:copy-of select="$l"/></r></xsl:template>
            """);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        // Namespace-well-formed, with the same names (namespace and local name), values, text,
        // comments and instructions. Where the declarations are made is not compared: the
        // streamed copy does not carry the in-scope namespaces that no name in it uses (BUGS #159).
        Names(streamed.Output).Should().Be(Names(tree.Output));
        streamed.Streamed.Should().BeTrue();
    }

    // Each node of the document with its expanded name: elements, attributes in name order (no
    // namespace declarations), text, comments and processing instructions. Parsing fails for output
    // that is not namespace-well-formed.
    private static string Names(string xml)
    {
        var text = new StringBuilder();
        void Write(System.Xml.Linq.XNode node)
        {
            switch (node)
            {
                case System.Xml.Linq.XElement e:
                    text.Append('<').Append(e.Name);
                    foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
                        text.Append(' ').Append(a.Name).Append("=\"").Append(a.Value).Append('"');
                    text.Append('>');
                    foreach (var child in e.Nodes())
                        Write(child);
                    text.Append("</>");
                    break;
                default:
                    text.Append(node.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
                    break;
            }
        }
        Write(System.Xml.Linq.XElement.Parse($"<_c>{xml}</_c>"));
        return text.ToString();
    }

    private static int Occurrences(string text, string what)
    {
        var n = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string Stylesheet(string rules) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
          <xsl:mode name="v" streamable="yes" on-no-match="shallow-skip"/>
          {{rules}}
        </xsl:stylesheet>
        """;

    private static async Task<(string Output, bool Streamed)> RunAsync(string stylesheet, string input, bool disableStreaming)
    {
        var transformer = new XsltTransformer { DisableStreaming = disableStreaming };
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetSourceDocumentUri(new Uri(SourceUri));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return (output.ToString(), transformer.LastTransformStreamed);
    }

    private static async Task<string> RunNamedAsync(string stylesheet)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await transformer.TransformAsync((string?)null);
    }

    private const string Schxslt2Input = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?xml-model href="price.sch" type="application/xml" schematypens="http://purl.oclc.org/dsdl/schematron"?>
        <books>
            <book price="93"><title>Learning XSLT</title></book>
            <book price="3000"><title>XSL FO</title></book>
            <book price="7"><title>XSLT and XPath On The Edge</title></book>
            <book price="97"><title>XSLT 2.0 Programmer's Reference</title></book>
        </books>
        """;

    // The SchXslt2 1.11.4 output of xslt#582 (Martin Honnen's streamable version), with the
    // changes named above and an "up" attribute on fired-rule that lists the ancestors.
    private const string Schxslt2Validator = """
        <stylesheet xmlns="http://www.w3.org/1999/XSL/Transform"
                    xmlns:svrl="http://purl.oclc.org/dsdl/svrl"
                    xmlns:map="http://www.w3.org/2005/xpath-functions/map"
                    xmlns:schxslt="http://dmaus.name/ns/2023/schxslt"
                    version="3.0" expand-text="false" exclude-result-prefixes="#all">
           <variable name="Q{http://dmaus.name/ns/2023/schxslt}phase" as="Q{http://www.w3.org/2001/XMLSchema}string" select="'#ALL'"/>
           <mode use-accumulators="" streamable="true"/>
           <mode name="Q{http://dmaus.name/ns/2023/schxslt}validate" streamable="true" use-accumulators="" on-no-match="shallow-skip"/>
           <mode name="group.d6e2" on-no-match="shallow-skip" streamable="true" use-accumulators=""/>
           <template match="* | root()" mode="group.d6e2" priority="-10">
              <apply-templates select="@*" mode="#current"/>
              <apply-templates select="node()" mode="#current"/>
           </template>
           <template match="book" mode="group.d6e2" priority="0">
              <param name="Q{http://dmaus.name/ns/2023/schxslt}pattern" as="Q{http://www.w3.org/2001/XMLSchema}string*" select="()"/>
              <choose>
                 <when test="'d6e2' = $Q{http://dmaus.name/ns/2023/schxslt}pattern">
                    <svrl:suppressed-rule context="book">
                       <if test="(document-uri(.), base-uri(.))[1]">
                          <attribute name="document" select="(document-uri(.), base-uri(.))[1]"/>
                       </if>
                    </svrl:suppressed-rule>
                    <next-match>
                       <with-param name="Q{http://dmaus.name/ns/2023/schxslt}pattern" as="Q{http://www.w3.org/2001/XMLSchema}string*" select="$Q{http://dmaus.name/ns/2023/schxslt}pattern"/>
                    </next-match>
                 </when>
                 <otherwise>
                    <svrl:fired-rule context="book">
                       <if test="(document-uri(.), base-uri(.))[1]">
                          <attribute name="document" select="(document-uri(.), base-uri(.))[1]"/>
                       </if>
                       <attribute name="up" select="string-join(ancestor::node() ! (if (. instance of document-node()) then '#doc' else name()), '/')"/>
                    </svrl:fired-rule>
                    <variable name="Q{http://dmaus.name/ns/2023/schxslt}severity" as="map(Q{http://www.w3.org/2001/XMLSchema}string, Q{http://www.w3.org/2001/XMLSchema}integer)">
                       <map>
                          <map-entry key="'d6e6'">
                             <variable name="severity" as="Q{http://www.w3.org/2001/XMLSchema}string" select="'fatal'"/>
                             <sequence select="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity($severity)"/>
                          </map-entry>
                          <map-entry key="'d6e9'">
                             <variable name="severity" as="Q{http://www.w3.org/2001/XMLSchema}string" select="'fatal'"/>
                             <sequence select="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity($severity)"/>
                          </map-entry>
                       </map>
                    </variable>
                    <for-each select=".">
                       <choose>
                          <when test="map:get($schxslt:severity, 'd6e6') ge Q{http://dmaus.name/ns/2023/schxslt}numeric-severity('info')">
                             <if test="not(@price &gt; 10)">
                                <variable name="failed-assert" as="element(svrl:failed-assert)">
                                   <svrl:failed-assert test="@price &gt; 10"><svrl:text>The book price is too small</svrl:text></svrl:failed-assert>
                                </variable>
                                <sequence select="$failed-assert"/>
                             </if>
                          </when>
                          <otherwise><svrl:skipped-assert severityThreshold="info" test="@price &gt; 10"/></otherwise>
                       </choose>
                       <choose>
                          <when test="map:get($schxslt:severity, 'd6e9') ge Q{http://dmaus.name/ns/2023/schxslt}numeric-severity('info')">
                             <if test="@price &gt; 1000">
                                <variable name="successful-report" as="element(svrl:successful-report)">
                                   <svrl:successful-report test="@price &gt; 1000"><svrl:text>The book price is too big</svrl:text></svrl:successful-report>
                                </variable>
                                <sequence select="$successful-report"/>
                             </if>
                          </when>
                          <otherwise><svrl:skipped-report severityThreshold="info" test="@price &gt; 1000"/></otherwise>
                       </choose>
                    </for-each>
                    <next-match>
                       <with-param name="Q{http://dmaus.name/ns/2023/schxslt}pattern" as="Q{http://www.w3.org/2001/XMLSchema}string*" select="('d6e2', $Q{http://dmaus.name/ns/2023/schxslt}pattern)"/>
                    </next-match>
                 </otherwise>
              </choose>
           </template>
           <template match="root()" as="element(svrl:schematron-output)">
              <svrl:schematron-output phase="#ALL">
                 <try>
                    <apply-templates select="." mode="Q{http://dmaus.name/ns/2023/schxslt}validate"/>
                    <catch>
                       <svrl:error code="{$Q{http://www.w3.org/2005/xqt-errors}code}"/>
                    </catch>
                 </try>
              </svrl:schematron-output>
           </template>
           <template match="root()" as="element()*" mode="Q{http://dmaus.name/ns/2023/schxslt}validate">
              <svrl:active-pattern/>
              <apply-templates select="." mode="group.d6e2"/>
           </template>
           <function name="Q{http://dmaus.name/ns/2023/schxslt}numeric-severity" as="Q{http://www.w3.org/2001/XMLSchema}integer">
              <param name="severity" as="Q{http://www.w3.org/2001/XMLSchema}string"/>
              <sequence select="(index-of(('info', 'warning', 'error', 'fatal'), $severity), 4)[1]"/>
           </function>
        </stylesheet>
        """;
}
