using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Ast;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XsltTransformer.AllowedOutputMethods restricts the methods results are DELIVERED in, judged on
/// the method actually used: the default-method rule and a run-time result-document method
/// included. Each rejected case below declares no disallowed method anywhere, so a check of the
/// declarations alone would pass it. OutputDeclarations is the static, read-only view.
/// </summary>
public class OutputMethodPolicyTests
{
    private static readonly HashSet<OutputMethod> XmlOnly = [OutputMethod.Xml];

    private static async Task<XsltTransformer> Load(string ss, IReadOnlySet<OutputMethod>? allowed)
    {
        var t = new XsltTransformer { AllowedOutputMethods = allowed };
        await t.LoadStylesheetAsync(ss);
        return t;
    }

    [Fact]
    public async Task Html_document_element_with_no_declared_method_is_rejected_when_only_xml_is_allowed()
    {
        // No xsl:output at all: the declared (effective) method is xml, but the default-method
        // rule serializes an <html> document element with the html method.
        var t = await Load("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/"><html><body>x</body></html></xsl:template>
            </xsl:stylesheet>
            """, XmlOnly);

        var act = () => t.TransformAsync("<r/>");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("SEPM0016").And.Contain("html");
    }

    [Fact]
    public async Task Result_document_method_chosen_at_run_time_is_rejected()
    {
        var t = await Load("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/">
                <out/>
                <xsl:result-document href="side.txt" method="{'te' || 'xt'}">side</xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """, XmlOnly);

        var act = () => t.TransformAsync("<r/>");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("SEPM0016").And.Contain("text");
    }

    [Fact]
    public async Task Result_document_without_href_is_judged_on_its_own_method()
    {
        var t = await Load("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/">
                <xsl:result-document method="text">principal</xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """, XmlOnly);

        var act = () => t.TransformAsync("<r/>");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("SEPM0016").And.Contain("text");
    }

    [Fact]
    public async Task An_allowed_method_transforms_normally()
    {
        var t = await Load("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">ok</xsl:template>
            </xsl:stylesheet>
            """, new HashSet<OutputMethod> { OutputMethod.Xml, OutputMethod.Text });

        (await t.TransformAsync("<r/>")).Should().Be("ok");
    }

    [Fact]
    public async Task OutputDeclarations_lists_named_and_imported_declarations()
    {
        var dir = Directory.CreateTempSubdirectory("xsl-outputs-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "imported.xsl"), """
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                  <xsl:output name="report" method="html"/>
                </xsl:stylesheet>
                """);
            var mainPath = Path.Combine(dir.FullName, "main.xsl");
            File.WriteAllText(mainPath, """
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                  <xsl:import href="imported.xsl"/>
                  <xsl:output method="text"/>
                  <xsl:output name="data" method="json"/>
                  <xsl:template match="/">x</xsl:template>
                </xsl:stylesheet>
                """);
            var t = new XsltTransformer();
            t.OutputDeclarations.Should().BeEmpty("nothing is loaded yet");
            await t.LoadStylesheetAsync(File.ReadAllText(mainPath), new Uri(mainPath));

            var byName = t.OutputDeclarations.ToDictionary(d => d.Name?.LocalName ?? "", d => d.EffectiveMethod);
            byName.Should().Contain("", OutputMethod.Text);
            byName.Should().Contain("data", OutputMethod.Json);
            byName.Should().Contain("report", OutputMethod.Html);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
