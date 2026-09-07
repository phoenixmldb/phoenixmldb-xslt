using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:result-document/@href</c> must accept every valid URI form, including RFC 8089 §2's
/// minimal <c>file:/path</c> (a file URI with no authority component).
/// </summary>
/// <remarks>
/// <para>
/// The XTDE1500 read/write-overlap check combined the href against the stylesheet base URI with
/// the throwing <c>Uri</c> constructor and converted ANY parse failure into XTDE1400. .NET
/// rejects <c>file:/path</c> as an absolute URI — it wants <c>file://</c> — so combining it threw
/// "The Authority/Host could not be parsed" and the transform aborted on a URI that is valid and
/// that Saxon accepts.
/// </para>
/// <para>
/// That check is overlap DETECTION, not a validity gate, so it now parses with TryCreate
/// throughout and reports XTDE1400 only when the href cannot be parsed as a URI reference at
/// all. XSpec's xsl-result-document suite writes to <c>file:/dev/null</c> because that is the
/// portable way to discard a result document.
/// </para>
/// </remarks>
public class ResultDocumentHrefUriTests
{
    private static async Task<(Exception? Error, IReadOnlyDictionary<string, string> Secondary)>
        RunAsync(string href)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\"><xsl:template name=\"main\">"
            + "<xsl:result-document href=\"" + href + "\"><d/></xsl:result-document>"
            + "<ok/></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        var error = await Record.ExceptionAsync(async () =>
        {
            await t.LoadStylesheetAsync(xsl);
            t.SetInitialTemplate("main");
            await t.TransformAsync((string?)null);
        });
        return (error, t.SecondaryResultDocuments);
    }

    /// <summary>
    /// The defect: RFC 8089's minimal no-authority form was rejected as an invalid URI and
    /// aborted the transform.
    /// </summary>
    [Fact]
    public async Task NoAuthorityFileUri_IsAccepted()
    {
        var (error, _) = await RunAsync("file:/dev/null");
        error.Should().BeNull();
    }

    /// <summary>The result document is still produced under the href it was given.</summary>
    [Fact]
    public async Task NoAuthorityFileUri_StillProducesTheResultDocument()
    {
        var (error, secondary) = await RunAsync("file:/dev/null");
        error.Should().BeNull();
        secondary.Should().ContainKey("file:/dev/null");
    }

    /// <summary>The forms that already worked must keep working.</summary>
    [Theory]
    [InlineData("file:///dev/null")]
    [InlineData("out.xml")]
    [InlineData("sub/out.xml")]
    [InlineData("file://localhost/tmp/out.xml")]
    public async Task OtherValidHrefForms_AreAccepted(string href)
    {
        var (error, _) = await RunAsync(href);
        error.Should().BeNull();
    }
}
