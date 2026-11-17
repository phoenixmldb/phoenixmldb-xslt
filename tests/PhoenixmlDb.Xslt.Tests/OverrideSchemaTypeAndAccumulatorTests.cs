using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Overriding across packages that each import a schema, and accumulators local to a package.
/// <list type="bullet">
/// <item>A type name without a prefix (as="u1") names a schema type in no namespace; it was taken
/// for item(). Two plain union types are identical when they have the same member types
/// (XSLT 3.0 §3.5.3.3), so an override may change the name and the order, not the members
/// (XTSE3070; W3C override-v-005, -006).</item>
/// <item>A used package's schema defines the types its components declare: a function parameter
/// of a union type casts an untyped value to the first member type that accepts it (W3C
/// override-f-031).</item>
/// <item>use-accumulators names the accumulator of the package that runs the instruction, as
/// accumulator-after() does, also where the using package declares one by the same name (W3C
/// override-misc-007).</item>
/// </list>
/// </summary>
public sealed class OverrideSchemaTypeAndAccumulatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-ovschema-" + Guid.NewGuid().ToString("N"));

    public OverrideSchemaTypeAndAccumulatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Union(string name, string members) => $"""
        <xsl:import-schema><xs:schema><xs:simpleType name="{name}"><xs:union memberTypes="{members}"/></xs:simpleType></xs:schema></xsl:import-schema>
        """;

    private const string Head = """version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all" expand-text="yes" """;

    private async Task<string> RunAsync(string library, string principal, string? input = null)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync(input ?? "<in/>")).Trim();
    }

    private static string VariableLibrary => $"""
        <xsl:package name="urn:lib" package-version="1.0" {Head}>
          {Union("u1", "xs:date xs:time xs:dateTime")}
          <xsl:variable name="var" as="u1" select="current-dateTime()" visibility="public"/>
        </xsl:package>
        """;

    private static string VariableOverride(string union) => $"""
        <xsl:package name="urn:main" package-version="1.0" {Head}>
          {union}
          <xsl:use-package name="urn:lib" package-version="1.0">
            <xsl:override><xsl:variable name="var" as="u2" select="current-date()" visibility="public"/></xsl:override>
          </xsl:use-package>
          <xsl:template name="main" visibility="public"><out>{"{"}$var instance of xs:date{"}"}</out></xsl:template>
        </xsl:package>
        """;

    [Fact]
    public async Task An_override_may_name_a_union_with_the_same_members_in_another_order()
        => (await RunAsync(VariableLibrary, VariableOverride(Union("u2", "xs:time xs:dateTime xs:date")))).Should().Be("<out>true</out>");

    [Fact]
    public async Task An_override_that_changes_the_members_of_a_union_is_XTSE3070()
    {
        var run = () => RunAsync(VariableLibrary, VariableOverride(Union("u2", "xs:dateTime xs:date")));
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE3070");
    }

    [Fact]
    public async Task A_parameter_of_a_union_type_from_the_used_package_casts_an_untyped_value()
        => (await RunAsync($"""
                <xsl:package name="urn:lib" package-version="1.0" {Head}>
                  {Union("u1", "xs:decimal xs:double")}
                  <xsl:function name="f:b" visibility="public" as="u1"><xsl:param name="x" as="u1"/><xsl:sequence select="$x"/></xsl:function>
                </xsl:package>
                """, $"""
                <xsl:stylesheet {Head}>
                  <xsl:use-package name="urn:lib" package-version="1.0"/>
                  <xsl:template name="main"><out>{"{"}f:b(xs:untypedAtomic('12')) instance of xs:decimal{"}"}</out></xsl:template>
                </xsl:stylesheet>
                """)).Should().Be("<out>true</out>");

    [Fact]
    public async Task Each_package_streams_its_own_accumulator_of_a_shared_name()
    {
        var input = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(input, "<r><a/><b/><c/></r>", TestContext.Current.CancellationToken);
        var href = new Uri(input).AbsoluteUri;
        (await RunAsync($$"""
                <xsl:package name="urn:lib" package-version="1.0" {{Head}}>
                  <xsl:accumulator name="ac" initial-value="0" streamable="yes"><xsl:accumulator-rule match="*" select="$value + 1"/></xsl:accumulator>
                  <xsl:template name="use-base" visibility="public">
                    <xsl:source-document streamable="yes" href="{{href}}" use-accumulators="ac"><a>{accumulator-after('ac')}</a></xsl:source-document>
                  </xsl:template>
                </xsl:package>
                """, $$"""
                <xsl:package name="urn:main" package-version="1.0" {{Head}}>
                  <xsl:use-package name="urn:lib" package-version="1.0"/>
                  <xsl:accumulator name="ac" initial-value="0" streamable="yes"><xsl:accumulator-rule match="*" select="$value - 1"/></xsl:accumulator>
                  <xsl:template name="main" visibility="public">
                    <out><xsl:call-template name="use-base"/>
                      <xsl:source-document streamable="yes" href="{{href}}" use-accumulators="ac"><b>{accumulator-after('ac')}</b></xsl:source-document></out>
                  </xsl:template>
                </xsl:package>
                """)).Should().Be("<out><a>4</a><b>-4</b></out>");
    }
}
