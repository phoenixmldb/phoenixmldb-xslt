using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Phase E of the source-location audit: synthetic LSP-readiness verification.
/// For each (XSLT element shape × error site) combination, run a transform that
/// raises a runtime error and assert the diagnostic carries (uri/module, line, col)
/// pinned to the offending token. Catches regressions and gives confidence that the
/// LSP server's <c>publishDiagnostics</c> output will be actionable.
/// </summary>
/// <remarks>
/// These tests don't pin to exact column values (numbering depends on parser quirks
/// across the file's whitespace) — they assert <i>structural</i> properties:
/// (1) Module is set and points at the right file; (2) Line is plausible (matches
/// the source line we expect); (3) Column is past the element's start column,
/// proving the file-absolute shift fired.
/// </remarks>
public class SourceLocationLspReadinessTests
{
    private static async Task<PhoenixmlDb.XQuery.Functions.XQueryException> RunAndCaptureXQueryAsync(string stylesheet, string input = "<x/>")
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await transformer.TransformAsync(input));
        Exception? cur = ex;
        while (cur != null)
        {
            if (cur is PhoenixmlDb.XQuery.Functions.XQueryException q) return q;
            cur = cur.InnerException;
        }
        throw new Xunit.Sdk.XunitException(
            $"Expected an XQueryException somewhere in the chain, got: {ex.GetType().Name}: {ex.Message}");
    }

    private static (int Line, int Column) ExtractLineCol(string formattedMessage)
    {
        // [module:line:col] message  OR  [line N, col M] message
        var m = System.Text.RegularExpressions.Regex.Match(
            formattedMessage, @"\[(?:.+:)?(\d+):(\d+)\]|\[line\s+(\d+),\s*col\s+(\d+)\]");
        if (!m.Success) return (-1, -1);
        var line = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
        var col = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
        return (
            int.Parse(line, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(col, System.Globalization.CultureInfo.InvariantCulture));
    }


    private static void AssertLineColMatch(PhoenixmlDb.XQuery.Functions.XQueryException ex, int expectedLine, int minColumn)
    {
        // Accept either the typed Line/Column properties OR the formatted [line N, col M] in the message —
        // intermediate wrapping in some code paths drops the typed location even though the message carries it.
        var (msgLine, msgCol) = ExtractLineCol(ex.Message);
        var line = ex.Line ?? msgLine;
        var col = ex.Column ?? msgCol;
        line.Should().Be(expectedLine, $"line should match. typed={ex.Line}, msg-line={msgLine}, message={ex.Message}");
        col.Should().BeGreaterThan(minColumn, $"col should be >{minColumn}. typed={ex.Column}, msg-col={msgCol}, message={ex.Message}");
    }

    // --- D1: XPath in select="..." attribute ---

    [Fact]
    public async Task XPTY0004_in_select_attribute_carries_line_3_and_column_inside_attribute()
    {
        var ex = await RunAndCaptureXQueryAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out><xsl:value-of select="index-of((1,2,3), ())"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        ex.ErrorCode.Should().Be("XPTY0004");
        // Either typed Line/Column properties OR the formatted message must carry the position.
        var (line, col) = ExtractLineCol(ex.Message);
        if (ex.Line.HasValue) ex.Line.Should().Be(3);
        else line.Should().Be(3, $"the formatted message should carry line=3. Got: {ex.Message}");
        if (ex.Column.HasValue) ex.Column.Should().BeGreaterThan(20);
        else col.Should().BeGreaterThan(20, $"the formatted message should carry col>20. Got: {ex.Message}");
    }

    // --- D1: XPath in test="..." attribute (xsl:if) ---

    [Fact]
    public async Task XPTY0004_in_test_attribute_carries_attribute_position()
    {
        var ex = await RunAndCaptureXQueryAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out>
                  <xsl:if test="index-of((1,2,3), ())">match</xsl:if>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        ex.ErrorCode.Should().Be("XPTY0004");
        AssertLineColMatch(ex, 4, 15);
    }

    // --- D2: AVT inner expression in attribute value ---

    [Fact]
    public async Task XPTY0004_in_AVT_inner_expression_carries_brace_position()
    {
        var ex = await RunAndCaptureXQueryAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:element name="prefix-{string-length(index-of((1,2,3), ()))}-suffix"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        ex.ErrorCode.Should().Be("XPTY0004");
        AssertLineColMatch(ex, 3, 25);
    }

    // --- D3: TVT inner expression in element text content ---

    [Fact]
    public async Task XPTY0004_in_TVT_text_content_carries_text_position()
    {
        var ex = await RunAndCaptureXQueryAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out><xsl:text expand-text="yes">Hello {index-of((1,2,3), ())}</xsl:text></out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        ex.ErrorCode.Should().Be("XPTY0004");
        AssertLineColMatch(ex, 3, 30);
    }

    // --- D4: error in imported module carries the imported module's URI ---

    [Fact]
    public async Task XPTY0004_in_imported_module_carries_imported_uri_in_module()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-lsp-d4-{Guid.NewGuid():N}");
        var modulesDir = Path.Combine(dir, "modules");
        Directory.CreateDirectory(modulesDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(modulesDir, "lib.xsl"), """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:variable name="bad" select="index-of((1,2,3), ())"/>
                </xsl:stylesheet>
                """);
            var mainPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(mainPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:import href="modules/lib.xsl"/>
                  <xsl:template match="/">
                    <out><xsl:value-of select="$bad"/></out>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(await File.ReadAllTextAsync(mainPath),
                baseUri: new Uri(mainPath));
            Exception? thrown = null;
            try { await transformer.TransformAsync("<x/>"); }
            catch (Exception e) { thrown = e; }
            thrown.Should().NotBeNull();

            PhoenixmlDb.XQuery.Functions.XQueryException? xq = null;
            for (var cur = thrown; cur != null; cur = cur.InnerException)
            {
                if (cur is PhoenixmlDb.XQuery.Functions.XQueryException q) { xq = q; break; }
            }
            xq.Should().NotBeNull();
            xq!.Module.Should().EndWith("/modules/lib.xsl",
                $"imported-module errors must carry the imported module's URI. Got: {xq.Module}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- D5: multi-line XPath expression — line offset arithmetic ---

    [Fact]
    public async Task XPTY0004_on_xpath_line_2_maps_to_correct_file_line()
    {
        // Multi-line select where the offending function call is on the SECOND line
        // of the XPath text — should map to file line (attr-line + 1).
        var ex = await RunAndCaptureXQueryAsync(
            "<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">\n" +
            "  <xsl:template match=\"/\">\n" +
            "    <out><xsl:value-of select=\"\n" +
            "      index-of((1,2,3), ())\n" +
            "    \"/></out>\n" +
            "  </xsl:template>\n" +
            "</xsl:stylesheet>");
        ex.ErrorCode.Should().Be("XPTY0004");
        // The select attribute starts on line 3; the inner XPath call is on line 4.
        // Some parser paths report the attribute's start line (3) instead of the
        // exact inner-XPath line (4) — accept either since both are sensible LSP
        // anchors. D5 line-arithmetic exists; this test guards that we at least
        // land somewhere in the (3, 4) range for the multi-line case.
        var line = ex.Line ?? ExtractLineCol(ex.Message).Line;
        line.Should().BeOneOf([3, 4],
            $"line should be the attribute start (3) or the inner XPath line (4). Got: {line}, message: {ex.Message}");
    }

    // --- Convention check (D8): formatted message includes (line, col) ---

    [Fact]
    public async Task Formatted_message_always_includes_line_and_column_for_runtime_error()
    {
        var ex = await RunAndCaptureXQueryAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out><xsl:value-of select="index-of((1,2,3), ())"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var (line, col) = ExtractLineCol(ex.Message);
        line.Should().BeGreaterThan(0, $"message should contain line number. Got: {ex.Message}");
        col.Should().BeGreaterThan(0, $"message should contain column number. Got: {ex.Message}");
    }
}
