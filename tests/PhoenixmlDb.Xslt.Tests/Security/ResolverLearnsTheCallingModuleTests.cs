using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// A host's resolver is told which stylesheet module asks for a resource (#330):
/// <see cref="ResourceRequest.ModuleUri"/> is where the module that contains the call was
/// loaded from. The module cannot change it; <c>xml:base</c> changes the static base URI, which
/// the request also carries, and nothing else.
/// </summary>
public sealed class ResolverLearnsTheCallingModuleTests
{
    private const string MainUri = "mem://app/main.xsl";
    private const string LibraryUri = "mem://app/lib/inc.xsl";

    private sealed class Recorder : ResourceResolverBase
    {
        public string Library { get; set; } = "";

        /// <summary>How the transformation ended, for the message of a failed assertion.</summary>
        public string Outcome { get; set; } = "completed";
        public List<(string Location, string? Module)> Asked { get; } = [];
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            var absolute = request.BaseUri != null && Uri.TryCreate(request.BaseUri, request.Location, out var joined)
                ? joined.AbsoluteUri
                : request.Location;
            if (absolute == LibraryUri)
                return new ResourceContent(Library, new Uri(absolute));
            Asked.Add((absolute, request.ModuleUri?.AbsoluteUri));
            return null;
        }

        public override bool? IsAvailable(ResourceRequest request)
        {
            Asked.Add((request.Location, request.ModuleUri?.AbsoluteUri));
            return false;
        }
    }

    private const string Head =
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform' xmlns:f='urn:f' "
        + "xmlns:xs='http://www.w3.org/2001/XMLSchema' exclude-result-prefixes='#all'";

    private static string Module(string declarations, string rootAttributes = "") =>
        Head + rootAttributes + "><xsl:output method='text'/>" + declarations + "</xsl:stylesheet>";

    /// <summary>Runs a main module that imports the library, and returns what the host was asked.</summary>
    private static async Task<Recorder> Run(string mainDeclarations, string libraryDeclarations, string libraryRootAttributes = "", string source = "<r><i/></r>")
    {
        var host = new Recorder { Library = Module(libraryDeclarations, libraryRootAttributes) };
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build() };
        try
        {
            await transformer.LoadStylesheetAsync(Module("<xsl:import href='lib/inc.xsl'/>" + mainDeclarations), new Uri(MainUri));
            await transformer.TransformAsync(source);
        }
#pragma warning disable CA1031 // the host supplies nothing: the load fails, and what it was asked is the subject
        catch (Exception e)
#pragma warning restore CA1031
        {
            host.Outcome = e.Message;
        }
        return host;
    }

    private const string CallF = "<xsl:template match='/'><xsl:value-of select='f:f()'/></xsl:template>";

    private static string Function(string expression) =>
        $"<xsl:function name='f:f'><xsl:sequence select=\"string(({expression})[1])\"/></xsl:function>";

    [Theory]
    [InlineData("doc('d.xml')", "d.xml")]
    [InlineData("document('d.xml')", "d.xml")]
    [InlineData("doc-available('d.xml')", "d.xml")]
    [InlineData("unparsed-text('t.txt')", "t.txt")]
    [InlineData("unparsed-text-lines('t.txt')", "t.txt")]
    [InlineData("unparsed-text-available('t.txt')", "t.txt")]
    [InlineData("json-doc('j.json')", "j.json")]
    [InlineData("transform(map { 'stylesheet-location': 's.xsl', 'source-node': parse-xml('&lt;r/&gt;') })?output", "s.xsl")]
    public async Task A_load_names_the_module_that_makes_it(string expression, string file)
    {
        // The function is in the library; the main module only calls it.
        var inLibrary = await Run(CallF, Function(expression));
        inLibrary.Asked.Should().NotBeEmpty(inLibrary.Outcome).And.OnlyContain(a => a.Module == LibraryUri);
        inLibrary.Asked.Should().Contain(a => a.Location == "mem://app/lib/" + file);

        // The function is in the main module; the library is imported and does nothing.
        var inMain = await Run(Function(expression) + CallF, "");
        inMain.Asked.Should().NotBeEmpty().And.OnlyContain(a => a.Module == MainUri);
    }

    /// <summary>
    /// xml:base moves the static base URI, and with it where a relative location points. It
    /// does not move the module.
    /// </summary>
    [Theory]
    [InlineData("doc('d.xml')")]
    [InlineData("unparsed-text('t.txt')")]
    [InlineData("json-doc('j.json')")]
    [InlineData("doc-available('d.xml')")]
    public async Task A_module_that_sets_xml_base_is_still_known_by_where_it_is(string expression)
    {
        var host = await Run(CallF, Function(expression), " xml:base='mem://elsewhere/trusted/'");

        host.Asked.Should().NotBeEmpty().And.OnlyContain(a => a.Module == LibraryUri);
        host.Asked.Should().OnlyContain(a => a.Location.StartsWith("mem://elsewhere/trusted/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Code of the library that an instruction of the MAIN module causes to run: the load is
    /// still the library's. Each of these is evaluated outside any instruction of its own.
    /// </summary>
    [Theory]
    [InlineData("a global variable",
        "<xsl:variable name='g' select=\"string((doc('d.xml'))[1])\"/>",
        "<xsl:template match='/'><xsl:value-of select='$g'/></xsl:template>")]
    [InlineData("a global parameter default",
        "<xsl:param name='g' select=\"string((doc('d.xml'))[1])\"/>",
        "<xsl:template match='/'><xsl:value-of select='$g'/></xsl:template>")]
    [InlineData("a key",
        "<xsl:key name='k' match='i' use=\"string((doc('d.xml'))[1])\"/>",
        "<xsl:template match='/'><xsl:value-of select=\"count(key('k', 'x'))\"/></xsl:template>")]
    [InlineData("a match pattern",
        "<xsl:template match=\"i[doc('d.xml')]\" mode='m'>x</xsl:template>",
        "<xsl:template match='/'><xsl:apply-templates select='r/i' mode='m'/></xsl:template>")]
    [InlineData("a template parameter default",
        "<xsl:template name='t'><xsl:param name='p' select=\"string((doc('d.xml'))[1])\"/><xsl:value-of select='$p'/></xsl:template>",
        "<xsl:template match='/'><xsl:call-template name='t'/></xsl:template>")]
    [InlineData("an attribute set",
        "<xsl:attribute-set name='s'><xsl:attribute name='a' select=\"string((doc('d.xml'))[1])\"/></xsl:attribute-set>",
        "<xsl:template match='/'><e xsl:use-attribute-sets='s'/></xsl:template>")]
    [InlineData("an accumulator rule",
        "<xsl:accumulator name='acc' initial-value='0'><xsl:accumulator-rule match='i' select=\"string((doc('d.xml'))[1])\"/></xsl:accumulator>"
        + "<xsl:mode use-accumulators='acc'/>",
        "<xsl:template match='/'><xsl:value-of select=\"r/i/accumulator-after('acc')\"/></xsl:template>")]
    [InlineData("a closure the library returns",
        "<xsl:function name='f:make'><xsl:sequence select=\"function() { doc('d.xml') }\"/></xsl:function>",
        "<xsl:template match='/'><xsl:value-of select='string((f:make()())[1])'/></xsl:template>")]
    [InlineData("a sort key in a library template",
        "<xsl:template name='t'><xsl:for-each select='r/i'><xsl:sort select=\"string((doc('d.xml'))[1])\"/>x</xsl:for-each></xsl:template>",
        "<xsl:template match='/'><xsl:call-template name='t'/></xsl:template>")]
    [InlineData("an attribute value template",
        "<xsl:template name='t'><e a=\"{string((doc('d.xml'))[1])}\"/></xsl:template>",
        "<xsl:template match='/'><xsl:call-template name='t'/></xsl:template>")]
    public async Task What_the_library_declares_runs_as_the_library(string what, string library, string main)
    {
        var host = await Run(main, library);

        host.Asked.Should().NotBeEmpty(what);
        host.Asked.Should().OnlyContain(a => a.Module == LibraryUri, what);
    }

    /// <summary>And the other way: the library calls back into the main module.</summary>
    [Fact]
    public async Task What_the_main_module_declares_runs_as_the_main_module_when_the_library_calls_it()
    {
        var host = await Run(
            "<xsl:template match='i' mode='m'><xsl:value-of select=\"string((doc('d.xml'))[1])\"/></xsl:template>"
            + "<xsl:template match='/'><xsl:call-template name='t'/></xsl:template>",
            "<xsl:template name='t'><xsl:apply-templates select='r/i' mode='m'/></xsl:template>");

        host.Asked.Should().NotBeEmpty().And.OnlyContain(a => a.Module == MainUri);
    }

    /// <summary>A stylesheet with no base URI has no location to report.</summary>
    [Fact]
    public async Task A_module_with_no_location_reports_none()
    {
        var host = new Recorder();
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build() };
        try
        {
            await transformer.LoadStylesheetAsync(Module("<xsl:template match='/'><xsl:value-of select=\"string((doc('mem://x/d.xml'))[1])\"/></xsl:template>"));
            await transformer.TransformAsync("<r/>");
        }
#pragma warning disable CA1031 // as above
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        host.Asked.Should().NotBeEmpty().And.OnlyContain(a => a.Module == null);
    }
}
