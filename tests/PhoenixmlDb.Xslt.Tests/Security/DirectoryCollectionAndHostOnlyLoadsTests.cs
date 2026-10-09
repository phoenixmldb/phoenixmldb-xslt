using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// Reads that the resource policy judged once and the engine then did by name:
/// a directory given to fn:collection (the directory was allowed, each file in it was not
/// asked about), and three loads that never asked a host resolver that is the only source of
/// resources (fn:stream-available and the two parameter documents).
/// </summary>
public sealed class DirectoryCollectionAndHostOnlyLoadsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-dir-collection").FullName;
    private readonly string _allowed;
    private readonly string _allowedUri;

    private const string Params = """
        <output:serialization-parameters xmlns:output="http://www.w3.org/2010/xslt-xquery-serialization">
          <output:method value="text"/>
        </output:serialization-parameters>
        """;

    public DirectoryCollectionAndHostOnlyLoadsTests()
    {
        _allowed = Directory.CreateDirectory(Path.Combine(_dir, "allowed")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.xml"), "<s>outside-content</s>");
        File.WriteAllText(Path.Combine(_allowed, "ok.xml"), "<s>inside-content</s>");
        File.WriteAllText(Path.Combine(_allowed, "params.xml"), Params.Replace("text", "xml", StringComparison.Ordinal));
        File.CreateSymbolicLink(Path.Combine(_allowed, "link.xml"), Path.Combine(outside, "secret.xml"));
        Directory.CreateSymbolicLink(Path.Combine(_allowed, "linked"), outside);
        _allowedUri = new Uri(_allowed).AbsoluteUri;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ResourcePolicy DirectoryOnly => ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).Build();

    /// <summary>The only source of resources; it supplies what it is given and nothing else.</summary>
    private sealed class OnlySource(Dictionary<string, string> content) : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => true;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location);
            foreach (var (name, text) in content)
            {
                if (request.Location.EndsWith(name, StringComparison.Ordinal))
                    return new ResourceContent(text, new Uri(request.Location));
            }
            return null;
        }
    }

    private static async Task<string> RunAsync(ResourcePolicy? policy, string declarations)
    {
        var transformer = new XsltTransformer { ResourcePolicy = policy };
        try
        {
            await transformer.LoadStylesheetAsync($"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  {declarations}
                </xsl:stylesheet>
                """, new Uri("urn:test:main.xsl"));
            return (await transformer.TransformAsync("<x/>")).Trim();
        }
#pragma warning disable CA1031 // any failure is the outcome under test
        catch (Exception e)
#pragma warning restore CA1031
        {
            return "error: " + e.Message;
        }
    }

    private static string Select(string expression)
        => $"<xsl:output omit-xml-declaration=\"yes\"/><xsl:template match=\"/\"><out><xsl:value-of select=\"{expression}\"/></out></xsl:template>";

    // ── fn:collection over a directory ──

    [Theory]
    [InlineData("")]
    [InlineData("?select=*.xml")]
    [InlineData("?select=*.xml;recurse=yes")]
    public async Task A_link_in_an_allowed_directory_that_points_outside_it_is_not_read(string query)
    {
        var result = await RunAsync(DirectoryOnly, Select($"string-join(collection('{_allowedUri}{query}') ! string(.), '|')"));

        result.Should().Contain("inside-content").And.NotContain("outside-content");
    }

    [Fact]
    public async Task With_no_policy_the_link_is_read_as_before()
        => (await RunAsync(null, Select($"string-join(collection('{_allowedUri}?select=*.xml') ! string(.), '|')")))
            .Should().Contain("inside-content").And.Contain("outside-content");

    // ── fn:stream-available ──

    [Fact]
    public async Task Stream_available_asks_a_resolver_that_is_the_only_source_and_nothing_else()
    {
        var resolver = new OnlySource(new() { ["host.xml"] = "<a/>", ["text.xml"] = "not xml" });
        var policy = ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).WithResourceResolver(resolver).Build();

        // ok.xml is a real file the rules allow; the resolver does not supply it.
        var result = await RunAsync(policy, Select(
            $"stream-available('{_allowedUri}/ok.xml'), stream-available('urn:x:host.xml'), stream-available('urn:x:text.xml'), stream-available('urn:x:none.xml')"));

        result.Should().Be("<out>false true false false</out>");
        resolver.Asked.Should().Contain(a => a.EndsWith("ok.xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stream_available_still_reads_an_allowed_file_when_no_resolver_is_the_only_source()
        => (await RunAsync(DirectoryOnly, Select($"stream-available('{_allowedUri}/ok.xml'), stream-available('{_allowedUri}/link.xml')")))
            .Should().Be("<out>true false</out>");

    // ── parameter documents ──

    [Fact]
    public async Task The_parameter_document_of_xsl_output_comes_from_the_only_source()
    {
        // The file says method xml; the resolver's copy says text.
        var resolver = new OnlySource(new() { ["params.xml"] = Params });
        var policy = ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).WithResourceResolver(resolver).Build();

        var result = await RunAsync(policy,
            $"<xsl:output parameter-document=\"{_allowedUri}/params.xml\"/><xsl:template match=\"/\"><out>plain</out></xsl:template>");

        result.Should().Be("plain");
        resolver.Asked.Should().Contain(a => a.EndsWith("params.xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_parameter_document_the_only_source_does_not_supply_is_not_read_from_disk()
    {
        var resolver = new OnlySource([]);
        var policy = ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).WithResourceResolver(resolver).Build();

        var result = await RunAsync(policy,
            $"<xsl:output parameter-document=\"{_allowedUri}/params.xml\"/><xsl:template match=\"/\"><out>plain</out></xsl:template>");

        result.Should().StartWith("error:").And.Contain("XTSE0010");
    }

    [Fact]
    public async Task The_parameter_document_of_xsl_result_document_comes_from_the_only_source()
    {
        var resolver = new OnlySource(new() { ["params.xml"] = Params });
        var policy = ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed)
            .AllowWriteTo("file", pathPrefix: _allowed).WithResourceResolver(resolver).Build();

        var result = await RunAsync(policy,
            $"<xsl:template match=\"/\"><xsl:result-document href=\"{_allowedUri}/out.txt\" parameter-document=\"{_allowedUri}/params.xml\"><out>plain</out></xsl:result-document></xsl:template>");

        result.Should().NotStartWith("error:");
        resolver.Asked.Should().Contain(a => a.EndsWith("params.xml", StringComparison.Ordinal));
    }
}
