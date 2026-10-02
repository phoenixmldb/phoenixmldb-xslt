using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// indent="yes" writes LF line ends on every OS. It wrote Environment.NewLine (CRLF on Windows).
/// Only the Windows CI job can fail this without the fix; on Linux both are LF.
/// </summary>
public sealed class IndentNewlineTests
{
    [Fact]
    public async Task Indented_output_uses_LF_on_every_OS()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:output indent='yes'/><xsl:template match='/'><a><b/><c><d/></c></a></xsl:template></xsl:stylesheet>");
        var output = await t.TransformAsync("<x/>");
        output.Should().Contain("\n").And.NotContain("\r");
    }
}
