using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A failed XPath parse must name the expression that failed and where it sits in the
/// stylesheet.
/// </summary>
/// <remarks>
/// The raw parser message is written for someone looking at one expression in isolation: ANTLR
/// reports "mismatched input '&lt;EOF&gt;' expecting {...}" followed by every token the grammar
/// would accept — well over a hundred of them. Against generated XSLT that is a single line
/// hundreds of kilobytes wide, that message identifies neither the attribute nor the offset, and
/// the expected-token list buries the part that matters. Locating the failure meant bisecting
/// the stylesheet by hand.
/// </remarks>
public class XPathParseDiagnosticTests
{
    private static async Task<Exception> CaptureAsync(string stylesheet)
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        var caught = await Record.ExceptionAsync(async () =>
        {
            await t.LoadStylesheetAsync(stylesheet);
            t.SetInitialTemplate("main");
            await t.TransformAsync((string?)null);
        });
        caught.Should().NotBeNull("the stylesheet contains an unparsable XPath");
        return caught!;
    }

    /// <summary>
    /// A bare "$" — the exact shape XSpec's generated code produced — reports the expression
    /// text and the owning attribute rather than only the grammar's expected-token list.
    /// </summary>
    [Fact]
    public async Task ParseFailure_NamesTheExpressionAndItsAttribute()
    {
        var ex = await CaptureAsync(
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:template name=\"main\"><xsl:value-of select=\"$\"/></xsl:template>"
            + "</xsl:stylesheet>");

        ex.Message.Should().Contain("value-of/@select");
        ex.Message.Should().Contain("parsing");
    }

    /// <summary>
    /// The expression text itself is present, so the reader can see what was actually parsed
    /// without opening the file.
    /// </summary>
    [Fact]
    public async Task ParseFailure_IncludesTheExpressionText()
    {
        var ex = await CaptureAsync(
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:template name=\"main\"><xsl:value-of select=\"1 +\"/></xsl:template>"
            + "</xsl:stylesheet>");

        ex.Message.Should().Contain("1 +");
    }

    /// <summary>
    /// The spec-mandated error code still leads the message — the decoration is appended, not
    /// substituted, so harnesses matching on XPST0003 are unaffected.
    /// </summary>
    [Fact]
    public async Task ParseFailure_StillCarriesTheStaticErrorCode()
    {
        var ex = await CaptureAsync(
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:template name=\"main\"><xsl:value-of select=\"$\"/></xsl:template>"
            + "</xsl:stylesheet>");

        ex.Message.Should().Contain("XPST0003");
    }
}
