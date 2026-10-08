using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// Loading a stylesheet pre-fetches its http imports, so that a runtime that cannot block (Blazor
/// WebAssembly) finds them ready. With a host resolver in place the pre-fetch has to give way to
/// it: a resolver that is the only source of resources is never bypassed, and a module a resolver
/// supplies is not also fetched by the engine's own client.
/// </summary>
public sealed class PreloadRespectsHostResolverTests : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _origin;
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Task _serving;

    private const string Lib = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:lib="urn:lib">
          <xsl:template name="lib:greet">hello-from-lib</xsl:template>
        </xsl:stylesheet>
        """;

    public PreloadRespectsHostResolverTests()
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _origin = $"http://localhost:{((IPEndPoint)probe.LocalEndpoint).Port}/";
        }
        _listener.Prefixes.Add(_origin);
        _listener.Start();
        _serving = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                var path = context.Request.Url!.AbsolutePath;
                _requests.Enqueue(path);
                var body = path.EndsWith("lib.xsl", StringComparison.Ordinal) ? Lib
                    : path.EndsWith("data.xml", StringComparison.Ordinal) ? "<data>from-network</data>"
                    : "";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/xml";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.OutputStream.Close();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _serving;
        _listener.Close();
    }

    /// <summary>Imports a module over http, and names a document over http in a branch that never runs.</summary>
    private string Main(bool withDoc = true) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:lib="urn:lib" exclude-result-prefixes="#all">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:import href="{_origin}lib.xsl"/>
          <xsl:template match="/">
            <out><xsl:call-template name="lib:greet"/>{(withDoc ? $"<xsl:if test=\"false()\"><xsl:copy-of select=\"doc('{_origin}data.xml')\"/></xsl:if>" : "")}</out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private sealed class Resolver(bool suppliesAll, string? lib) : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => suppliesAll;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location);
            return lib is not null && request.Location.EndsWith("lib.xsl", StringComparison.Ordinal)
                ? new ResourceContent(lib, new Uri(request.Location))
                : null;
        }
    }

    private ResourcePolicy Policy(IResourceResolver? resolver)
    {
        var port = new Uri(_origin).Port;
        var builder = ResourcePolicy.CreateBuilder().AllowScheme("http")
            .AllowImportFrom("http", "localhost", null, port).AllowReadFrom("http", "localhost", null, port);
        return (resolver is null ? builder : builder.WithResourceResolver(resolver)).Build();
    }

    private async Task<string> RunAsync(ResourcePolicy policy, bool withDoc = true)
    {
        var transformer = new XsltTransformer { ResourcePolicy = policy };
        await transformer.LoadStylesheetAsync(Main(withDoc), new Uri("urn:test:main.xsl"));
        return (await transformer.TransformAsync("<x/>")).Trim();
    }

    /// <summary>
    /// The resolver is the only source of resources and supplies the module. The policy would
    /// allow the engine to fetch from that origin, and it must not: not the import, and not the
    /// document a branch that never runs names.
    /// </summary>
    [Fact]
    public async Task A_resolver_that_supplies_all_content_is_never_bypassed_at_load()
    {
        var resolver = new Resolver(suppliesAll: true, Lib);
        (await RunAsync(Policy(resolver))).Should().Be("<out>hello-from-lib</out>");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_the_resolver_supplies_is_not_also_fetched()
    {
        var resolver = new Resolver(suppliesAll: false, Lib);
        (await RunAsync(Policy(resolver), withDoc: false)).Should().Be("<out>hello-from-lib</out>");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_the_resolver_leaves_to_the_engine_is_fetched_once()
    {
        var resolver = new Resolver(suppliesAll: false, lib: null);
        (await RunAsync(Policy(resolver), withDoc: false)).Should().Be("<out>hello-from-lib</out>");
        _requests.Should().Equal("/lib.xsl");
    }

    [Fact]
    public async Task With_no_resolver_the_import_is_fetched_once_as_before()
    {
        (await RunAsync(Policy(null), withDoc: false)).Should().Be("<out>hello-from-lib</out>");
        _requests.Should().Equal("/lib.xsl");
    }

    /// <summary>The only source of resources is asked for the module once, by the parser; the pre-fetch does not ask it.</summary>
    [Fact]
    public async Task A_resolver_that_supplies_all_content_is_asked_for_a_module_once()
    {
        var resolver = new Resolver(suppliesAll: true, Lib);
        await RunAsync(Policy(resolver), withDoc: false);
        resolver.Asked.Count(a => a.EndsWith("lib.xsl", StringComparison.Ordinal)).Should().Be(1);
    }

    /// <summary>
    /// A module the resolver supplies imports one that it leaves to the engine. The supplied
    /// module is read for its imports, so the second is still pre-fetched, once.
    /// </summary>
    [Fact]
    public async Task The_imports_of_a_supplied_module_are_still_found()
    {
        var outer = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:import href="{_origin}lib.xsl"/>
            </xsl:stylesheet>
            """;
        var resolver = new OuterResolver(outer);
        var transformer = new XsltTransformer { ResourcePolicy = Policy(resolver) };
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:lib="urn:lib" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import href="{_origin}outer.xsl"/>
              <xsl:template match="/"><out><xsl:call-template name="lib:greet"/></out></xsl:template>
            </xsl:stylesheet>
            """, new Uri("urn:test:main.xsl"));
        (await transformer.TransformAsync("<x/>")).Trim().Should().Be("<out>hello-from-lib</out>");
        _requests.Should().Equal("/lib.xsl");
    }

    private sealed class EverythingResolver(string origin) : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location.Replace(origin, "/", StringComparison.Ordinal));
            var text = request.Location.EndsWith("lib.xsl", StringComparison.Ordinal) ? Lib
                : request.Location.EndsWith("inner.xsl", StringComparison.Ordinal)
                    ? "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:template match='/'><inner/></xsl:template></xsl:stylesheet>"
                : request.Location.EndsWith("t.xsd", StringComparison.Ordinal)
                    ? "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema' targetNamespace='urn:t' elementFormDefault='qualified'><xs:element name='n' type='xs:integer'/></xs:schema>"
                : request.Location.EndsWith(".xml", StringComparison.Ordinal) ? "<data>from-resolver</data>"
                : null;
            return text is null ? null : new ResourceContent(text, new Uri(request.Location));
        }
    }

    /// <summary>
    /// Every way a stylesheet names a resource over http, under a resolver that is the only
    /// source of resources: each is served by the resolver, with no request on the network,
    /// whether the policy has a rule for the origin or no rule at all.
    /// </summary>
    [Theory]
    [InlineData(true, "import", "<xsl:import href='{0}lib.xsl'/>", "<xsl:call-template name='lib:greet'/>", "hello-from-lib")]
    [InlineData(false, "import", "<xsl:import href='{0}lib.xsl'/>", "<xsl:call-template name='lib:greet'/>", "hello-from-lib")]
    [InlineData(true, "doc", "", "<xsl:copy-of select=\"doc('{0}data.xml')\"/>", "<data>from-resolver</data>")]
    [InlineData(false, "doc", "", "<xsl:copy-of select=\"doc('{0}data.xml')\"/>", "<data>from-resolver</data>")]
    [InlineData(true, "doc never evaluated", "", "<xsl:if test='false()'><xsl:copy-of select=\"doc('{0}unused.xml')\"/></xsl:if>ok", "ok")]
    [InlineData(true, "transform", "", "<xsl:copy-of select=\"transform(map{{'stylesheet-location':'{0}inner.xsl','source-node':/}})?output\"/>", "<inner/>")]
    [InlineData(false, "transform", "", "<xsl:copy-of select=\"transform(map{{'stylesheet-location':'{0}inner.xsl','source-node':/}})?output\"/>", "<inner/>")]
    [InlineData(true, "import-schema", "<xsl:import-schema namespace='urn:t' schema-location='{0}t.xsd'/>", "<t:n xsl:validation='strict'>5</t:n>", "<t:n>5</t:n>")]
    [InlineData(false, "import-schema", "<xsl:import-schema namespace='urn:t' schema-location='{0}t.xsd'/>", "<t:n xsl:validation='strict'>5</t:n>", "<t:n>5</t:n>")]
    public async Task Under_a_resolver_that_supplies_all_content_nothing_goes_to_the_network(
        bool originAllowed, string what, string declaration, string body, string expected)
    {
        var resolver = new EverythingResolver(_origin);
        var policy = originAllowed
            ? Policy(resolver)
            : ResourcePolicy.CreateBuilder().WithResourceResolver(resolver).Build();
        var transformer = new XsltTransformer { ResourcePolicy = policy };
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:lib="urn:lib" xmlns:t="urn:t"
                exclude-result-prefixes="lib">
              <xsl:output omit-xml-declaration="yes"/>
              {string.Format(System.Globalization.CultureInfo.InvariantCulture, declaration, _origin)}
              <xsl:template match="/"><out>{string.Format(System.Globalization.CultureInfo.InvariantCulture, body, _origin)}</out></xsl:template>
            </xsl:stylesheet>
            """, new Uri("urn:test:main.xsl"));

        var result = (await transformer.TransformAsync("<x/>")).Trim();

        result.Should().Contain(expected, what);
        _requests.Should().BeEmpty(what);
    }


    private sealed class MergeResolver : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location);
            return request.Location.EndsWith("rows.xml", StringComparison.Ordinal)
                ? new ResourceContent("<rows><row k='a'>from-resolver</row></rows>", new Uri(request.Location))
                : null;
        }
    }

    /// <summary>
    /// A streamable xsl:merge-source opens its documents itself, one reader each. It opened
    /// them by name, with the engine's own client, and never asked the resolver.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_streamable_merge_source_is_read_from_the_resolver(bool originAllowed)
    {
        var resolver = new MergeResolver();
        var policy = originAllowed
            ? Policy(resolver)
            : ResourcePolicy.CreateBuilder().WithResourceResolver(resolver).Build();
        var transformer = new XsltTransformer { ResourcePolicy = policy };
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <out>
                  <xsl:merge>
                    <xsl:merge-source for-each-source="'{_origin}rows.xml'" streamable="yes" select="rows/row">
                      <xsl:merge-key select="@k"/>
                    </xsl:merge-source>
                    <xsl:merge-action><xsl:value-of select="current-merge-group()"/></xsl:merge-action>
                  </xsl:merge>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri("urn:test:main.xsl"));

        var result = (await transformer.TransformAsync("<x/>")).Trim();

        result.Should().Contain("from-resolver");
        _requests.Should().BeEmpty();
        resolver.Asked.Should().ContainSingle(a => a.EndsWith("rows.xml", StringComparison.Ordinal));
    }

    private sealed class OuterResolver(string outer) : ResourceResolverBase
    {
        public override string? ResolveStylesheetModule(string href, Uri? baseUri)
            => href.EndsWith("outer.xsl", StringComparison.Ordinal) ? outer : null;
    }
}
