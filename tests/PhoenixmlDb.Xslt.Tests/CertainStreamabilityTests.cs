using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XTSE3430 by default: a template rule of a streamable mode, or the body of a streamable
/// xsl:source-document, that the modelled rules of XSLT 3.0 §19.8 find not guaranteed-streamable
/// is an error without the strict option. A verdict that depends on a construct the analysis
/// does not model is left to the strict option, and the rule runs on a buffered copy.
/// </summary>
public class CertainStreamabilityTests
{
    private const string Input = """<root><item id="1"><foo>f1</foo><bar>b1</bar></item><item id="2"><foo>f2</foo><bar>b2</bar></item></root>""";

    private static async Task<string> RunAsync(string rules, bool strict = false, bool disableStreaming = false, bool fallback = false)
    {
        var t = new XsltTransformer
        {
            StrictStreamability = strict,
            DisableStreaming = disableStreaming,
            StreamingFallback = fallback,
        };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
              {rules}
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(Input)).Trim();
    }

    private static async Task<string?> ErrorAsync(string rules, bool strict = false)
    {
        try
        {
            await RunAsync(rules, strict);
            return null;
        }
        catch (XsltException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Not guaranteed-streamable by the general rules (§19.8.1), which the analysis applied to
    /// expressions only: two operands of one instruction or one sequence constructor that read
    /// the children of the matched node. Each ran on a buffered copy, also under the strict
    /// option (W3C si-assert-902, streamable-107, streamable-110).
    /// </summary>
    public static TheoryData<string> CertainlyNotStreamable => new()
    {
        // §19.8.4.6: the test of xsl:assert is inspected and its content absorbed (si-assert-902)
        """<xsl:template match="item"><xsl:assert test="string(foo) ne ''"><xsl:sequence select="string(bar)"/></xsl:assert><xsl:copy/></xsl:template>""",
        // §19.8.4.26: the select and the content of xsl:message are both absorbed
        """<xsl:template match="item"><xsl:message select="string(foo)"><xsl:value-of select="bar"/></xsl:message><xsl:copy/></xsl:template>""",
        // §19.8.3: two instructions that atomize the matched element (streamable-107)
        """<xsl:template match="foo"><xsl:attribute name="a" select="string(.)"/><xsl:attribute name="b" select="string(.)"/></xsl:template>""",
        // §19.8.4.41: without "as" the select of xsl:variable is navigated, so it cannot hold a
        // streamed node (streamable-110)
        """<xsl:template match="item"><xsl:variable name="this" select="."/><x>{count($this/foo)}</x></xsl:template>""",
        // §19.8.4.21: a test and a branch that both consume
        """<xsl:template match="item"><xsl:if test="foo = 'f1'"><xsl:value-of select="bar"/></xsl:if></xsl:template>""",
        // §19.8.4.1: an attribute value template and the content of a literal result element
        """<xsl:template match="item"><x a="{foo}"><xsl:value-of select="bar"/></x></xsl:template>""",
    };

    [Theory]
    [MemberData(nameof(CertainlyNotStreamable))]
    public async Task Is_XTSE3430_by_default(string rules)
        => (await ErrorAsync(rules)).Should().StartWith("XTSE3430");

    [Theory]
    [MemberData(nameof(CertainlyNotStreamable))]
    public async Task Is_XTSE3430_under_the_strict_option(string rules)
        => (await ErrorAsync(rules, strict: true)).Should().StartWith("XTSE3430");

    [Theory]
    [MemberData(nameof(CertainlyNotStreamable))]
    public async Task Runs_without_streaming_when_it_is_disabled(string rules)
        => (await RunAsync(rules, disableStreaming: true)).Should().StartWith("<root>");

    /// <summary>
    /// The answer depends on what the analysis does not know: the classifier puts an attribute
    /// on the climbing posture, where §19.8.8.9 has it striding, so whether a variable may hold
    /// it is not established. The default runs the rule; the strict option reports it.
    /// </summary>
    [Fact]
    public async Task An_uncertain_verdict_is_left_to_the_strict_option()
    {
        const string Rules = """<xsl:template match="item"><xsl:variable name="i" select="@id"/><x>{$i}</x></xsl:template>""";
        (await RunAsync(Rules)).Should().Be("<root><x>1</x><x>2</x></root>");
        (await ErrorAsync(Rules, strict: true)).Should().StartWith("XTSE3430");
    }

    /// <summary>
    /// Absorbing a node that cannot have children is inspection (§19.8.1), so two atomizations
    /// of a text node are motionless; the type comes from the match pattern.
    /// </summary>
    [Fact]
    public async Task Atomizing_a_text_node_twice_is_streamable()
        => (await RunAsync("""<xsl:template match="foo/text()"><xsl:value-of select="string(.)"/>-<xsl:value-of select="string(.)"/></xsl:template>""", strict: true))
            .Should().Be("""<root><item id="1"><foo>f1-f1</foo><bar>b1</bar></item><item id="2"><foo>f2-f2</foo><bar>b2</bar></item></root>""");

    /// <summary>
    /// xsl:copy with a select and content that also reads is taken as xsl:for-each over the
    /// selection, as the W3C suite expects (si-copy-028) and Saxon-EE and Exselt report passing.
    /// No error by default; the strict option keeps its own detector of two readers of the
    /// children, which counts the select and the content.
    /// </summary>
    [Fact]
    public async Task Nested_xsl_copy_with_select_is_not_an_error_by_default()
        => (await RunAsync("""<xsl:template match="root"><xsl:copy select="item[1]"><xsl:copy select="foo"/></xsl:copy></xsl:template>"""))
            .Should().Be("<item><foo/></item>");

    /// <summary>
    /// A quantifier over streamed nodes whose condition reads only what each node holds when it
    /// is reached, as the W3C suite expects (streamable-100) and Saxon-EE and Exselt report passing.
    /// </summary>
    [Fact]
    public async Task A_quantifier_that_reads_attributes_of_its_variable_is_streamable()
        => (await RunAsync("""<xsl:template match="root"><r>{some $i in item satisfies $i/@id = '2'}</r></xsl:template>""", strict: true))
            .Should().Be("<r>true</r>");

    /// <summary>The engine's own tests reach the buffered evaluation with the internal switch.</summary>
    [Theory]
    [MemberData(nameof(CertainlyNotStreamable))]
    public async Task The_fallback_switch_gives_the_tree_answer(string rules)
    {
        // xsl:message and xsl:assert write to the message listener; compare the output only.
        (await RunAsync(rules, fallback: true)).Should().Be(await RunAsync(rules, disableStreaming: true));
    }
}
