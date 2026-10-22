using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A global variable of a library package has no context item: the global context item is the
/// principal package's (XSLT 3.0 §2.3.2), so a path from the context item in it is XPDY0002. It
/// read the principal's source document (W3C package-912).
/// </summary>
public sealed class LibraryGlobalContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxlibctx-" + Guid.NewGuid().ToString("N"));

    public LibraryGlobalContextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string librarySelect, string principalVariable = "")
    {
        var library = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(library, $"""
            <xsl:package name="urn:lib" package-version="1.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="var" visibility="public" select="{librarySelect}"/>
            </xsl:package>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:package package-version="1.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode/>
              <xsl:use-package name="urn:lib" package-version="*"/>
              {principalVariable}
              <xsl:template match="/"><out value="{"{$var}"}"/></xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null,
            new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = [("1.0", library)] });
        return await t.TransformAsync("<doc><a/><b/></doc>");
    }

    [Fact]
    public async Task A_library_global_that_reads_the_context_item_is_XPDY0002()
    {
        var act = () => RunAsync("count(//*)");
        (await act.Should().ThrowAsync<PhoenixmlDb.XQuery.Execution.XQueryRuntimeException>()).Which.ErrorCode.Should().Be("XPDY0002");
    }

    [Fact]
    public async Task A_library_global_without_the_context_item_is_evaluated()
        => (await RunAsync("count((1, 2, 3))")).Should().Contain("value=\"3\"");
}
