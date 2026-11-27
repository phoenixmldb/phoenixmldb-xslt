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

    private static string Sheet(string body, string declarations = "") =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'"
        + $" xmlns:phx='{Phx}' xmlns:q='urn:q' xmlns:err='{Err}'>"
        + $"{declarations}<xsl:template match='/'>{body}</xsl:template></xsl:stylesheet>";

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
        // error-code is an attribute value template: the braces of an EQName are doubled.
        { $"<xsl:message terminate='yes' error-code='Q{{{{{Phx}}}}}XTDE9430'>m</xsl:message>", "phx:XTDE9430" },
        { "<xsl:message terminate='yes' error-code='phx:MYCODE'>m</xsl:message>", "phx:MYCODE" },
        { "<xsl:message terminate='yes' error-code='q:E1'>m</xsl:message>", "Q{urn:q}E1" },
        { "<xsl:message terminate='yes' error-code='Q{{urn:q}}E1'>m</xsl:message>", "Q{urn:q}E1" },
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

    /// <summary>
    /// XSLT 3.0 §2.3.2 lets a dynamic error of a global variable wait until the variable is
    /// referenced. The engine defers an error whose code is not a static one; with no code (null)
    /// it did not, so an xsl:message terminate="yes" with a code outside the error namespace in a
    /// global variable that is never referenced stopped the transformation, and the same
    /// instruction with the default code did not. Now one rule holds for every code.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" error-code='err:FOER0000'")]
    [InlineData(" error-code='q:E1'")]
    [InlineData(" error-code='phx:MYCODE'")]
    [InlineData(" error-code='MYCODE'")]
    public async Task A_global_variable_that_is_never_referenced_does_not_stop_the_transformation(string errorCode)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Sheet("<done/>",
            $"<xsl:variable name='g'><xsl:message terminate='yes'{errorCode}>g</xsl:message></xsl:variable>")).ConfigureAwait(true);
        (await transformer.TransformAsync("<r/>").ConfigureAwait(true)).Should().Contain("<done");
    }

    /// <summary>Guard: a reference to the variable raises the error, with its code.</summary>
    [Theory]
    [InlineData(" error-code='q:E1'", "Q{urn:q}E1")]
    [InlineData(" error-code='phx:MYCODE'", "phx:MYCODE")]
    [InlineData("", "XTMM9000")]
    public async Task A_referenced_global_variable_raises_the_code(string errorCode, string code)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Sheet("<xsl:value-of select='$g'/>",
            $"<xsl:variable name='g'><xsl:message terminate='yes'{errorCode}>g</xsl:message></xsl:variable>")).ConfigureAwait(true);
        var error = await Record.ExceptionAsync(() => transformer.TransformAsync("<r/>")).ConfigureAwait(true);
        error.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be(code);
    }

    /// <summary>
    /// An error that the engine raises with a code of this processor, phx:XTDE9430 (an
    /// xsl:source-document that cannot be streamed, under StrictStreamability). xsl:try read its
    /// message with its own reader, which did not read phx:, so the code was err:XSLT0000: a catch
    /// by name did not match, and a catch-all saw err:XSLT0000.
    /// </summary>
    [Theory]
    [InlineData("phx:XTDE9430")]
    [InlineData("*:XTDE9430")]
    [InlineData($"Q{{{Phx}}}XTDE9430")]
    [InlineData("phx:*")]
    [InlineData("")]
    public async Task Xsl_catch_matches_an_engine_phx_code(string catchName)
    {
        var dir = Directory.CreateTempSubdirectory("phoenixml-phx-code-");
        try
        {
            var input = Path.Combine(dir.FullName, "b.xml");
            await File.WriteAllTextAsync(input, "<B><I><P>1</P></I><I><P>2</P></I></B>").ConfigureAwait(true);
            var errors = catchName.Length == 0 ? "" : $" errors='{catchName}'";
            var transformer = new XsltTransformer { StrictStreamability = true };
            await transformer.LoadStylesheetAsync(Sheet(
                $"<xsl:try><xsl:source-document streamable='yes' href='{new Uri(input).AbsoluteUri}'>"
                + "<out><xsl:copy-of select='round(avg(./B/I[position() lt 4]/P), 2)'/></out></xsl:source-document>"
                + $"<xsl:catch{errors}><c code='{{$err:code}}' l='{{local-name-from-QName($err:code)}}'"
                + " ns='{namespace-uri-from-QName($err:code)}'/></xsl:catch><xsl:catch><other code='{$err:code}'/></xsl:catch></xsl:try>")).ConfigureAwait(true);
            var result = await transformer.TransformAsync("<r/>").ConfigureAwait(true);
            result.Should().Contain("<c ").And.Contain("code=\"phx:XTDE9430\"").And.Contain("l=\"XTDE9430\"").And.Contain($"ns=\"{Phx}\"");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Guard: the same transformation outside xsl:try still raises phx:XTDE9430.</summary>
    [Fact]
    public async Task The_engine_phx_code_reaches_the_host()
    {
        var dir = Directory.CreateTempSubdirectory("phoenixml-phx-code-");
        try
        {
            var input = Path.Combine(dir.FullName, "b.xml");
            await File.WriteAllTextAsync(input, "<B><I><P>1</P></I></B>").ConfigureAwait(true);
            var transformer = new XsltTransformer { StrictStreamability = true };
            await transformer.LoadStylesheetAsync(Sheet(
                $"<xsl:source-document streamable='yes' href='{new Uri(input).AbsoluteUri}'>"
                + "<out><xsl:copy-of select='round(avg(./B/I[position() lt 4]/P), 2)'/></out></xsl:source-document>")).ConfigureAwait(true);
            var error = await Record.ExceptionAsync(() => transformer.TransformAsync("<r/>")).ConfigureAwait(true);
            error.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be("phx:XTDE9430");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// ErrorCode and xsl:try read a code of this processor at the start of a message with one
    /// reader (<c>XsltException.ReadPhxCode</c>), so they agree on every message, also with '-' and
    /// '.' in the local name.
    /// </summary>
    [Theory]
    [InlineData("phx:MY-CODE.v2: m", "MY-CODE.v2")]
    [InlineData("phx:MY-CODE.v2 m", "MY-CODE.v2")]
    [InlineData("phx:MY-CODE.v2", "MY-CODE.v2")]
    [InlineData("phx:XTDE9430: The document of xsl:source-document cannot be streamed", "XTDE9430")]
    [InlineData("phx:-CODE: the local name does not start with a letter", null)]
    [InlineData("phx:: no local name", null)]
    [InlineData("phx:CODE!: no delimiter after the local name", null)]
    public void ErrorCode_and_xsl_try_read_a_phx_code_alike(string message, string? local)
    {
        new XsltException(message).ErrorCode.Should().Be(local is null ? null : "phx:" + local);
        DefaultXsltExecutionContext.ExtractErrorCode(message).Should().Be(local is null ? null : $"Q{{{Phx}}}{local}");
    }

    /// <summary>A phx code with '-' and '.' in its local name is the same code as ErrorCode and as $err:code.</summary>
    [Fact]
    public async Task A_phx_code_with_a_hyphen_and_a_dot_is_the_same_on_both_routes()
    {
        const string body = "<xsl:message terminate='yes' error-code='phx:MY-CODE.v2'>m</xsl:message>";
        var error = await TransformAsync(body).ConfigureAwait(true);
        error.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be("phx:MY-CODE.v2");

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Sheet(
            $"<xsl:try>{body}<xsl:catch errors='phx:MY-CODE.v2'><c l='{{local-name-from-QName($err:code)}}'"
            + " ns='{namespace-uri-from-QName($err:code)}'/></xsl:catch><xsl:catch><other/></xsl:catch></xsl:try>")).ConfigureAwait(true);
        var result = await transformer.TransformAsync("<r/>").ConfigureAwait(true);
        result.Should().Contain("<c ").And.Contain("l=\"MY-CODE.v2\"").And.Contain($"ns=\"{Phx}\"");
    }

    /// <summary>
    /// ErrorCode and xsl:try read a code written as <c>Q{uri}local:</c> with one reader
    /// (<c>XsltException.TryReadExpandedCode</c>), which checks that the local name is an NCName.
    /// ErrorCode gives the err namespace as the local name and the phx namespace as phx:local;
    /// xsl:try gives every namespace as Q{uri}local, which its catch matches by URI and local name.
    /// </summary>
    [Theory]
    [InlineData("Q{urn:q}E1: Transformation terminated: m", "urn:q", "E1", "Q{urn:q}E1")]
    [InlineData("Q{}MYCODE: Transformation terminated: m", "", "MYCODE", "Q{}MYCODE")]
    [InlineData($"Q{{{Err}}}FOER0000: Transformation terminated: m", Err, "FOER0000", "FOER0000")]
    [InlineData($"Q{{{Phx}}}XTDE9430: Assertion failed", Phx, "XTDE9430", "phx:XTDE9430")]
    [InlineData("Q{urn:q}1E: the local name is not an NCName", null, null, null)]
    [InlineData("Q{urn:q: no closing brace", null, null, null)]
    public void ErrorCode_and_xsl_try_read_an_expanded_code_alike(string message, string? ns, string? local, string? errorCode)
    {
        new XsltException(message).ErrorCode.Should().Be(errorCode);
        DefaultXsltExecutionContext.ExtractErrorCode(message).Should().Be(local is null ? null : $"Q{{{ns}}}{local}");
    }
}
