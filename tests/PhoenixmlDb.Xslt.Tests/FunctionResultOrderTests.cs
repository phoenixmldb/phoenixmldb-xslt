using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The result of a stylesheet function is the items of its body in the order the body gives them
/// (XSLT 3.0 §5.7), also when the body mixes literal result elements with atomic values or copies
/// (#645). The elements were written to the output buffer and the other items collected apart, and
/// the function returned the elements first: a body <c>&lt;xsl:sequence select="1"/&gt;&lt;w/&gt;</c> gave
/// (&lt;w/&gt;, 1). A typed variable and a typed named template kept the order; their rows guard it.
/// The expected results are Saxon-HE 10.9's, but for the function of a used package, whose body is
/// the first function's with one more item.
/// </summary>
public sealed class FunctionResultOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-fnorder-" + Guid.NewGuid().ToString("N"));

    public FunctionResultOrderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Head = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="f">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
        """;

    // Each item of $s in an element of its own, to show the items and their order.
    private const string Show = """<out><xsl:for-each select="$s"><i><xsl:copy-of select="."/></i></xsl:for-each></out>""";

    private static string Stylesheet(string context, string body) => context switch
    {
        "function" => Head + $"""
              <xsl:function name="f:s" as="item()*">{body}</xsl:function>
              <xsl:template match="/"><xsl:variable name="s" select="f:s()"/>{Show}</xsl:template>
            </xsl:stylesheet>
            """,
        "variable" => Head + $"""
              <xsl:template match="/"><xsl:variable name="s" as="item()*">{body}</xsl:variable>{Show}</xsl:template>
            </xsl:stylesheet>
            """,
        _ => Head + $"""
              <xsl:template name="t" as="item()*">{body}</xsl:template>
              <xsl:template match="/"><xsl:variable name="s" as="item()*"><xsl:call-template name="t"/></xsl:variable>{Show}</xsl:template>
            </xsl:stylesheet>
            """,
    };

    private static async Task<string> RunAsync(string stylesheet, string source = "<r/>")
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(source);
    }

    // Sequence constructors that mix atomic values and literal result elements.
    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["plain"] = """<xsl:sequence select="1"/><w/>""",
        ["iterate"] = """<xsl:iterate select="1 to 2"><xsl:sequence select="."/><e n="{.}"/></xsl:iterate>""",
        ["foreach"] = """<xsl:for-each select="1 to 2"><xsl:sequence select="."/><e n="{.}"/></xsl:for-each>""",
        ["fork"] = """<xsl:fork><xsl:sequence><xsl:sequence select="1"/><a/></xsl:sequence><xsl:sequence><b/><xsl:sequence select="2"/></xsl:sequence></xsl:fork>""",
        ["try"] = """<xsl:try><xsl:sequence select="1"/><t/><xsl:catch><c/></xsl:catch></xsl:try>""",
        ["choose"] = """<xsl:choose><xsl:when test="true()"><xsl:sequence select="1"/><w/></xsl:when></xsl:choose>""",
        ["if"] = """<xsl:if test="true()"><xsl:sequence select="1"/><w/></xsl:if>""",
    };

    public static TheoryData<string, string, string> Cases => new()
    {
        { "function", "plain", """<out><i>1</i><i><w/></i></out>""" },
        { "function", "iterate", """<out><i>1</i><i><e n="1"/></i><i>2</i><i><e n="2"/></i></out>""" },
        { "function", "foreach", """<out><i>1</i><i><e n="1"/></i><i>2</i><i><e n="2"/></i></out>""" },
        { "function", "fork", """<out><i>1</i><i><a/></i><i><b/></i><i>2</i></out>""" },
        { "function", "try", """<out><i>1</i><i><t/></i></out>""" },
        { "function", "choose", """<out><i>1</i><i><w/></i></out>""" },
        { "function", "if", """<out><i>1</i><i><w/></i></out>""" },
        { "variable", "plain", """<out><i>1</i><i><w/></i></out>""" },
        { "variable", "iterate", """<out><i>1</i><i><e n="1"/></i><i>2</i><i><e n="2"/></i></out>""" },
        { "variable", "foreach", """<out><i>1</i><i><e n="1"/></i><i>2</i><i><e n="2"/></i></out>""" },
        { "variable", "fork", """<out><i>1</i><i><a/></i><i><b/></i><i>2</i></out>""" },
        { "variable", "try", """<out><i>1</i><i><t/></i></out>""" },
        { "variable", "choose", """<out><i>1</i><i><w/></i></out>""" },
        { "variable", "if", """<out><i>1</i><i><w/></i></out>""" },
        { "template", "plain", """<out><i>1</i><i><w/></i></out>""" },
        { "template", "iterate", """<out><i>1</i><i><e n="1"/></i><i>2</i><i><e n="2"/></i></out>""" },
        { "template", "foreach", """<out><i>1</i><i><e n="1"/></i><i>2</i><i><e n="2"/></i></out>""" },
        { "template", "fork", """<out><i>1</i><i><a/></i><i><b/></i><i>2</i></out>""" },
        { "template", "try", """<out><i>1</i><i><t/></i></out>""" },
        { "template", "choose", """<out><i>1</i><i><w/></i></out>""" },
        { "template", "if", """<out><i>1</i><i><w/></i></out>""" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_items_of_a_body_come_in_the_order_the_body_gives_them(string context, string body, string expected)
        => (await RunAsync(Stylesheet(context, Bodies[body]))).Should().Be(expected);

    public static TheoryData<string, string> FunctionBodies => new()
    {
        { """<xsl:param name="r"/><xsl:sequence select="1"/><w/>""", """<out><i>1</i><i><w/></i></out>""" },
        { """<xsl:param name="r"/><w/><xsl:sequence select="1"/>""", """<out><i><w/></i><i>1</i></out>""" },
        { """<xsl:param name="r"/><xsl:for-each select="$r/a"><xsl:copy/></xsl:for-each><w/>""", """<out><i><a/></i><i><a/></i><i><w/></i></out>""" },
        { """<xsl:param name="r"/><xsl:for-each select="$r/a"><xsl:copy/></xsl:for-each><xsl:sequence select="1"/>""", """<out><i><a/></i><i><a/></i><i>1</i></out>""" },
        { """<xsl:param name="r"/><x/><xsl:sequence select="1"/><y/>""", """<out><i><x/></i><i>1</i><i><y/></i></out>""" },
        { """<xsl:param name="r"/><xsl:copy-of select="$r/a"/><w/>""", """<out><i><a/></i><i><a/></i><i><w/></i></out>""" },
    };

    // Bodies with a parameter $r, the source <r><a/><a/></r>: copies (xsl:copy, xsl:copy-of) and
    // literal result elements, with atomic values.
    [Theory]
    [MemberData(nameof(FunctionBodies))]
    public async Task A_function_returns_copies_atomic_values_and_elements_in_order(string body, string expected)
    {
        var stylesheet = Head + $"""
              <xsl:function name="f:s" as="item()*">{body}</xsl:function>
              <xsl:template match="/r"><xsl:variable name="s" select="f:s(.)"/>{Show}</xsl:template>
            </xsl:stylesheet>
            """;

        (await RunAsync(stylesheet, "<r><a/><a/></r>")).Should().Be(expected);
    }

    [Fact]
    public async Task A_function_with_no_declared_type_returns_its_items_in_order()
    {
        var stylesheet = Head + """
              <xsl:function name="f:s"><xsl:sequence select="1"/><w/></xsl:function>
              <xsl:template match="/"><out><xsl:for-each select="f:s()"><i><xsl:copy-of select="."/></i></xsl:for-each></out></xsl:template>
            </xsl:stylesheet>
            """;

        (await RunAsync(stylesheet)).Should().Be("<out><i>1</i><i><w/></i></out>");
    }

    [Fact]
    public async Task A_function_of_a_used_package_returns_its_items_in_order()
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, """
            <xsl:package name="urn:lib" package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="f">
              <xsl:function name="f:s" as="item()*" visibility="public"><xsl:sequence select="1"/><w/><xsl:sequence select="2"/></xsl:function>
            </xsl:package>
            """, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = [("1.0", libPath)] };
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="f">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:use-package name="urn:lib" package-version="1.0"/>
              <xsl:template match="/"><out><xsl:for-each select="f:s()"><i><xsl:copy-of select="."/></i></xsl:for-each></out></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);

        (await transformer.TransformAsync("<r/>")).Should().Be("<out><i>1</i><i><w/></i><i>2</i></out>");
    }
}
