using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// The requests the compiler makes name the module that makes them (#335, after #330):
/// <see cref="ResourceRequest.ModuleUri"/> is the URI the module that holds the element was
/// loaded at. <c>xml:base</c> changes the base the request carries and not the module.
/// </summary>
public sealed class ResolverLearnsTheImportingModuleTests
{
    private const string MainUri = "mem://app/main.xsl";
    private const string LibraryUri = "mem://app/lib/inc.xsl";

    private sealed class Host : ResourceResolverBase
    {
        public Dictionary<string, string> Content { get; } = new(StringComparer.Ordinal);
        public List<(string Location, string? Module, ResourceAccessKind Kind)> Asked { get; } = [];
        public string Outcome { get; set; } = "completed";
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            var absolute = request.BaseUri != null && Uri.TryCreate(request.BaseUri, request.Location, out var joined)
                ? joined.AbsoluteUri
                : request.Location;
            Asked.Add((absolute, request.ModuleUri?.AbsoluteUri, request.Access));
            return Content.TryGetValue(absolute, out var text) ? new ResourceContent(text, new Uri(absolute)) : null;
        }

        public override bool? IsAvailable(ResourceRequest request)
        {
            var absolute = request.BaseUri != null && Uri.TryCreate(request.BaseUri, request.Location, out var joined)
                ? joined.AbsoluteUri
                : request.Location;
            Asked.Add((absolute, request.ModuleUri?.AbsoluteUri, request.Access));
            return false;
        }
    }

    private static string Module(string declarations) =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform' "
        + "xmlns:xs='http://www.w3.org/2001/XMLSchema' exclude-result-prefixes='#all'>"
        + declarations + "</xsl:stylesheet>";

    private static async Task<Host> Load(string mainDeclarations, params (string Uri, string Declarations)[] modules)
    {
        var host = new Host();
        foreach (var (uri, declarations) in modules)
            host.Content[uri] = Module(declarations);
        var transformer = new XsltTransformer
        {
            ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build(),
            SchemaProvider = new PhoenixmlDb.XQuery.XsdSchemaProvider(),
        };
        try
        {
            await transformer.LoadStylesheetAsync(Module(mainDeclarations), new Uri(MainUri));
        }
#pragma warning disable CA1031 // a load the host does not supply fails; what it was asked is the subject
        catch (Exception e)
#pragma warning restore CA1031
        {
            host.Outcome = e.Message;
        }
        return host;
    }

    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task A_module_is_asked_for_by_the_module_that_names_it(string element)
    {
        var host = await Load($"<xsl:{element} href='lib/inc.xsl'/>",
            (LibraryUri, $"<xsl:{element} href='other.xsl'/>"),
            ("mem://app/lib/other.xsl", ""));

        host.Outcome.Should().Be("completed");
        // Every request for a module: the static pass asks, and the parser asks.
        host.Asked.Where(a => a.Location == LibraryUri).Should().NotBeEmpty().And.OnlyContain(a => a.Module == MainUri);
        host.Asked.Where(a => a.Location == "mem://app/lib/other.xsl").Should().NotBeEmpty().And.OnlyContain(a => a.Module == LibraryUri);
    }

    [Fact]
    public async Task Xml_base_changes_the_location_and_not_the_module()
    {
        var host = await Load("<xsl:import href='inc.xsl' xml:base='mem://elsewhere/dir/'/>",
            ("mem://elsewhere/dir/inc.xsl", "<xsl:include href='next.xsl' xml:base='mem://third/'/>"),
            ("mem://third/next.xsl", ""));

        host.Outcome.Should().Be("completed");
        host.Asked.Where(a => a.Location == "mem://elsewhere/dir/inc.xsl").Should().NotBeEmpty().And.OnlyContain(a => a.Module == MainUri);
        host.Asked.Where(a => a.Location == "mem://third/next.xsl").Should().NotBeEmpty()
            .And.OnlyContain(a => a.Module == "mem://elsewhere/dir/inc.xsl");
    }

    [Fact]
    public async Task A_parameter_document_is_asked_for_by_the_module_of_its_xsl_output()
    {
        var host = await Load("<xsl:import href='lib/inc.xsl'/>",
            (LibraryUri, "<xsl:output parameter-document='params.xml'/>"));

        host.Asked.Where(a => a.Location.EndsWith("params.xml", StringComparison.Ordinal))
            .Should().NotBeEmpty(host.Outcome).And.OnlyContain(a => a.Module == LibraryUri);
    }

    [Theory]
    [InlineData("unparsed-text-available('t.txt')", "t.txt")]
    [InlineData("doc-available('d.xml')", "d.xml")]
    public async Task A_load_a_static_expression_makes_names_its_module(string expression, string file)
    {
        var host = await Load("<xsl:import href='lib/inc.xsl'/>",
            (LibraryUri, $"<xsl:variable name='s' static='yes' select=\"{expression}\"/>"));

        host.Asked.Where(a => a.Location.EndsWith(file, StringComparison.Ordinal))
            .Should().NotBeEmpty(host.Outcome).And.OnlyContain(a => a.Module == LibraryUri);
    }

    private const string Schema =
        "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema' targetNamespace='urn:s'>"
        + "<xs:include schemaLocation='more.xsd'/></xs:schema>";

    private const string More =
        "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema' targetNamespace='urn:s'>"
        + "<xs:simpleType name='word'><xs:restriction base='xs:string'/></xs:simpleType></xs:schema>";

    [Fact]
    public async Task A_schema_is_asked_for_by_the_module_that_imports_it()
    {
        var host = new Host();
        host.Content[LibraryUri] = Module("<xsl:import-schema namespace='urn:s' schema-location='schemas/s.xsd'/>");
        host.Content["mem://app/lib/schemas/s.xsd"] = Schema;
        host.Content["mem://app/lib/schemas/more.xsd"] = More;
        var transformer = new XsltTransformer
        {
            ResourcePolicy = ResourcePolicy.CreateBuilder().AllowScheme("mem").WithResourceResolver(host).Build(),
            SchemaProvider = new PhoenixmlDb.XQuery.XsdSchemaProvider(),
        };
        await transformer.LoadStylesheetAsync(Module("<xsl:import href='lib/inc.xsl'/>"), new Uri(MainUri));

        // The schema document and the one it includes.
        host.Asked.Where(a => a.Location.EndsWith(".xsd", StringComparison.Ordinal))
            .Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(a => a.Module == LibraryUri);
    }
}
