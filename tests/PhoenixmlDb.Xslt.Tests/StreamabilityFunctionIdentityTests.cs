using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Engine.Streamability;
using PhoenixmlDb.XQuery.Ast;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The streamability analysis identifies a built-in function by its expanded name, not its local
/// name (BUGS.md #132, the follow-up to xslt#582).
/// </summary>
/// <remarks>
/// About twenty checks in the streamability checker and the classifier's role table looked at the
/// local name only, so a stylesheet function <c>o:copy-of</c> or <c>Q{urn:other}count</c> in another
/// namespace was analysed as the built-in of that name: grounding, absorbing and focus rules applied
/// to a call that was none of those. Each case below is a stylesheet function in another namespace
/// with a built-in's local name; it must be called, and classified as a user function.
/// </remarks>
public class StreamabilityFunctionIdentityTests
{
    private const string Books =
        """<books><book price="93"><title>A</title></book><book price="3000"><title>B</title></book></books>""";

    private static async Task<string> StreamAsync(string functions, string select)
    {
        var dir = Directory.CreateTempSubdirectory("xslt132-");
        try
        {
            var source = Path.Combine(dir.FullName, "books.xml");
            await File.WriteAllTextAsync(source, Books);
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync($"""
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:other"
                    exclude-result-prefixes="#all" version="3.0">
                  <xsl:output omit-xml-declaration="yes"/>
                  {functions}
                  <xsl:template match="/">
                    <out><xsl:source-document streamable="yes" href="{new Uri(source).AbsoluteUri}"><xsl:value-of select="{select}"/></xsl:source-document></out>
                  </xsl:template>
                </xsl:stylesheet>
                """);
            return (await t.TransformAsync("<d/>")).Trim();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string UserFunction(string local, int arity) =>
        $"""<xsl:function name="o:{local}" streamability="absorbing">{string.Concat(Enumerable.Range(0, arity).Select(i => $"<xsl:param name='p{i}'/>"))}<xsl:sequence select="'user'"/></xsl:function>""";

    [Theory]
    [InlineData("copy-of", "o:copy-of(books/book)")]
    [InlineData("snapshot", "o:snapshot(books/book)")]
    [InlineData("data", "o:data(books/book)")]
    [InlineData("outermost", "o:outermost(books/book)")]
    [InlineData("innermost", "o:innermost(books/book)")]
    [InlineData("exists", "o:exists(books/book)")]
    public async Task A_stylesheet_function_with_a_built_in_local_name_is_called(string local, string call)
    {
        (await StreamAsync(UserFunction(local, 1), call)).Should().Be("<out>user</out>");
    }

    [Fact]
    public async Task A_stylesheet_function_named_current_group_is_called()
    {
        var functions = $"""<xsl:function name="o:current-group"><xsl:sequence select="'user'"/></xsl:function>""";
        (await StreamAsync(functions, "o:current-group()")).Should().Be("<out>user</out>");
    }

    // ---------------------------------------------------------------- the classifier's role table

    private static readonly StreamingContext Streamed = new(Posture.Striding, InStreamedScope: true);

    private static StepExpression Child(string name) =>
        new() { Axis = Axis.Child, NodeTest = new NameTest { LocalName = name }, Predicates = [] };

    [Fact]
    public void Count_in_another_namespace_does_not_take_fn_counts_role()
    {
        var fnCount = new FunctionCallExpression { Name = new QName(NamespaceId.None, "count"), Arguments = [Child("book")] };
        var eqName = new FunctionCallExpression
        {
            Name = new QName(NamespaceId.None, "count") { ExpandedNamespace = "urn:other" },
            Arguments = [Child("book")],
        };
        var prefixed = new FunctionCallExpression { Name = new QName(new NamespaceId(500), "count", "o"), Arguments = [Child("book")] };

        var builtIn = StreamabilityClassifier.Classify(fnCount, Streamed);
        StreamabilityClassifier.Classify(eqName, Streamed).Should().NotBe(builtIn);
        StreamabilityClassifier.Classify(prefixed, Streamed).Should().NotBe(builtIn);
    }
}
