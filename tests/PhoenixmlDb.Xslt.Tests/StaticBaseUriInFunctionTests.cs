using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:static-base-uri()</c> inside an <c>xsl:function</c> must resolve against the module the
/// function is written in, not the principal stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// The static base URI comes from the expression's static context (XPath 3.1 §16.2.4), which in
/// XSLT is the base URI of the containing element. The runtime keeps a stack for this and falls
/// back to the principal stylesheet whenever the stack is empty. <c>xsl:template</c> pushed its
/// module's URI; <c>xsl:function</c> carried no BaseUri at all and pushed nothing, so a function
/// in an imported module silently reported the importing stylesheet's URI — the same
/// one-of-a-pair-has-the-fix shape seen repeatedly in this engine.
/// </para>
/// <para>
/// Found through XSpec's issue-746 suite, where a helper module calls
/// <c>transform(map{'stylesheet-location': static-base-uri(), ...})</c> to run itself. Getting
/// the principal stylesheet's URI made fn:transform load the wrong document, which failed as
/// XTSE0150 ("a literal result element used as a simplified stylesheet module must have an
/// xsl:version attribute") — an error naming neither static-base-uri nor the real module.
/// </para>
/// </remarks>
public class StaticBaseUriInFunctionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "phx-sbu-" + Guid.NewGuid().ToString("N"));

    public StaticBaseUriInFunctionTests() => Directory.CreateDirectory(_dir);

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

    private const string Imported =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<xsl:transform xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\"\n"
        + "  xmlns:mf=\"helper\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">\n"
        + "  <xsl:function name=\"mf:where\" as=\"xs:string\">\n"
        + "    <xsl:sequence select=\"static-base-uri()\"/>\n"
        + "  </xsl:function>\n"
        + "  <xsl:template name=\"tmpl-where\" as=\"xs:string\">\n"
        + "    <xsl:sequence select=\"static-base-uri()\"/>\n"
        + "  </xsl:template>\n"
        + "</xsl:transform>";

    private const string Principal =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\"\n"
        + "  xmlns:mf=\"helper\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">\n"
        + "  <xsl:import href=\"imported.xsl\"/>\n"
        + "  <xsl:template name=\"main\"><out>\n"
        + "    <fn><xsl:value-of select=\"replace(mf:where(),'.*/','')\"/></fn>\n"
        + "    <tmpl><xsl:variable name=\"t\" as=\"xs:string\">"
        + "<xsl:call-template name=\"tmpl-where\"/></xsl:variable>"
        + "<xsl:value-of select=\"replace($t,'.*/','')\"/></tmpl>\n"
        + "    <principal><xsl:value-of select=\"replace(static-base-uri(),'.*/','')\"/></principal>\n"
        + "  </out></xsl:template>\n"
        + "</xsl:stylesheet>";

    private async Task<string> RunAsync()
    {
        var principalPath = Path.Combine(_dir, "principal.xsl");
        await File.WriteAllTextAsync(Path.Combine(_dir, "imported.xsl"), Imported);
        await File.WriteAllTextAsync(principalPath, Principal);

        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(Principal, new Uri(principalPath));
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>The defect: a function in an imported module reported the principal's URI.</summary>
    [Fact]
    public async Task Function_InImportedModule_ReportsThatModule()
    {
        var result = await RunAsync();
        result.Should().Contain("<fn>imported.xsl</fn>");
    }

    /// <summary>The template twin, which already worked — kept as the reference to match.</summary>
    [Fact]
    public async Task Template_InImportedModule_StillReportsThatModule()
    {
        var result = await RunAsync();
        result.Should().Contain("<tmpl>imported.xsl</tmpl>");
    }

    /// <summary>An expression in the principal stylesheet still reports the principal.</summary>
    [Fact]
    public async Task Expression_InPrincipalStylesheet_ReportsThePrincipal()
    {
        var result = await RunAsync();
        result.Should().Contain("<principal>principal.xsl</principal>");
    }

    /// <summary>
    /// Both contexts in the same module agree — the point of the fix is that they stop
    /// disagreeing.
    /// </summary>
    [Fact]
    public async Task FunctionAndTemplate_InTheSameModule_Agree()
    {
        var result = await RunAsync();
        result.Should().Contain("<fn>imported.xsl</fn>").And.Contain("<tmpl>imported.xsl</tmpl>");
    }
}
