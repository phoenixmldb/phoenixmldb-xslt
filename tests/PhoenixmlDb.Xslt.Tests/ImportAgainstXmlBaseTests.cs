using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The href of xsl:import and xsl:include is relative to the base URI in effect at the element,
/// which xml:base sets. The host's resolver is asked for that location, once. In 2.8.0 the pass
/// that collects static declarations resolved the href against the module's own base first, so
/// the host was asked twice: first for a location the stylesheet does not name (with no base at
/// all, for a stylesheet given as text), then for the right one.
/// </summary>
public sealed class ImportAgainstXmlBaseTests
{
    private sealed class Recorder : ResourceResolverBase
    {
        public Dictionary<string, string> Content { get; } = new(StringComparer.Ordinal);
        public List<string> Modules { get; } = [];
        public List<string> Contents { get; } = [];
        public override bool SuppliesAllContent => true;

        private static string Describe(string href, Uri? baseUri) => $"{href} @ {baseUri?.AbsoluteUri ?? "no base"}";

        public override string? ResolveStylesheetModule(string href, Uri? baseUri)
        {
            Modules.Add(Describe(href, baseUri));
            return null;
        }

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Contents.Add(Describe(request.Location, request.BaseUri));
            // A host that will not guess: a relative location with no base is an error to it.
            if (request.BaseUri is null && !Uri.TryCreate(request.Location, UriKind.Absolute, out _))
                throw new ResourceAccessDeniedException(request.Location, request.Access, "a relative location with no base URI");
            var absolute = request.BaseUri is null ? request.Location : new Uri(request.BaseUri, request.Location).AbsoluteUri;
            return Content.TryGetValue(absolute, out var text) ? new ResourceContent(text, new Uri(absolute)) : null;
        }
    }

    private const string Xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'";
    private const string Library = Xsl + "><xsl:variable name='v' select='42'/></xsl:stylesheet>";
    private const string UsesV = "<xsl:output method='text'/><xsl:template match='/'><xsl:value-of select='$v'/></xsl:template></xsl:stylesheet>";

    private static async Task<string> Run(Recorder host, string stylesheet, Uri? baseUri)
    {
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build() };
        await transformer.LoadStylesheetAsync(stylesheet, baseUri);
        return (await transformer.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task A_stylesheet_given_as_text_imports_against_its_xml_base(string instruction)
    {
        var host = new Recorder();
        host.Content["mem://base/dir/lib.xsl"] = Library;

        var result = await Run(host, Xsl + $" xml:base='mem://base/dir/'><xsl:{instruction} href='lib.xsl'/>" + UsesV, baseUri: null);

        result.Should().Be("42");
        host.Modules.Should().Equal("lib.xsl @ mem://base/dir/");
        host.Contents.Should().Equal("lib.xsl @ mem://base/dir/");
    }

    [Fact]
    public async Task An_imported_module_imports_against_its_own_xml_base()
    {
        var host = new Recorder();
        host.Content["mem://app/mods/a.xsl"] = Xsl + " xml:base='mem://other/place/'><xsl:import href='lib.xsl'/></xsl:stylesheet>";
        host.Content["mem://other/place/lib.xsl"] = Library;

        var result = await Run(host, Xsl + "><xsl:import href='mods/a.xsl'/>" + UsesV, new Uri("mem://app/main.xsl"));

        result.Should().Be("42");
        host.Contents.Should().Equal("mods/a.xsl @ mem://app/main.xsl", "lib.xsl @ mem://other/place/");
        host.Modules.Should().Equal(host.Contents);
    }

    [Fact]
    public async Task Xml_base_on_the_import_element_itself_counts()
    {
        var host = new Recorder();
        host.Content["mem://base/dir/lib.xsl"] = Library;

        var result = await Run(host, Xsl + "><xsl:import href='lib.xsl' xml:base='mem://base/dir/'/>" + UsesV, new Uri("mem://app/main.xsl"));

        result.Should().Be("42");
        host.Contents.Should().Equal("lib.xsl @ mem://base/dir/");
    }

    [Fact]
    public async Task Without_xml_base_the_import_is_against_the_modules_base_as_before()
    {
        var host = new Recorder();
        host.Content["mem://app/lib.xsl"] = Library;

        var result = await Run(host, Xsl + "><xsl:import href='lib.xsl'/>" + UsesV, new Uri("mem://app/main.xsl"));

        result.Should().Be("42");
        host.Contents.Should().Equal("lib.xsl @ mem://app/main.xsl");
    }
}
