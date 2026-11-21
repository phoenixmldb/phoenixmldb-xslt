using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Engine;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A function call written as an EQName, <c>Q{uri}local(…)</c>, reaches the engine with
/// <see cref="NamespaceId.None"/> and its namespace as text. Code that identified a built-in
/// function by <see cref="QName.Namespace"/> alone took every such call for an fn: call:
/// <c>Q{…/map}get</c> was looked up as fn:get and the rule refused as not streamable, and a
/// stylesheet function <c>Q{urn:other}sum</c> was run as fn:sum (xslt#582). And an XTSE3430 for a
/// template rule named neither the rule nor its line.
/// </summary>
public class EqNameFunctionCallTests
{
    private const string MapUri = "http://www.w3.org/2005/xpath-functions/map";
    private const string ArrayUri = "http://www.w3.org/2005/xpath-functions/array";
    private const string OtherCount = "Q{urn:other}count";

    private const string Books =
        """<books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book><book price="7"><title>C</title></book></books>""";

    private static async Task<string> RunAsync(string stylesheet, string input, bool strict)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        t.StrictStreamability = strict;
        return (await t.TransformAsync(input)).Trim();
    }

    private static string StreamableRule(string body) => $"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:m="{MapUri}" xmlns:a="{ArrayUri}"
            exclude-result-prefixes="#all" version="3.0">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes" on-no-match="shallow-skip"/>
          <xsl:template match="book">{body}</xsl:template>
        </xsl:stylesheet>
        """;

    // ---------------------------------------------------------------- the issue as reported

    [Theory]
    [InlineData("Q{http://www.w3.org/2005/xpath-functions/map}get")]
    [InlineData("m:get")]
    public async Task Map_get_passes_strict_streamability_written_either_way(string name)
    {
        var result = await RunAsync(StreamableRule($"<x n=\"{{{name}(map{{1:2}}, 1)}}\"/>"), Books, strict: true);
        result.Should().Be("<x n=\"2\"/><x n=\"2\"/><x n=\"2\"/>");
    }

    [Theory]
    [InlineData("Q{http://www.w3.org/2005/xpath-functions/map}")]
    [InlineData("m:")]
    public async Task Map_put_of_an_atomized_value_passes_strict_streamability_written_either_way(string map)
    {
        var body = $"<x n=\"{{{map}size({map}put(map{{}}, 'p', string(@price)))}}\"/>";
        var result = await RunAsync(StreamableRule(body), Books, strict: true);
        result.Should().Be("<x n=\"1\"/><x n=\"1\"/><x n=\"1\"/>");
    }

    [Theory]
    [InlineData("Q{http://www.w3.org/2005/xpath-functions/array}")]
    [InlineData("a:")]
    public async Task Array_size_passes_strict_streamability_written_either_way(string array)
    {
        var result = await RunAsync(StreamableRule($"<x n=\"{{{array}size([1, 2])}}\"/>"), Books, strict: true);
        result.Should().Be("<x n=\"2\"/><x n=\"2\"/><x n=\"2\"/>");
    }

    // ---------------------------------------------------------------- the message names the rule

    [Fact]
    public async Task XTSE3430_names_the_template_rule_and_its_line()
    {
        // Line 5 is the xsl:template; two instructions read the children of the matched node.
        var stylesheet = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode name="g" streamable="yes" on-no-match="shallow-skip"/>
              <xsl:template match="/"><out><xsl:apply-templates select="." mode="g"/></out></xsl:template>
              <xsl:template match="books" mode="g"><xsl:apply-templates mode="g"/></xsl:template>
              <xsl:template match="book" mode="g" priority="2"><a>{title}</a><b>{title}</b></xsl:template>
            </xsl:stylesheet>
            """.Replace("{title}", "<xsl:value-of select=\"title\"/>", StringComparison.Ordinal);
        var run = () => RunAsync(stylesheet, Books, strict: true);
        var error = (await run.Should().ThrowAsync<XsltException>()).Which;
        error.Message.Should().StartWith("XTSE3430");
        error.Message.Should().Contain("match=\"book\"").And.Contain("mode=\"g\"").And.Contain("(line 5)");
        error.Location.Should().NotBeNull();
        error.Location!.Line.Should().Be(5);
    }

    [Fact]
    public async Task XTSE3430_from_the_check_at_load_names_the_template_rule_and_its_line()
    {
        // current-group() in a streamable template rule is refused when the stylesheet is loaded.
        var stylesheet = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>

              <xsl:template name="t" match="book"><xsl:value-of select="count(current-group())"/></xsl:template>
            </xsl:stylesheet>
            """;
        var load = () => new XsltTransformer().LoadStylesheetAsync(stylesheet);
        var error = (await load.Should().ThrowAsync<XsltException>()).Which;
        error.Message.Should().StartWith("XTSE3430");
        error.Message.Should().Contain("name=\"t\" match=\"book\"").And.Contain("(line 4)");
        error.Location!.Line.Should().Be(4);
    }

    // ---------------------------------------------------------------- the reverse: not a built-in

    /// <summary>
    /// A stylesheet function in another namespace with a built-in's local name is that function,
    /// not the built-in. Streamed, <c>Q{urn:other}sum(//book/@price)</c> was taken for fn:sum and
    /// folded by the streaming pass, which summed the prices (3100) instead of calling the function.
    /// </summary>
    [Theory]
    [InlineData("Q{urn:other}sum(//book/@price)")]
    [InlineData("o:sum(//book/@price)")]
    [InlineData("Q{urn:other}count(books/book)")]
    public async Task A_stylesheet_function_with_a_built_in_local_name_is_called_in_a_streamed_body(string call)
    {
        var dir = Directory.CreateTempSubdirectory("xslt582-");
        try
        {
            var source = Path.Combine(dir.FullName, "books.xml");
            await File.WriteAllTextAsync(source, Books);
            var stylesheet = $"""
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:other"
                    exclude-result-prefixes="#all" version="3.0">
                  <xsl:output omit-xml-declaration="yes"/>
                  <xsl:function name="o:sum" streamability="absorbing"><xsl:param name="x"/><xsl:sequence select="'user'"/></xsl:function>
                  <xsl:function name="{OtherCount}" streamability="absorbing"><xsl:param name="x"/><xsl:sequence select="'user'"/></xsl:function>
                  <xsl:template match="/">
                    <out><xsl:source-document streamable="yes" href="{new Uri(source).AbsoluteUri}"><xsl:value-of select="{call}"/></xsl:source-document></out>
                  </xsl:template>
                </xsl:stylesheet>
                """;
            (await RunAsync(stylesheet, "<d/>", strict: false)).Should().Be("<out>user</out>");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The fast path for position() and last() answered for Q{urn:other}position() too.</summary>
    [Theory]
    [InlineData("Q{urn:other}position()")]
    [InlineData("Q{urn:other}last()")]
    public async Task A_stylesheet_function_named_position_or_last_is_called(string call)
    {
        var stylesheet = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:other"
                exclude-result-prefixes="#all" version="3.0">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:function name="o:position"><xsl:sequence select="'user'"/></xsl:function>
              <xsl:function name="o:last"><xsl:sequence select="'user'"/></xsl:function>
              <xsl:template match="/"><out><xsl:for-each select="//book"><xsl:value-of select="{call}"/>;</xsl:for-each></out></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(stylesheet, Books, strict: false)).Should().Be("<out>user;user;user;</out>");
    }

    /// <summary>
    /// A self-call of an absorbing function with a part of its streamed parameter consumes that
    /// parameter again on each level, and is refused; written as an EQName it was not recognised.
    /// </summary>
    [Theory]
    [InlineData("f:depth($n/*)")]
    [InlineData("Q{urn:f}depth($n/*)")]
    public async Task An_absorbing_function_that_calls_itself_is_recognised_written_either_way(string selfCall)
    {
        var stylesheet = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" version="3.0">
              <xsl:function name="f:depth" streamability="absorbing">
                <xsl:param name="n" as="node()*"/>
                <xsl:sequence select="if (empty($n)) then 0 else 1 + {selfCall}"/>
              </xsl:function>
              <xsl:template match="/"><out/></xsl:template>
            </xsl:stylesheet>
            """;
        var load = () => new XsltTransformer().LoadStylesheetAsync(stylesheet);
        (await load.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("recursive call");
    }

    // ---------------------------------------------------------------- the classifier itself

    private static readonly StreamingContext Streamed = new(Posture.Striding, InStreamedScope: true);
    private static readonly StreamingContext StreamedBySpec = new(Posture.Striding, InStreamedScope: true, BySpec: true);

    private static FunctionCallExpression EqName(string namespaceName, string local, params XQueryExpression[] args) => new()
    {
        Name = new QName(NamespaceId.None, local) { ExpandedNamespace = namespaceName },
        Arguments = args,
    };

    private static FunctionCallExpression Fn(string local, params XQueryExpression[] args) => new()
    {
        Name = new QName(NamespaceId.None, local),
        Arguments = args,
    };

    /// <summary>The grounding rule for map: and array: functions (§19.8.2) reads the namespace of an EQName.</summary>
    [Theory]
    [InlineData(MapUri, "put")]
    [InlineData(ArrayUri, "append")]
    public void Map_and_array_grounding_applies_to_an_EQName(string namespaceName, string local)
    {
        XQueryExpression[] args = namespaceName == MapUri
            ? [new MapConstructor { Entries = [] }, new StringLiteral { Value = "k" }, Fn("string", ContextItemExpression.Instance)]
            : [new ArrayConstructor { Kind = ArrayConstructorKind.Square, Members = [] }, Fn("string", ContextItemExpression.Instance)];
        StreamabilityClassifier.Classify(EqName(namespaceName, local, args), Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    /// <summary>
    /// A map or array keeps no streamed node (§19.8.2): an EQName map:put of a striding child
    /// sequence is not streamable, as the prefixed form is not. Before, the EQName fell to the
    /// role by local name and the reason was not the grounding rule.
    /// </summary>
    [Fact]
    public void An_EQName_map_put_of_streamed_nodes_is_not_streamable()
    {
        var children = new StepExpression { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "foo" }, Predicates = [] };
        StreamabilityClassifier.Classify(
                EqName(MapUri, "put", new MapConstructor { Entries = [] }, new StringLiteral { Value = "k" }, children), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    /// <summary>
    /// By the specification's table (§19.8.9), an EQName fn:/map: call is the built-in function;
    /// a call in another namespace with the local name of a built-in is not.
    /// </summary>
    [Fact]
    public void By_the_specification_an_EQName_call_is_looked_up_in_its_own_namespace()
    {
        StreamabilityClassifier.Classify(EqName(MapUri, "get", new MapConstructor { Entries = [] }, new IntegerLiteral { Value = 1L }), StreamedBySpec)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
        // fn:current() of a streamed node climbs; Q{urn:other}current() is not fn:current().
        StreamabilityClassifier.Classify(EqName("http://www.w3.org/2005/xpath-functions", "current"), StreamedBySpec)
            .Should().Be(new PostureSweep(Posture.Climbing, Sweep.Motionless));
        StreamabilityClassifier.Classify(EqName("urn:other", "current"), StreamedBySpec)
            .Should().NotBe(new PostureSweep(Posture.Climbing, Sweep.Motionless));
    }

    [Fact]
    public void FunctionNames_reads_the_namespace_of_each_form()
    {
        FunctionNames.IsFn(new QName(NamespaceId.None, "count")).Should().BeTrue();
        FunctionNames.IsFn(new QName(NamespaceId.Fn, "count", "fn")).Should().BeTrue();
        FunctionNames.IsFn(EqName("http://www.w3.org/2005/xpath-functions", "count").Name).Should().BeTrue();
        FunctionNames.IsFn(EqName("urn:other", "count").Name).Should().BeFalse();
        FunctionNames.IsFn(EqName("", "count").Name).Should().BeFalse();
        FunctionNames.IsMap(EqName(MapUri, "get").Name).Should().BeTrue();
        FunctionNames.IsMap(new QName(NamespaceId.Map, "get", "map")).Should().BeTrue();
        FunctionNames.IsXs(EqName("http://www.w3.org/2001/XMLSchema", "date").Name).Should().BeTrue();
        FunctionNames.SameName(
                new QName(StylesheetParser.ResolveNamespaceUri("urn:f"), "g", "f"), EqName("urn:f", "g").Name)
            .Should().BeTrue();
    }
}
