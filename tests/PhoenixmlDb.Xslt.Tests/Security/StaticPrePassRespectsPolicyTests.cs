using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// Static parameters and variables are collected from a stylesheet's modules before the modules
/// are parsed, because a shadow attribute or a use-when anywhere may refer to them. That first
/// walk reaches the modules the same way the parser does: through the host's resolver, and
/// through the resource policy. It used to open the file an href names directly.
/// </summary>
public sealed class StaticPrePassRespectsPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-static-prepass").FullName;

    public StaticPrePassRespectsPolicyTests()
        => File.WriteAllText(Path.Combine(_dir, "secret.xsl"), Module("on-disk"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Module(string value) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:param name="secret" static="yes" select="'{value}'"/>
        </xsl:stylesheet>
        """;

    /// <summary>Uses the static parameter of a module in a shadow attribute.</summary>
    private static string Sheet(string moduleRef) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output omit-xml-declaration="yes"/>
          {moduleRef}
          <xsl:template match="/"><out><xsl:value-of _select="'[{"{$secret}"}]'"/></out></xsl:template>
        </xsl:stylesheet>
        """;

    private Uri MainUri => new(Path.Combine(_dir, "main.xsl"));

    private sealed class Resolver(bool suppliesAll, string? module) : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];
        public override bool SuppliesAllContent => suppliesAll;

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location);
            return module is not null && request.Location.EndsWith("secret.xsl", StringComparison.Ordinal)
                ? new ResourceContent(module, new Uri("urn:test:secret.xsl"))
                : null;
        }
    }

    private async Task<string> RunAsync(ResourcePolicy? policy, string moduleRef)
    {
        try
        {
            var transformer = new XsltTransformer { ResourcePolicy = policy };
            await transformer.LoadStylesheetAsync(Sheet(moduleRef), MainUri);
            return (await transformer.TransformAsync("<x/>")).Trim();
        }
        catch (Exception e) when (e.GetType().Name.Contains("Xslt", StringComparison.Ordinal))
        {
            return "error: " + e.Message;
        }
    }

    private static string Ref(string kind, bool excluded = false)
        => $"<xsl:{kind} href=\"secret.xsl\"{(excluded ? " use-when=\"false()\"" : "")}/>";

    private static ResourcePolicy With(Resolver resolver)
        => ResourcePolicy.CreateBuilder().WithResourceResolver(resolver).Build();

    /// <summary>With no policy the module is a file beside the stylesheet, read as before.</summary>
    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task With_no_policy_the_static_parameter_of_a_file_module_is_in_scope(string kind)
        => (await RunAsync(null, Ref(kind))).Should().Be("<out>[on-disk]</out>");

    /// <summary>
    /// The policy allows no file. The module is excluded by use-when, so the parser never asks
    /// for it and nothing reports the refusal: the first walk alone decided whether the file
    /// was read, and its static parameter reached the result.
    /// </summary>
    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task A_file_the_policy_refuses_gives_no_static_declarations(string kind)
        => (await RunAsync(ResourcePolicy.CreateBuilder().Build(), Ref(kind, excluded: true)))
            .Should().NotContain("on-disk");

    /// <summary>The same, with a resolver that is the only source of resources and supplies nothing.</summary>
    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task A_resolver_that_supplies_all_content_keeps_the_first_walk_off_the_file_system(string kind)
    {
        (await RunAsync(With(new Resolver(true, null)), Ref(kind, excluded: true))).Should().NotContain("on-disk");
        (await RunAsync(With(new Resolver(true, null)), Ref(kind))).Should().StartWith("error: XTSE0165");
    }

    /// <summary>
    /// The resolver supplies the module, and a file of the same name exists. The declarations
    /// come from the module the stylesheet is built from, not from the file.
    /// </summary>
    [Theory]
    [InlineData("import", true)]
    [InlineData("include", true)]
    [InlineData("import", false)]
    [InlineData("include", false)]
    public async Task The_static_declarations_come_from_the_module_the_resolver_supplies(string kind, bool suppliesAll)
    {
        var resolver = new Resolver(suppliesAll, Module("from-resolver"));

        (await RunAsync(With(resolver), Ref(kind))).Should().Be("<out>[from-resolver]</out>");
        resolver.Asked.Should().ContainSingle();
    }

    /// <summary>A module that use-when excludes contributes nothing, policy or none.</summary>
    [Theory]
    [InlineData("import")]
    [InlineData("include")]
    public async Task An_excluded_module_gives_no_static_declarations(string kind)
    {
        var resolver = new Resolver(true, Module("from-resolver"));

        (await RunAsync(null, Ref(kind, excluded: true))).Should().NotContain("on-disk");
        (await RunAsync(With(resolver), Ref(kind, excluded: true))).Should().NotContain("from-resolver");
        resolver.Asked.Should().BeEmpty();
    }

    /// <summary>A module the resolver supplies under a base that is no file has its declarations found.</summary>
    [Fact]
    public async Task The_declarations_of_a_supplied_module_are_found_under_any_base()
    {
        var transformer = new XsltTransformer { ResourcePolicy = With(new Resolver(true, Module("from-resolver"))) };
        await transformer.LoadStylesheetAsync(Sheet(Ref("include")), new Uri("urn:test:main.xsl"));

        (await transformer.TransformAsync("<x/>")).Trim().Should().Be("<out>[from-resolver]</out>");
    }
    /// <summary>
    /// The href is absolute, so no base the host gives the module keeps it off the file system.
    /// The module is one the resolver supplied, under the host's own scheme.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_absolute_file_href_is_not_opened_by_the_first_walk(bool excluded)
    {
        var href = new Uri(Path.Combine(_dir, "secret.xsl")).AbsoluteUri;
        var moduleRef = $"<xsl:include href=\"{href}\"{(excluded ? " use-when=\"false()\"" : "")}/>";
        foreach (var policy in new[] { With(new Resolver(true, null)), ResourcePolicy.CreateBuilder().Build() })
        {
            string result;
            try
            {
                var transformer = new XsltTransformer { ResourcePolicy = policy };
                await transformer.LoadStylesheetAsync(Sheet(moduleRef), new Uri("urn:test:main.xsl"));
                result = (await transformer.TransformAsync("<x/>")).Trim();
            }
            catch (Exception e) when (e.GetType().Name.Contains("Xslt", StringComparison.Ordinal))
            {
                result = "error: " + e.Message;
            }
            result.Should().NotContain("on-disk");
            if (!excluded) result.Should().StartWith("error: XTSE0165");
        }
    }
}
