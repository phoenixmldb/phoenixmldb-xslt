using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A shadow attribute (<c>_select</c>, <c>_name</c>, …) is resolved at compile time by evaluating
/// its attribute-value template against the static variables in scope.
/// </summary>
/// <remarks>
/// The engine had two implementations of one job — evaluating XPath at compile time with no context
/// item. <c>use-when</c> got a real AST evaluator; shadow attributes got a string matcher that
/// understood <c>||</c>, <c>$var</c> and literals, and when it met anything else it substituted the
/// expression's SOURCE TEXT where its value belonged. See BUGS.md #47.
///
/// Test shapes contributed by the parsers2 session; the <c>'a' || 'b'</c> and StringConcat cases
/// were added from the repro matrix that found the remaining two defects.
/// </remarks>
public sealed class ShadowAttributeStaticValueTests : IDisposable
{
    private readonly string _dir;

    public ShadowAttributeStaticValueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxshadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri(Path.Combine(_dir, "s.xsl"))).ConfigureAwait(false);
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<in/>").ConfigureAwait(false);
    }

    /// <summary>
    /// The OUTCOME of a compile-and-run: <c>"ok:"</c> plus the output when it worked, or the
    /// exception message when it did not.
    /// </summary>
    /// <remarks>
    /// Two reasons this is not just RunAsync. A test asserting "the outcome does not contain the
    /// source text" must see the FAILURE path too — on unfixed source this stylesheet throws, and
    /// the pasted text is inside the exception message, which is exactly where it must not be. And
    /// the <c>ok:</c> prefix separates "compiled and produced the right answer" from "failed for
    /// some unrelated reason", without which a NotContain assertion passes trivially on any
    /// failure. Design from the parsers2 session.
    /// </remarks>
    private async Task<string> OutcomeAsync(string stylesheet)
    {
        try
        {
            return "ok:" + await RunAsync(stylesheet).ConfigureAwait(false);
        }
        catch (XsltException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// THE regression test. A static variable whose select is an expression must reach the shadow
    /// attribute as its VALUE. Before the fix the source text was pasted, so the compiled
    /// expression read <c>$prefix || 'string-length'#1</c> and died in the XPath parser.
    /// </summary>
    [Fact]
    public async Task An_expression_valued_static_variable_is_not_pasted_as_source_text()
    {
        var outcome = await OutcomeAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="prefix" static="yes" select="''"/>
              <xsl:variable name="fname" static="yes" select="$prefix || 'string-length'" as="xs:string"/>
              <xsl:template name="main">
                <xsl:variable name="f" _select="{$fname}#1"/>
                <out><xsl:value-of select="$f('hello')"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """);

        outcome.Should().NotContain("$prefix || 'string-length'",
            "the variable's source text must not appear in the outcome — not in the output, and not "
            + "inside an error message either, which is where it landed before the fix");
        outcome.Should().StartWith("ok:", "the stylesheet must actually compile and run, not merely avoid the text");
        outcome.Should().Contain("5", "string-length('hello') is 5");
    }

    /// <summary>
    /// The case that hid the defect: a literal select is textually identical to its own value, so
    /// pasting source text and substituting the value are indistinguishable here.
    /// </summary>
    [Fact]
    public async Task A_literal_valued_static_variable_still_works()
    {
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="fname" static="yes" select="'string-length'" as="xs:string"/>
              <xsl:template name="main">
                <xsl:variable name="f" _select="{$fname}#1"/>
                <out><xsl:value-of select="$f('hello')"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """);

        result.Should().Contain("5");
    }

    /// <summary>
    /// A "is it a string literal?" test written as StartsWith('\'') &amp;&amp; EndsWith('\'') also
    /// accepts <c>'a' || 'b'</c> and strips the outer quotes, yielding <c>a' || 'b</c>. Only
    /// something that parses the expression can tell a literal from an expression that merely
    /// begins and ends with a quote. The middle case is the one a future refactor is most likely
    /// to break again, which is why all three shapes are pinned here.
    /// </summary>
    [Theory]
    [InlineData("'string-length'", "string-length")]
    [InlineData("'a' || 'b'", "ab")]
    [InlineData("\"a\" || \"b\"", "ab")]
    public async Task A_quoted_expression_is_evaluated_not_unwrapped(string select, string expected)
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="v" static="yes" select="@@SELECT@@" as="xs:string"/>
              <xsl:template name="main"><xsl:element _name="{$v}"/></xsl:template>
            </xsl:stylesheet>
            """.Replace("@@SELECT@@", select.Replace("\"", "&quot;", StringComparison.Ordinal), StringComparison.Ordinal);

        var result = await RunAsync(stylesheet);

        result.Should().Contain("<" + expected);
    }

    /// <summary>
    /// <c>||</c> parses to StringConcatExpression, a different AST node from
    /// BinaryExpression{Operator=Concat}. The static evaluator handled only the latter, so every
    /// static concatenation was reported unevaluable and silently produced nothing.
    /// </summary>
    [Fact]
    public async Task Concatenation_of_static_variables_evaluates_at_compile_time()
    {
        var result = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="a" static="yes" select="'foo'"/>
              <xsl:variable name="b" static="yes" select="$a || 'bar'"/>
              <xsl:template name="main"><xsl:element _name="{$b}"/></xsl:template>
            </xsl:stylesheet>
            """);

        result.Should().Contain("<foobar");
    }

    /// <summary>
    /// A shadow attribute the processor cannot evaluate must say so. It used to resolve to the
    /// empty string, which was then handed to the XPath parser — producing
    /// "XPST0003: mismatched input '&lt;EOF&gt;'", a message about the empty string we ourselves
    /// produced, naming neither the attribute nor the expression. All 166 cases in the W3C
    /// fn/system-property-gen set fail exactly that way.
    /// </summary>
    /// <remarks>
    /// The example was an inline function call until xslt#156 made those evaluable; a static
    /// expression has no context item, so one that needs it stays unevaluable.
    /// </remarks>
    [Fact]
    public async Task An_unevaluable_shadow_expression_reports_its_own_cause()
    {
        var act = async () => await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="v" static="yes" _select="{name(.)}"/>
              <xsl:template name="main"><out a="{$v}"/></xsl:template>
            </xsl:stylesheet>
            """);

        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.WithMessage("*_select*", "the message must name the attribute that failed");
        ex.WithMessage("*name(.)*", "and the sub-expression it could not evaluate");
    }

    /// <summary>
    /// Static expressions are evaluated by the engine's runtime evaluator when the hand-written
    /// one cannot: inline function items and calls to them, partial application, the simple map
    /// operator, doc() with a predicated path, and static variables that hold function items
    /// (xslt#156, W3C fn/system-property-gen).
    /// </summary>
    [Theory]
    [InlineData("""<xsl:variable name="p" static="yes" select="function($x) { '(' || $x || ')' }"/> <xsl:variable name="v" static="yes" select="$p('a')"/>""", "(a)")]
    [InlineData("""<xsl:variable name="e" static="yes" select="replace(?, 'a', 'b')"/> <xsl:variable name="v" static="yes" select="$e('aa')"/>""", "bb")]
    [InlineData("""<xsl:variable name="v" static="yes" select="string-join(('x', 'y') ! upper-case(.), '-')"/>""", "X-Y")]
    [InlineData("""<xsl:variable name="d" static="yes" select="parse-xml('&lt;r&gt;&lt;i k=&quot;1&quot;&gt;one&lt;/i&gt;&lt;i k=&quot;2&quot;&gt;two&lt;/i&gt;&lt;/r&gt;')"/> <xsl:variable name="v" static="yes" select="string($d/r/i[@k = '2'])"/>""", "two")]
    [InlineData("""<xsl:variable name="p" static="yes" select="function($x) { '(' || $x || ')' }"/> <xsl:variable name="w" static="yes" select="function($x) { $p(string-join($x ! $p(.), ',')) }"/> <xsl:variable name="v" static="yes" _select="'{$w(('a', 'b'))}'"/>""", "((a),(b))")]
    public async Task Static_expressions_beyond_the_hand_written_evaluator_evaluate(string declarations, string expected)
    {
        var result = await RunAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              {{declarations}}
              <xsl:template name="main"><out use-when="$v = '{{expected}}'" a="{$v}"/></xsl:template>
            </xsl:stylesheet>
            """);

        result.Should().Contain($"a=\"{expected}\"", "the value is available at run time")
            .And.Contain("<out", "and to use-when at compile time");
    }

    /// <summary>
    /// A static expression may not call a function that needs dynamic XSLT context (XPST0017,
    /// W3C current-output-uri-901). The runtime evaluator implements them all, so the fallback
    /// must refuse them by name.
    /// </summary>
    [Fact]
    public async Task A_runtime_only_function_is_not_available_statically()
    {
        var act = async () => await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main"><xsl:result-document _href="{string-join(('a') ! current-output-uri())}"><out/></xsl:result-document></xsl:template>
            </xsl:stylesheet>
            """);

        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XPST0017*");
    }
}
