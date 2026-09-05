using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:transform's <c>global-context-item</c> option must be the focus while the invoked
/// stylesheet's global variables are evaluated (XSLT 3.0 §5.4.1).
/// </summary>
/// <remarks>
/// <para>
/// The option was read and stored, but only applied as the focus for the initial template or
/// match selection — which is established AFTER globals are built. Global initialization pushed
/// AbsentFocus unconditionally, so a stylesheet declaring
/// <c>&lt;xsl:global-context-item use="required"/&gt;</c> with a global variable
/// <c>select="."</c> raised XPDY0002 even though fn:transform had been handed the item.
/// </para>
/// <para>
/// The two cannot share one field: the initial context item belongs to the template invocation,
/// the global context item belongs to the whole transformation and exists earlier. XSpec passes
/// x:context this way whenever the context is an atomic value, which cannot travel as
/// source-node at all — which is why these tests cover atomics specifically.
/// </para>
/// </remarks>
public class GlobalContextItemViaTransformTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "phx-gci-" + Guid.NewGuid().ToString("N"));

    public GlobalContextItemViaTransformTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sut.xsl"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"\n"
            + "  xmlns:t=\"urn:t\" exclude-result-prefixes=\"#all\">\n"
            + "  <xsl:global-context-item as=\"item()\" use=\"required\"/>\n"
            + "  <xsl:variable name=\"t:gc\" as=\"item()\" select=\".\"/>\n"
            + "  <xsl:template name=\"t:get\" as=\"item()\"><xsl:sequence select=\"$t:gc\"/></xsl:template>\n"
            + "</xsl:stylesheet>");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private async Task<string> RunAsync(string globalContextItemExpr)
    {
        var driverPath = Path.Combine(_dir, "driver.xsl");
        var driver =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"\n"
            + "  xmlns:t=\"urn:t\" exclude-result-prefixes=\"#all\">\n"
            + "  <xsl:template name=\"main\"><out><xsl:value-of select=\"transform(map{\n"
            + "    'stylesheet-location':'sut.xsl',\n"
            + "    'initial-template': QName('urn:t','get'),\n"
            + "    'delivery-format':'raw',\n"
            + "    'global-context-item': " + globalContextItemExpr + "})?output\"/></out>\n"
            + "  </xsl:template>\n</xsl:stylesheet>";
        await File.WriteAllTextAsync(driverPath, driver);

        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(driver, new Uri(driverPath));
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>
    /// An atomic global context item — the case that cannot use source-node at all, and the one
    /// XSpec relies on.
    /// </summary>
    [Fact]
    public async Task AtomicGlobalContextItem_IsVisibleToGlobalVariables()
    {
        var result = await RunAsync("'context-string'");
        result.Should().Be("<out>context-string</out>");
    }

    /// <summary>A numeric item keeps its value.</summary>
    [Fact]
    public async Task NumericGlobalContextItem_IsVisibleToGlobalVariables()
    {
        var result = await RunAsync("42");
        result.Should().Be("<out>42</out>");
    }

    /// <summary>
    /// Omitting it raises XTDE3086 — the spec-defined error for "a global context item is
    /// required but none was supplied".
    /// </summary>
    /// <remarks>
    /// The fn:transform paths had no xsl:global-context-item enforcement at all, so execution
    /// continued until a global variable evaluated "." and failed as XPDY0002 — the downstream
    /// symptom rather than the condition. The two paths that DID enforce it tested only for a
    /// source document, so they could neither be satisfied by a supplied global-context-item nor
    /// complain when one was handed to a use="absent" stylesheet. All four now share one check.
    /// </remarks>
    [Fact]
    public async Task OmittingIt_RaisesXTDE3086()
    {
        var ex = await Record.ExceptionAsync(async () => await RunAsync("()"));
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTDE3086");
    }
}
