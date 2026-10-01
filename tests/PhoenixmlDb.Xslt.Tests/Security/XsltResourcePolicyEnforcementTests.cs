using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// A transformation under a resource policy reads, fetches and evaluates only what the policy
/// allows. Each case plants a marker (or counts requests at a 127.0.0.1 listener) and asserts
/// it never comes back under <see cref="ResourcePolicy.ServerDefault"/>, with a no-policy
/// control showing the same stylesheet does reach the resource.
/// </summary>
public sealed class XsltResourcePolicyEnforcementTests : IDisposable
{
    private const string Marker = "SECRET-MARKER-9c1e";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-xrp-" + Guid.NewGuid().ToString("N"));
    private readonly string _allowed;

    public XsltResourcePolicyEnforcementTests()
    {
        _allowed = Directory.CreateDirectory(Path.Combine(_dir, "allowed")).FullName;
        File.WriteAllText(Path.Combine(_allowed, "secret.txt"), Marker);
        File.WriteAllText(Path.Combine(_allowed, "secret.xml"), $"<s>{Marker}</s>");
        File.WriteAllText(Path.Combine(_allowed, "nested.xsl"), Stylesheet($"<xsl:value-of select=\"'{Marker}'\"/>"));
        File.WriteAllText(Path.Combine(_allowed, "s.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:s">
              <xs:element name="e" type="xs:string"/>
            </xs:schema>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string FileUri(string name) => new Uri(Path.Combine(_allowed, name)).AbsoluteUri;
    private string FilePath(string name) => Path.Combine(_allowed, name);

    private static string Stylesheet(string body, string top = "") =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" + top +
        "<xsl:template match='/' name='xsl:initial-template'><out>" + body + "</out></xsl:template></xsl:stylesheet>";

    private static async Task<string> Run(ResourcePolicy? policy, string stylesheet, Uri? baseUri = null)
    {
        var t = new XsltTransformer { ResourcePolicy = policy };
        try
        {
            await t.LoadStylesheetAsync(stylesheet, baseUri);
            return await t.TransformAsync("<in/>");
        }
        catch (Exception e) when (e is XsltException or PhoenixmlDb.XQuery.Execution.XQueryRuntimeException
                                   or PhoenixmlDb.XQuery.Functions.XQueryException or ResourceAccessDeniedException)
        {
            return "ERR " + e.Message;
        }
    }

    public static TheoryData<string> DeniedBodies() => new()
    {
        "<xsl:source-document streamable='no' href='{file:secret.xml}'><xsl:value-of select='.'/></xsl:source-document>",
        "<xsl:source-document streamable='yes' href='{file:secret.xml}'><xsl:for-each select='s'><xsl:value-of select='.'/></xsl:for-each></xsl:source-document>",
        "<xsl:value-of select=\"unparsed-text('{path:secret.txt}')\"/>",
        "<xsl:value-of select=\"unparsed-text('{file:secret.txt}')\"/>",
        "<xsl:value-of select=\"string-join(unparsed-text-lines('{path:secret.txt}'))\"/>",
        "<xsl:value-of select=\"transform(map { 'stylesheet-location': '{file:nested.xsl}' })?output\"/>",
    };

    private string Fill(string template) => System.Text.RegularExpressions.Regex.Replace(template, @"\{(file|path):([^}]+)\}",
        m => m.Groups[1].Value == "file" ? FileUri(m.Groups[2].Value) : FilePath(m.Groups[2].Value));

    [Theory]
    [MemberData(nameof(DeniedBodies))]
    public async Task ServerDefault_never_returns_local_content(string body)
    {
        var xsl = Stylesheet(Fill(body));
        (await Run(ResourcePolicy.ServerDefault, xsl)).Should().NotContain(Marker);
        (await Run(null, xsl)).Should().Contain(Marker);
    }

    [Fact]
    public async Task A_refused_read_in_use_when_says_nothing_about_the_file()
    {
        // An unevaluable use-when includes its element. Under a policy the read is refused, so
        // the element must be included whatever the file holds, or whether it exists at all.
        await File.WriteAllTextAsync(FilePath("plain.json"), """{"k":"nothing"}""");
        await File.WriteAllTextAsync(FilePath("secret.json"), $$"""{"k":"{{Marker}}"}""");
        string Probe(string name) => Stylesheet(
            $"<hit xsl:use-when=\"contains(json-doc('{FilePath(name)}')?k, 'SECRET')\">in</hit>");
        var results = new[] { "secret.json", "plain.json", "missing.json" }
            .Select(n => Run(ResourcePolicy.ServerDefault, Probe(n))).ToArray();
        await Task.WhenAll(results);
        results.Select(r => r.Result).Distinct().Should().ContainSingle();
        // Control: without a policy the answer does depend on the content.
        (await Run(null, Probe("plain.json"))).Should().NotBe(await Run(null, Probe("secret.json")));
    }

    [Fact]
    public async Task A_nested_transform_inherits_the_policy()
    {
        var nested = "&lt;xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'&gt;"
                   + $"&lt;xsl:template name='xsl:initial-template'&gt;&lt;xsl:value-of select=\"unparsed-text('{FilePath("secret.txt")}')\"/&gt;&lt;/xsl:template&gt;"
                   + "&lt;/xsl:stylesheet&gt;";
        var xsl = Stylesheet(
            "<xsl:value-of select=\"transform(map { 'stylesheet-text': string($nested), 'initial-template': QName('http://www.w3.org/1999/XSL/Transform', 'initial-template'), 'delivery-format': 'serialized' })?output\"/>",
            $"<xsl:variable name='nested'>{nested}</xsl:variable>");
        (await Run(ResourcePolicy.ServerDefault, xsl)).Should().NotContain(Marker);
        (await Run(null, xsl)).Should().Contain(Marker);
    }

    [Theory]
    [InlineData("unparsed-text('{path}')")]
    [InlineData("string(doc('{xml}'))")]
    public async Task Static_expressions_evaluated_at_load_time_obey_the_policy(string select)
    {
        // use-when is decided by the parser while the stylesheet LOADS, from a static variable
        // it also evaluates then: an oracle for file content before any runtime check existed.
        select = select.Replace("{path}", FilePath("secret.txt"), StringComparison.Ordinal)
                       .Replace("{xml}", FileUri("secret.xml"), StringComparison.Ordinal);
        var xsl = Stylesheet(
            "<xsl:value-of use-when=\"contains($v, 'SECRET-MARKER')\" select=\"'READ-AT-LOAD-TIME'\"/>",
            $"<xsl:variable name='v' static='yes' select=\"{select}\"/>");
        (await Run(ResourcePolicy.ServerDefault, xsl)).Should().NotContain("READ-AT-LOAD-TIME");
        (await Run(null, xsl)).Should().Contain("READ-AT-LOAD-TIME");
    }

    [Fact]
    public async Task Availability_checks_do_not_reveal_denied_files()
    {
        (await Run(ResourcePolicy.ServerDefault, Stylesheet($"<xsl:value-of select=\"unparsed-text-available('{FilePath("secret.txt")}')\"/>")))
            .Should().Contain("<out>false</out>");
        (await Run(ResourcePolicy.ServerDefault, Stylesheet($"<xsl:value-of select=\"stream-available('{FileUri("secret.xml")}')\"/>")))
            .Should().Contain("<out>false</out>");
    }

    [Fact]
    public async Task Xsl_evaluate_honours_AllowXslEvaluate()
    {
        var xsl = Stylesheet("<xsl:evaluate xpath=\"'1+1'\"/>");
        (await Run(ResourcePolicy.ServerDefault, xsl)).Should().StartWith("ERR XTDE3175");
        (await Run(ResourcePolicy.Unrestricted, xsl)).Should().Contain("<out>2</out>");
    }

    [Fact]
    public async Task Xsl_import_needs_import_access_not_read_access()
    {
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                  $"<xsl:import href='{FileUri("nested.xsl")}'/></xsl:stylesheet>";
        // The principal module's own location (it is loaded from a string) is the base.
        var main = new Uri(Path.Combine(_dir, "main.xsl"));
        var readOnly = ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).Build();
        (await Run(readOnly, xsl, main)).Should().StartWith("ERR XTSE0165");
        var withImport = ResourcePolicy.CreateBuilder().AllowImportFrom("file", pathPrefix: _allowed).Build();
        (await Run(withImport, xsl, main)).Should().Contain(Marker);
    }

    [Fact]
    public async Task Import_schema_locations_are_checked()
    {
        var xsl = Stylesheet("x", $"<xsl:import-schema namespace='urn:s' schema-location='{FileUri("s.xsd")}'/>");
        var t = new XsltTransformer { ResourcePolicy = ResourcePolicy.ServerDefault, SchemaProvider = new PhoenixmlDb.XQuery.XsdSchemaProvider() };
        var act = () => t.LoadStylesheetAsync(xsl);
        await act.Should().ThrowAsync<XsltException>();
    }

    [Fact]
    public async Task A_schema_location_is_one_uri_resolved_against_its_own_module()
    {
        // The imported module lives in mod/ beside its schema; the principal module one level up.
        var mod = Directory.CreateDirectory(Path.Combine(_allowed, "mod")).FullName;
        File.Copy(FilePath("s.xsd"), Path.Combine(mod, "s.xsd"));
        await File.WriteAllTextAsync(Path.Combine(mod, "m.xsl"),
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:import-schema namespace='urn:s' schema-location='s.xsd'/></xsl:stylesheet>");
        var main = new Uri(Path.Combine(_allowed, "main.xsl"));
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                  "<xsl:import href='mod/m.xsl'/><xsl:template match='/'><ok/></xsl:template></xsl:stylesheet>";
        var t = new XsltTransformer { SchemaProvider = new PhoenixmlDb.XQuery.XsdSchemaProvider() };
        await t.LoadStylesheetAsync(xsl, main);   // resolved against mod/, where s.xsd is
        (await t.TransformAsync("<in/>")).Should().Contain("<ok");

        // A second whitespace-separated token is not a second location to try.
        using var server = new LoopbackHttpServer().Serve("/x.xsd", await File.ReadAllTextAsync(FilePath("s.xsd")));
        var twoTokens = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                        $"<xsl:import-schema namespace='urn:s' schema-location='missing.xsd {server.Url("/x.xsd")}'/></xsl:stylesheet>";
        var t2 = new XsltTransformer { SchemaProvider = new PhoenixmlDb.XQuery.XsdSchemaProvider() };
        try { await t2.LoadStylesheetAsync(twoTokens, main); } catch (XsltException) { }
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task A_custom_resolver_is_given_the_base_the_import_resolves_against()
    {
        var resolver = new BaseCapturingResolver();
        var policy = ResourcePolicy.CreateBuilder().AllowImportFrom("file", pathPrefix: _allowed).WithResourceResolver(resolver).Build();
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                  $"<xsl:import href='nested.xsl' xml:base='{new Uri(_allowed + "/").AbsoluteUri}'/></xsl:stylesheet>";
        await new XsltTransformer { ResourcePolicy = policy }.LoadStylesheetAsync(xsl, new Uri(Path.Combine(_dir, "main.xsl")));
        resolver.BaseSeen!.AbsoluteUri.Should().StartWith(new Uri(_allowed + "/").AbsoluteUri);
    }

    private sealed class BaseCapturingResolver : ResourceResolverBase
    {
        public Uri? BaseSeen { get; private set; }
        public override string? ResolveStylesheetModule(string href, Uri? baseUri)
        {
            BaseSeen = baseUri;
            return null;
        }
    }

    [Fact]
    public async Task Loading_a_stylesheet_makes_no_request_the_policy_forbids()
    {
        using var server = new LoopbackHttpServer().Serve("/x.xml", $"<r>{Marker}</r>").Serve("/m.xsl", Stylesheet("m"));
        var docXsl = Stylesheet($"<xsl:value-of select=\"doc('{server.Url("/x.xml")}')\"/>");
        var importXsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                        $"<xsl:import href='{server.Url("/m.xsl")}'/></xsl:stylesheet>";

        var t = new XsltTransformer { ResourcePolicy = ResourcePolicy.ServerDefault };
        try { await t.LoadStylesheetAsync(docXsl); } catch (XsltException) { }
        try { await t.LoadStylesheetAsync(importXsl); } catch (XsltException) { }
        server.Requests.Should().Be(0);

        // Control: without a policy the pre-fetch does go out.
        await new XsltTransformer().LoadStylesheetAsync(docXsl);
        server.Requests.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task An_http_import_redirect_is_re_authorised()
    {
        using var target = new LoopbackHttpServer().Serve("/m.xsl", Stylesheet(Marker));
        using var origin = new LoopbackHttpServer().Redirect("/m.xsl", target.Url("/m.xsl"));
        var policy = ResourcePolicy.CreateBuilder().AllowImportFrom("http", "127.0.0.1", null, origin.Port).Build();
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                  $"<xsl:import href='{origin.Url("/m.xsl")}'/></xsl:stylesheet>";
        (await Run(policy, xsl, new Uri(Path.Combine(_dir, "main.xsl")))).Should().NotContain(Marker);
        target.Requests.Should().Be(0);
        (await Run(null, xsl, new Uri(Path.Combine(_dir, "main.xsl")))).Should().Contain(Marker);
    }

    [Fact]
    public async Task Runtime_redirects_are_re_authorised()
    {
        using var target = new LoopbackHttpServer().Serve("/x.xml", $"<r>{Marker}</r>");
        using var origin = new LoopbackHttpServer().Redirect("/x.xml", target.Url("/x.xml"));
        var policy = ResourcePolicy.CreateBuilder().AllowReadFrom("http", "127.0.0.1", null, origin.Port).Build();
        // concat() keeps the URL out of the load-time pre-fetch, so this is the runtime path.
        var xsl = Stylesheet($"<xsl:value-of select=\"doc(concat('{origin.Url("")}', '/x.xml'))\"/>");

        (await Run(policy, xsl)).Should().NotContain(Marker);
        target.Requests.Should().Be(0);
        (await Run(null, xsl)).Should().Contain(Marker);
    }

    [Fact]
    public async Task The_XQuery_side_transform_runs_under_the_calling_querys_policy()
    {
        // PhoenixmlDb.Xslt registers fn:transform for XQuery; referencing this assembly loads it.
        _ = typeof(XsltTransformer);
        var facade = new PhoenixmlDb.XQuery.XQueryFacade { ResourcePolicy = ResourcePolicy.ServerDefault };
        var query = $"transform(map {{ 'stylesheet-location': '{FileUri("nested.xsl")}' }})?output";
        string result;
        try { result = await facade.EvaluateAsync(query) ?? ""; }
        catch (Exception e) when (e is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException or PhoenixmlDb.XQuery.Functions.XQueryException) { result = "ERR " + e.Message; }
        result.Should().NotContain(Marker);
    }
}
