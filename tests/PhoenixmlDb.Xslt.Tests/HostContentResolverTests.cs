using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A host supplies the CONTENT of what a stylesheet loads (IResourceResolver.ResolveContent), so
/// the engine opens nothing itself and there is no gap between checking a location and opening
/// it. Everything here is served from memory; none of the mem: locations exists anywhere.
/// </summary>
public sealed class HostContentResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-xhost-" + Guid.NewGuid().ToString("N"));

    public HostContentResolverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class MemoryResolver(bool suppliesAll) : ResourceResolverBase
    {
        public Dictionary<string, string> Content { get; } = new(StringComparer.Ordinal);
        public override bool SuppliesAllContent => suppliesAll;

        private string Absolute(string location, Uri? baseUri) =>
            baseUri != null && Uri.TryCreate(baseUri, location, out var joined) ? joined.AbsoluteUri : location;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            var absolute = Absolute(request.Location, request.BaseUri);
            return Content.TryGetValue(absolute, out var text)
                ? new ResourceContent(text, Uri.TryCreate(absolute, UriKind.Absolute, out var known) ? known : new Uri("mem://unbased/" + absolute))
                : null;
        }

        public override bool IsTextAvailable(string uri) =>
            Refused.Contains(uri) ? throw new ResourceAccessDeniedException(uri, ResourceAccessKind.ReadText, "refused by the host")
            : Content.ContainsKey(uri);

        /// <summary>Locations this host refuses outright, by throwing from the older text member.</summary>
        public HashSet<string> Refused { get; } = new(StringComparer.Ordinal);

        public override string? ResolveText(string uri, string? encoding) =>
            Refused.Contains(uri) ? throw new ResourceAccessDeniedException(uri, ResourceAccessKind.ReadText, "refused by the host") : null;
    }

    private static readonly Uri StylesheetBase = new("mem://app/main.xsl");

    private static string Sheet(string body, string top = "") =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform' xmlns:xs='http://www.w3.org/2001/XMLSchema' "
        + "xmlns:map='http://www.w3.org/2005/xpath-functions/map' exclude-result-prefixes='#all'>"
        + "<xsl:output method='text'/>" + top + "<xsl:template match='/'>" + body + "</xsl:template></xsl:stylesheet>";

    private static async Task<string> Run(MemoryResolver resolver, string stylesheet, Action<ResourcePolicyBuilder>? rules = null)
    {
        var builder = ResourcePolicy.CreateBuilder().WithResourceResolver(resolver);
        rules?.Invoke(builder);
        var t = new XsltTransformer { ResourcePolicy = builder.Build() };
        try
        {
            await t.LoadStylesheetAsync(stylesheet, StylesheetBase);
            return (await t.TransformAsync("<r/>")).Trim();
        }
#pragma warning disable CA1031 // any failure is the outcome under test
        catch (Exception e)
#pragma warning restore CA1031
        {
            return "ERR " + e.Message;
        }
    }

    [Fact]
    public async Task An_imported_stylesheet_module_comes_from_the_host()
    {
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["mem://app/lib.xsl"] =
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:variable name='v' select='42'/></xsl:stylesheet>";
        (await Run(host, Sheet("<xsl:value-of select='$v'/>", "<xsl:import href='lib.xsl'/>"))).Should().Be("42");
    }

    [Fact]
    public async Task Text_and_json_come_from_the_host()
    {
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["mem://app/t.txt"] = "héllo";
        host.Content["mem://app/x.json"] = "{ \"a\": [1, 2, 3] }";

        (await Run(host, Sheet("<xsl:value-of select=\"unparsed-text('t.txt')\"/>"))).Should().Be("héllo");
        (await Run(host, Sheet("<xsl:value-of select=\"unparsed-text-available('t.txt'), unparsed-text-available('none.txt')\"/>")))
            .Should().Be("true false");
        (await Run(host, Sheet("<xsl:value-of select=\"xs:integer(sum(json-doc('x.json')?a?*))\"/>"))).Should().Be("6");
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    public async Task A_source_document_comes_from_the_host(string streamable)
    {
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["mem://app/data.xml"] = "<d><i>1</i><i>2</i><i>3</i></d>";
        (await Run(host, Sheet(
                $"<xsl:source-document href='data.xml' streamable='{streamable}'><xsl:value-of select='count(d/i)'/></xsl:source-document>")))
            .Should().Be("3");
    }

    [Fact]
    public async Task A_stylesheet_named_to_fn_transform_comes_from_the_host()
    {
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["mem://app/inner.xsl"] =
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:output method='text'/>"
            + "<xsl:template name='main'>inner</xsl:template></xsl:stylesheet>";
        (await Run(host, Sheet(
                "<xsl:value-of select=\"transform(map { 'stylesheet-location': 'inner.xsl', 'initial-template': xs:QName('main'), 'delivery-format': 'serialized' })?output\"/>")))
            .Should().Be("inner");
    }

    [Fact]
    public async Task An_XQuery_module_loaded_from_a_stylesheet_comes_from_the_host()
    {
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["mem://app/m.xqm"] = "module namespace m = 'urn:m'; declare function m:f() { 41 + 1 };";
        (await Run(host, Sheet(
                "<xsl:value-of select=\"load-xquery-module('urn:m', map { 'location-hints': 'm.xqm' })?functions(QName('urn:m', 'f'))?0()\"/>")))
            .Should().Be("42");
    }

    [Theory]
    [InlineData("<xsl:value-of select=\"unparsed-text('{file}')\"/>", "")]
    [InlineData("<xsl:value-of select=\"json-doc('{file}')?x\"/>", "")]
    [InlineData("<xsl:value-of select=\"count(doc('{file}'))\"/>", "")]
    [InlineData("<xsl:source-document href='{file}'><xsl:value-of select='count(*)'/></xsl:source-document>", "")]
    [InlineData("x", "<xsl:import href='{file}'/>")]
    public async Task When_the_host_is_the_only_source_nothing_else_is_opened(string body, string top)
    {
        // The file exists, parses as each of these, and the policy's rules allow its folder. The
        // host does not supply it, so the load fails: there is no fallback to opening it.
        var file = Path.Combine(_dir, "real.xml");
        await File.WriteAllTextAsync(file,
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'/>");
        var uri = new Uri(file).AbsoluteUri;
        var host = new MemoryResolver(suppliesAll: true);

        var result = await Run(host, Sheet(body.Replace("{file}", uri, StringComparison.Ordinal), top.Replace("{file}", uri, StringComparison.Ordinal)),
            rules => rules.AllowReadFrom("file", pathPrefix: _dir).AllowImportFrom("file", pathPrefix: _dir));
        result.Should().StartWith("ERR");
    }

    [Fact]
    public async Task A_resolver_that_is_not_the_only_source_still_falls_through()
    {
        var file = Path.Combine(_dir, "t.txt");
        await File.WriteAllTextAsync(file, "from disk");
        var host = new MemoryResolver(suppliesAll: false);
        (await Run(host, Sheet($"<xsl:value-of select=\"unparsed-text('{new Uri(file).AbsoluteUri}')\"/>"),
                rules => rules.AllowReadFrom("file", pathPrefix: _dir)))
            .Should().Be("from disk");
    }

    [Fact]
    public async Task A_document_supplied_as_content_is_navigable()
    {
        // The older member has to return nodes, and nodes built outside the transformation's
        // own store are not navigable in it: string(doc(u)) worked and count(doc(u)//c) was 0.
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["mem://app/d.xml"] = "<d><c>1</c><c>2</c><g><c>3</c></g></d>";
        (await Run(host, Sheet(
                "<xsl:value-of select=\"count(doc('d.xml')//c), sum(document('d.xml')//c), doc('d.xml') is doc('d.xml')\"/>")))
            .Should().Be("3 6 true");
    }

    [Fact]
    public async Task A_refusal_from_the_host_ends_a_text_load()
    {
        // The policy's rules allow this real file. The host refuses it. That used to be
        // swallowed, and the file then checked again and read by name.
        var file = Path.Combine(_dir, "secret.txt");
        await File.WriteAllTextAsync(file, "SECRET");
        var uri = new Uri(file).AbsoluteUri;
        var host = new MemoryResolver(suppliesAll: false);
        host.Refused.Add(uri);

        foreach (var call in new[] { $"unparsed-text('{uri}')", $"string-join(unparsed-text-lines('{uri}'), '')" })
        {
            var result = await Run(host, Sheet($"<xsl:value-of select=\"{call}\"/>"),
                rules => rules.AllowReadFrom("file", pathPrefix: _dir));
            result.Should().StartWith("ERR").And.NotContain("SECRET");
        }
        (await Run(host, Sheet($"<xsl:value-of select=\"unparsed-text-available('{uri}')\"/>"),
                rules => rules.AllowReadFrom("file", pathPrefix: _dir)))
            .Should().Be("false");
    }

    [Fact]
    public async Task An_import_is_put_to_the_host_even_with_no_base_URI()
    {
        // A stylesheet loaded from text with no base URI: a relative href used to reach no
        // resolver at all. The host is asked with the href as written and a null base.
        var host = new MemoryResolver(suppliesAll: true);
        host.Content["lib.xsl"] =
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:variable name='v' select='42'/></xsl:stylesheet>";
        var t = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build() };
        await t.LoadStylesheetAsync(Sheet("<xsl:value-of select='$v'/>", "<xsl:import href='lib.xsl'/>"));
        (await t.TransformAsync("<r/>")).Trim().Should().Be("42");
    }
}
