using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A shadow attribute inside an IMPORTED module must resolve against the static parameters
/// the caller supplied, not just that module's own defaults.
///
/// LoadExternalStylesheet called ResolveShadowAttributes with no parameters, so an imported
/// module never saw them. W3C copy-0617..0627 drive a shared stylesheet through xsl:import
/// with _inherit-namespaces="{$INHERIT}" in the imported file; INHERIT=false never arrived
/// and namespaces were inherited when the tests require they must not be.
/// </summary>
public class ShadowAttributeImportTests
{
    private static async Task<string> RunWithImport(string? inheritParam)
    {
        var dir = Path.Combine(Path.GetTempPath(), "phx-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The imported module declares the static param AND carries the shadow attribute.
            // Both halves matter: a test where the shadow lives in the IMPORTING module passes
            // even with the bug present, because that path always received the parameters.
            //
            // The shadow drives the element NAME, which is unambiguous. An earlier version of
            // this test drove _inherit-namespaces and asserted on the child's in-scope
            // namespaces — which measured a DIFFERENT and still-open gap (inherit-namespaces
            // appears not to be honoured on xsl:element; the corpus cases that this fix cleared
            // use xsl:copy) and so could not see the propagation it was meant to test.
            await File.WriteAllTextAsync(Path.Combine(dir, "base.xsl"), """
                <?xml version="1.0" encoding="utf-8"?>
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                  <xsl:param name="FLAG" static="yes" select="'fromDefault'"/>
                  <xsl:template name="main">
                    <out><xsl:element _name="{$FLAG}">x</xsl:element></out>
                  </xsl:template>
                </xsl:stylesheet>
                """);
            var main = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(main, """
                <?xml version="1.0" encoding="utf-8"?>
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                  <xsl:import href="base.xsl"/>
                  <xsl:output method="xml" indent="no"/>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            var sp = inheritParam is null ? null : new Dictionary<string, string> { ["FLAG"] = inheritParam };
            await transformer.LoadStylesheetAsync(await File.ReadAllTextAsync(main), new Uri(main), sp, null);
            transformer.SetInitialTemplate("main", null);
            return await transformer.TransformAsync("<dummy/>");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// With the caller passing "no", the element must NOT inherit the importing module's
    /// namespace. Before the fix the imported module used its own default of "yes" and the
    /// keep namespace leaked onto the element.
    /// </summary>
    [Fact]
    public async Task Caller_static_param_reaches_a_shadow_attribute_in_an_imported_module()
        => (await RunWithImport("'fromCaller'")).Should().Contain("<fromCaller>");

    /// <summary>The imported module's own default still applies when the caller supplies nothing.</summary>
    [Fact]
    public async Task Imported_module_default_is_used_when_no_param_is_supplied()
        => (await RunWithImport(null)).Should().Contain("<fromDefault>");

    /// <summary>An unquoted value works too — the CLI spelling.</summary>
    [Fact]
    public async Task Caller_param_may_be_unquoted()
        => (await RunWithImport("fromCaller")).Should().Contain("<fromCaller>");
}
