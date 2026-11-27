using AwesomeAssertions;
using PhoenixmlDb.XQuery.Functions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The error code a host reads, for each way a stylesheet raises a code. A code in the namespace
/// of this processor is <c>phx:LOCAL</c>; a code in the error namespace is its local name; a code
/// in another namespace is <c>Q{uri}local</c>, the form of the message.
/// </summary>
/// <remarks>
/// In 3.2.0 <see cref="XsltException.ErrorCode"/> was null for a code of
/// <c>xsl:message terminate="yes"</c> or <c>xsl:assert</c> outside the error namespace: the
/// message started with <c>Q{uri}local</c>, and the code reader read only a W3C-shaped code.
/// The exception of <c>fn:error</c> in an XPath expression lost the namespace and the prefix of
/// its QName when the expression's location was added: <c>fn:error(QName('urn:q', 'q:E1'))</c>
/// left as <c>E1</c> in no namespace.
/// </remarks>
public sealed class ErrorCodeRoutesTests
{
    private const string Phx = "https://schemas.phoenixml.dev/2026/functions";
    private const string Err = "http://www.w3.org/2005/xqt-errors";

    private static string Sheet(string body) =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'"
        + $" xmlns:phx='{Phx}' xmlns:q='urn:q' xmlns:err='{Err}'>"
        + $"<xsl:template match='/'>{body}</xsl:template></xsl:stylesheet>";

    private static async Task<Exception?> TransformAsync(string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Sheet(body)).ConfigureAwait(true);
        return await Record.ExceptionAsync(() => transformer.TransformAsync("<r/>")).ConfigureAwait(true);
    }

    /// <summary>fn:transform from a query: the error of the inner transformation, as the host of the query gets it.</summary>
    private static async Task<Exception?> TransformFromQueryAsync(string body)
    {
        var sheet = Sheet(body).Replace("'", "&apos;", StringComparison.Ordinal);
        var query = $"transform(map {{ 'stylesheet-text': '{sheet}', 'source-node': document {{ <r/> }} }})?output";
        return await Record.ExceptionAsync(() => new XQuery.XQueryFacade().EvaluateAsync(query)).ConfigureAwait(true);
    }

    public static TheoryData<string, string> TerminatingInstructions() => new()
    {
        { "<xsl:message terminate='yes' error-code='phx:XTDE9430'>m</xsl:message>", "phx:XTDE9430" },
        { $"<xsl:message terminate='yes' error-code='Q{{{Phx}}}XTDE9430'>m</xsl:message>", "phx:XTDE9430" },
        { "<xsl:message terminate='yes' error-code='phx:MYCODE'>m</xsl:message>", "phx:MYCODE" },
        { "<xsl:message terminate='yes' error-code='q:E1'>m</xsl:message>", "Q{urn:q}E1" },
        { "<xsl:message terminate='yes' error-code='Q{urn:q}E1'>m</xsl:message>", "Q{urn:q}E1" },
        { "<xsl:message terminate='yes' error-code='MYCODE'>m</xsl:message>", "Q{}MYCODE" },
        { "<xsl:message terminate='yes' error-code='{concat(\"q:\", \"E1\")}'>m</xsl:message>", "Q{urn:q}E1" },
        { "<xsl:assert test='false()' error-code='phx:XTDE9430'>a</xsl:assert>", "phx:XTDE9430" },
        { "<xsl:assert test='false()' error-code='q:E1'>a</xsl:assert>", "Q{urn:q}E1" },
        // Guards: the error namespace and the defaults were already right.
        { "<xsl:message terminate='yes' error-code='err:FOER0000'>m</xsl:message>", "FOER0000" },
        { "<xsl:message terminate='yes'>m</xsl:message>", "XTMM9000" },
        { "<xsl:assert test='false()' error-code='err:FOER0000'>a</xsl:assert>", "FOER0000" },
        { "<xsl:assert test='false()'>a</xsl:assert>", "XTMM9001" },
    };

    [Theory]
    [MemberData(nameof(TerminatingInstructions))]
    public async Task Xsl_message_and_xsl_assert_give_the_code_of_error_code(string body, string code)
    {
        var error = await TransformAsync(body).ConfigureAwait(true);
        error.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be(code);
    }

    [Theory]
    [MemberData(nameof(TerminatingInstructions))]
    public async Task Fn_transform_from_a_query_gives_the_same_code(string body, string code)
    {
        var error = await TransformFromQueryAsync(body).ConfigureAwait(true);
        error.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be(code);
    }

    /// <summary>
    /// The message is not changed: it starts with the code as <c>Q{uri}local</c>, and that is what
    /// xsl:catch matches.
    /// </summary>
    [Fact]
    public async Task The_message_keeps_the_expanded_name()
    {
        var error = await TransformAsync("<xsl:message terminate='yes' error-code='phx:MYCODE'>m</xsl:message>").ConfigureAwait(true);
        error!.Message.Should().StartWith($"Q{{{Phx}}}MYCODE: ");
    }

    [Theory]
    [InlineData($"QName('{Phx}', 'phx:XTDE9430')", "XTDE9430", Phx, "phx")]
    [InlineData($"QName('{Phx}', 'phx:MYCODE')", "MYCODE", Phx, "phx")]
    [InlineData("QName('urn:q', 'q:E1')", "E1", "urn:q", "q")]
    [InlineData($"QName('{Err}', 'err:FOER0001')", "FOER0001", Err, "err")]
    public async Task Fn_error_keeps_the_namespace_and_the_prefix_of_its_code(string qname, string local, string ns, string prefix)
    {
        foreach (var error in new[]
        {
            await TransformAsync($"<xsl:sequence select=\"error({qname}, 'e')\"/>").ConfigureAwait(true),
            await TransformFromQueryAsync($"<xsl:sequence select=\"error({qname}, 'e')\"/>").ConfigureAwait(true),
        })
        {
            var x = error.Should().BeOfType<XQueryException>().Subject;
            (x.ErrorCode, x.ErrorNamespaceUri, x.ErrorPrefix).Should().Be((local, ns, prefix));
        }
    }

    /// <summary>
    /// Guards: xsl:catch still matches each code by its namespace and local name, and fn:error's
    /// <c>$err:code</c> is the QName it got, with its prefix.
    /// </summary>
    [Theory]
    [InlineData("<xsl:message terminate='yes' error-code='phx:MYCODE'>m</xsl:message>", "phx:MYCODE", "MYCODE", Phx)]
    [InlineData("<xsl:message terminate='yes' error-code='q:E1'>m</xsl:message>", "q:E1", "E1", "urn:q")]
    [InlineData("<xsl:message terminate='yes' error-code='MYCODE'>m</xsl:message>", "Q{}MYCODE", "MYCODE", "")]
    [InlineData("<xsl:assert test='false()' error-code='q:E1'>a</xsl:assert>", "q:E1", "E1", "urn:q")]
    [InlineData("<xsl:sequence select=\"error(QName('urn:q', 'q:E1'), 'e')\"/>", "q:E1", "E1", "urn:q")]
    [InlineData($"<xsl:sequence select=\"error(QName('{Phx}', 'phx:XTDE9430'), 'e')\"/>", "phx:XTDE9430", "XTDE9430", Phx)]
    [InlineData($"<xsl:sequence select=\"error(QName('{Err}', 'err:FOER0001'), 'e')\"/>", "err:FOER0001", "FOER0001", Err)]
    public async Task Xsl_catch_matches_the_code(string body, string catchName, string local, string ns)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Sheet(
            $"<xsl:try>{body}<xsl:catch errors='{catchName}'><c l='{{local-name-from-QName($err:code)}}'"
            + " ns='{namespace-uri-from-QName($err:code)}'/></xsl:catch><xsl:catch><other/></xsl:catch></xsl:try>")).ConfigureAwait(true);
        var result = await transformer.TransformAsync("<r/>").ConfigureAwait(true);
        result.Should().Contain($"<c ").And.Contain($"l=\"{local}\"").And.Contain($"ns=\"{ns}\"");
    }

    [Fact]
    public async Task Fn_error_gives_err_code_with_its_prefix()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Sheet(
            "<xsl:try><xsl:sequence select=\"error(QName('urn:q', 'q:E1'), 'e')\"/>"
            + "<xsl:catch><c code='{$err:code}'/></xsl:catch></xsl:try>")).ConfigureAwait(true);
        (await transformer.TransformAsync("<r/>").ConfigureAwait(true)).Should().Contain("code=\"q:E1\"");
    }
}
