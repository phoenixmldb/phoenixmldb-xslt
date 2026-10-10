using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The pass that collects static declarations reads the modules a stylesheet is made of, and no
/// others: not a module that <c>use-when</c> excludes, and not what such a module imports.
/// </summary>
public sealed class StaticPassAsksOnlyForIncludedModulesTests
{
    private sealed class Recorder : ResourceResolverBase
    {
        public Dictionary<string, string> Content { get; } = new(StringComparer.Ordinal);
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            var absolute = request.BaseUri is null ? request.Location : new Uri(request.BaseUri, request.Location).AbsoluteUri;
            Asked.Add(absolute);
            return Content.TryGetValue(absolute, out var text) ? new ResourceContent(text, new Uri(absolute)) : null;
        }
    }

    private const string Xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'";
    private const string Library = Xsl + "><xsl:variable name='v' select='42'/><xsl:param name='lib-static' static='yes' select='true()'/></xsl:stylesheet>";
    private const string Tail = "<xsl:output method='text'/><xsl:template match='/'>ok</xsl:template></xsl:stylesheet>";
    private static readonly Uri Main = new("mem://app/main.xsl");

    private static async Task<string> Run(Recorder host, string stylesheet, Uri? baseUri, Action<XsltTransformer>? configure = null)
    {
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build() };
        configure?.Invoke(transformer);
        await transformer.LoadStylesheetAsync(stylesheet, baseUri);
        return (await transformer.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task A_module_excluded_by_a_static_parameter_is_not_asked_for(string instruction)
    {
        var host = new Recorder();
        host.Content["mem://app/m.xsl"] = Library;
        var declarations = instruction == "import"
            ? "<xsl:param name='p' static='yes' select='false()'/><xsl:import href='m.xsl' use-when='$p'/>"
            : "<xsl:param name='p' static='yes' select='false()'/><xsl:include href='m.xsl' use-when='$p'/>";

        (await Run(host, Xsl + ">" + declarations + Tail, Main)).Should().Be("ok");
        host.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_admitted_by_a_static_parameter_is_read_and_its_static_declarations_are_seen()
    {
        var host = new Recorder();
        host.Content["mem://app/m.xsl"] = Library;

        var result = await Run(host,
            Xsl + "><xsl:param name='p' static='yes' select='true()'/><xsl:import href='m.xsl' use-when='$p'/>"
            + "<xsl:output method='text'/><xsl:template match='/'><xsl:value-of select='$v' use-when='$lib-static'/></xsl:template></xsl:stylesheet>", Main);

        result.Should().Be("42");
        host.Asked.Should().Equal("mem://app/m.xsl");
    }

    [Fact]
    public async Task What_a_module_excluded_at_its_root_imports_is_not_asked_for()
    {
        var host = new Recorder();
        host.Content["mem://app/a.xsl"] = Xsl + " use-when='false()'><xsl:import href='m.xsl'/></xsl:stylesheet>";
        host.Content["mem://app/m.xsl"] = Library;

        (await Run(host, Xsl + "><xsl:import href='a.xsl'/>" + Tail, Main)).Should().Be("ok");
        host.Asked.Should().Equal("mem://app/a.xsl");
    }

    [Fact]
    public async Task A_relative_schema_location_with_no_base_uri_is_refused_and_names_no_directory()
    {
        var host = new Recorder();

        var act = () => Run(host, Xsl + "><xsl:import-schema namespace='urn:s' schema-location='s.xsd'/>" + Tail, baseUri: null);

        var error = (await act.Should().ThrowAsync<XsltException>()).Which;
        error.Message.Should().Contain("no base URI").And.NotContain(Environment.CurrentDirectory);
        host.Asked.Should().BeEmpty();
    }
}
