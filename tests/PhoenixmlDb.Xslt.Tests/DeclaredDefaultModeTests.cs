using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// With declared-modes="yes" (the default for xsl:package), the mode a default-mode attribute
/// names must be declared: XTSE3085 (XSLT 3.0 §6.6.2). It was not counted as a use, so the
/// package compiled and the run failed with XTDE0044 or XTDE0045 (W3C package-914d/e).
/// </summary>
public sealed class DeclaredDefaultModeTests
{
    private static Task LoadAsync(string root, string attributes, string body = "")
        => new XsltTransformer().LoadStylesheetAsync($"""
            <xsl:{root} xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" {attributes}>{body}
              <xsl:template name="xsl:initial-template" visibility="public"><out/></xsl:template>
            </xsl:{root}>
            """);

    [Theory]
    [InlineData("package", """default-mode="m" """)]
    [InlineData("stylesheet", """default-mode="m" declared-modes="yes" """)]
    public async Task An_undeclared_default_mode_is_XTSE3085(string root, string attributes)
    {
        var act = () => LoadAsync(root, attributes);
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTSE3085*");
    }

    [Theory]
    [InlineData("package", """default-mode="m" """, """<xsl:mode name="m"/>""")]
    [InlineData("package", """default-mode="#unnamed" """, "")]
    [InlineData("stylesheet", """default-mode="m" """, "")]
    public async Task A_declared_default_mode_or_no_declared_modes_compiles(string root, string attributes, string body)
    {
        var act = () => LoadAsync(root, attributes, body);
        await act.Should().NotThrowAsync();
    }
}
