using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.StrictStreamability"/>: a template rule of a streamable mode with
/// more than one instruction reading the children of the matched node is not guaranteed-streamable
/// (XSLT 3.0 §19.8.4.1). By default the engine runs it on a buffered copy of the matched subtree
/// and the result is right; under the strict option it reports XTSE3430 (xslt#295).
/// </summary>
public class StrictStreamabilityTests
{
    private const string Input =
        """<root><item id="1"><foo>f1</foo><bar>b1</bar></item><item id="2"><foo>f2</foo><bar>b2</bar></item></root>""";

    private static async Task<XsltTransformer> LoadAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
              <xsl:template match="item"><xsl:copy>{body}</xsl:copy></xsl:template>
            </xsl:stylesheet>
            """);
        return t;
    }

    private static async Task<string> RunAsync(string body, bool strict = false, bool disableStreaming = false)
    {
        var t = await LoadAsync(body);
        t.StrictStreamability = strict;
        t.DisableStreaming = disableStreaming;
        return (await t.TransformAsync(Input)).Trim();
    }

    public static TheoryData<string> SeveralConsumingOperands => new()
    {
        // xslt#295 as reported
        "<a>{foo}</a><b>{bar}</b>",
        "<xsl:value-of select=\"foo\"/><xsl:apply-templates/>",
        "<xsl:apply-templates select=\"foo\"/><xsl:apply-templates select=\"bar\"/>",
        "<xsl:if test=\"foo\"><xsl:value-of select=\"bar\"/></xsl:if>",
        "<x a=\"{foo}\">{bar}</x>",
        "<xsl:variable name=\"v\" select=\"string(foo)\"/><xsl:value-of select=\"$v, bar\"/>",
        "<xsl:choose><xsl:when test=\"foo = 'f1'\"><xsl:value-of select=\"bar\"/></xsl:when><xsl:otherwise>-</xsl:otherwise></xsl:choose>",
    };

    public static TheoryData<string> AtMostOneConsumingOperand => new()
    {
        "{foo}",
        "<a>{foo}</a><b>{@id}</b>",
        "<xsl:value-of select=\"@id\"/><xsl:apply-templates/>",
        // alternatives: only one branch runs
        "<xsl:choose><xsl:when test=\"@id = '1'\"><xsl:value-of select=\"foo\"/></xsl:when><xsl:otherwise><xsl:value-of select=\"bar\"/></xsl:otherwise></xsl:choose>",
        // each prong of a fork has the input to itself
        "<xsl:fork><xsl:sequence><a>{foo}</a></xsl:sequence><xsl:sequence><b>{bar}</b></xsl:sequence></xsl:fork>",
        // one instruction, whatever its expression holds
        "<xsl:value-of select=\"foo | bar\"/>",
        // the body of xsl:for-each has another focus
        "<xsl:for-each select=\"*\"><n>{name()}</n><v>{text()}</v></xsl:for-each>",
    };

    [Theory]
    [MemberData(nameof(SeveralConsumingOperands))]
    public async Task Strict_reports_XTSE3430_for_several_consuming_operands(string body)
    {
        var run = () => RunAsync(body, strict: true);
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE3430");
    }

    [Theory]
    [MemberData(nameof(SeveralConsumingOperands))]
    public async Task By_default_the_same_rule_runs_and_gives_the_tree_answer(string body)
        => (await RunAsync(body)).Should().Be(await RunAsync(body, disableStreaming: true));

    [Theory]
    [MemberData(nameof(SeveralConsumingOperands))]
    public async Task Strict_does_not_apply_when_streaming_is_disabled(string body)
        => (await RunAsync(body, strict: true, disableStreaming: true)).Should().Be(await RunAsync(body, disableStreaming: true));

    [Theory]
    [MemberData(nameof(AtMostOneConsumingOperand))]
    public async Task Strict_accepts_a_rule_with_at_most_one_consuming_operand(string body)
        => (await RunAsync(body, strict: true)).Should().Be(await RunAsync(body, disableStreaming: true));

    [Fact]
    public async Task The_reported_stylesheet_gives_the_complete_output_by_default()
        => (await RunAsync("<a>{foo}</a><b>{bar}</b>")).Should().Be(
            """<root><item><a>f1</a><b>b1</b></item><item><a>f2</a><b>b2</b></item></root>""");

    [Fact]
    public async Task Strict_applies_to_the_stream_overload_too()
    {
        var t = await LoadAsync("<a>{foo}</a><b>{bar}</b>");
        t.StrictStreamability = true;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input));
        await using var output = new StringWriter();
        XsltException? error = null;
        try
        {
            await t.TransformAsync(input, output);
        }
        catch (XsltException ex)
        {
            error = ex;
        }
        error.Should().NotBeNull();
        error!.Message.Should().StartWith("XTSE3430");
    }

    private const string DocumentRule = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
          <xsl:template match="/"><out><xsl:apply-templates select="root/item/foo"/><xsl:apply-templates select="root/item/bar"/></out></xsl:template>
        </xsl:stylesheet>
        """;

    private const string DocumentRuleOutput = "<out><foo>f1</foo><foo>f2</foo><bar>b1</bar><bar>b2</bar></out>";

    /// <summary>
    /// A rule that matches the document node has no matched element to buffer. Its second
    /// xsl:apply-templates found the input already read and wrote nothing, with no error (xslt#298).
    /// </summary>
    [Fact]
    public async Task A_document_rule_with_several_consuming_operands_gives_the_complete_output()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(DocumentRule);
        (await t.TransformAsync(Input)).Trim().Should().Be(DocumentRuleOutput);
    }

    [Fact]
    public async Task So_does_the_stream_overload()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(DocumentRule);
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input));
        (await t.TransformAsync(input)).Trim().Should().Be(DocumentRuleOutput);
    }

    [Fact]
    public async Task A_stylesheet_with_no_streamable_mode_is_not_affected()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="item"><i>{foo}{bar}</i></xsl:template>
            </xsl:stylesheet>
            """);
        t.StrictStreamability = true;
        (await t.TransformAsync(Input)).Trim().Should().Be("<i>f1b1</i><i>f2b2</i>");
    }
}
