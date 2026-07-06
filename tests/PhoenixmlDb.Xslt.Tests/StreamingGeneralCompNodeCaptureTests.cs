using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Node-capturing streaming watcher for general-comparison tails that NAVIGATE the
/// matched context node's attribute inside a compile-time numeric op — the
/// sx-GeneralComp-*-016/116/020 shapes: <c>(account/transaction/(@value*2)) = 8.64</c>,
/// <c>(account/transaction/abs(@value)) = 4.32</c>. Before the fix the watcher captured
/// decoded strings, so the <c>@attr</c>-navigating tail fell to synthetic-empty eval and
/// produced a wrong <c>false</c>. The fix materializes a childless element carrying the
/// matched attributes and applies the known numeric op cheaply per node.
/// </summary>
public class StreamingGeneralCompNodeCaptureTests
{
    private static async Task<string> Run(string select)
    {
        var input = """
            <account nr="1">
              <account-number>01234567</account-number>
              <transaction value="0.01"/>
              <transaction value="3.06"/>
              <transaction value="4.32"/>
              <transaction value="-1.84"/>
            </account>
            """;
        var sheet = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="t.xml">
                  <out><xsl:value-of select="{{select}}"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-gc-nodecap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "t.xml"), input);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(sheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return (await t.TransformAsync((string?)null)).Trim();
        }
        finally { Directory.Delete(dir, true); }
    }

    // sx-gc-eq-016 shape: (account/transaction/(@value*2)) = 8.64 -> @value=4.32 present -> true.
    [Fact]
    public async Task ArithmeticAttributeTail_Equal_True()
        => (await Run("(account/transaction/(@value*2)) = 8.64")).Should().Be("<out>true</out>");

    // No @value doubles to 8.64 other than 4.32; a value not present -> false.
    [Fact]
    public async Task ArithmeticAttributeTail_Equal_False()
        => (await Run("(account/transaction/(@value*2)) = 99.0")).Should().Be("<out>false</out>");

    // sx-gc-gt-016 family: some (@value*2) > 8 (e.g. 4.32*2=8.64) -> true.
    [Fact]
    public async Task ArithmeticAttributeTail_GreaterThan_True()
        => (await Run("(account/transaction/(@value*2)) > 8.0")).Should().Be("<out>true</out>");

    // sx-gc-eq-020 shape: (account/transaction/abs(@value)) = 4.32 -> true (4.32 present).
    [Fact]
    public async Task AbsAttributeTail_Equal_True()
        => (await Run("(account/transaction/abs(@value)) = 4.32")).Should().Be("<out>true</out>");

    // abs picks up negative magnitudes: abs(-1.84)=1.84 -> true.
    [Fact]
    public async Task AbsAttributeTail_Equal_NegativeMagnitude_True()
        => (await Run("(account/transaction/abs(@value)) = 1.84")).Should().Be("<out>true</out>");

    // Bare @value (sx-gc-eq-018, already-passing) must remain correct alongside the new path.
    [Fact]
    public async Task BareAttribute_Equal_True()
        => (await Run("(account/transaction/@value) = 4.32")).Should().Be("<out>true</out>");
}
