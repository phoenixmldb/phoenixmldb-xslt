using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A reserved namespace named in [xsl:]extension-element-prefixes is XTSE0085; an element in it
/// used as an extension instruction is XTSE0800 (XSLT 3.0 §3.3.1, §24.2). Both were XTSE0800.
/// Where a stylesheet does both, the use is reported (W3C extension-functions-0105); where it
/// only names the namespace, XTSE0085 (math-3702). Saxon reports both errors.
/// </summary>
public sealed class ReservedExtensionNamespaceTests
{
    private static async Task<string> CodeOf(string stylesheet)
    {
        var t = new XsltTransformer();
        var act = () => t.LoadStylesheetAsync(stylesheet);
        return (await act.Should().ThrowAsync<Exception>()).Which.Message[..8];
    }

    private const string Xsl = "xmlns:xsl='http://www.w3.org/1999/XSL/Transform' xmlns:xs='http://www.w3.org/2001/XMLSchema'";

    [Theory]
    [InlineData($"<xsl:stylesheet version='3.0' {Xsl} extension-element-prefixes='xs'><xsl:template match='/'><out/></xsl:template></xsl:stylesheet>", "XTSE0085")]
    [InlineData($"<xsl:stylesheet version='3.0' {Xsl} extension-element-prefixes='xs'><xsl:template match='/'><out><xs:special/></out></xsl:template></xsl:stylesheet>", "XTSE0800")]
    [InlineData($"<xsl:stylesheet version='3.0' {Xsl} extension-element-prefixes='xsl'><xsl:template match='/'><out/></xsl:template></xsl:stylesheet>", "XTSE0085")]
    [InlineData($"<xsl:stylesheet version='3.0' {Xsl}><xsl:template match='/'><out xsl:extension-element-prefixes='xs'/></xsl:template></xsl:stylesheet>", "XTSE0085")]
    [InlineData($"<xsl:stylesheet version='3.0' {Xsl}><xsl:template match='/'><out xsl:extension-element-prefixes='xs'><xs:special/></out></xsl:template></xsl:stylesheet>", "XTSE0800")]
    public async Task The_code_says_whether_the_namespace_is_named_or_used(string stylesheet, string code)
        => (await CodeOf(stylesheet)).Should().Be(code);
}
