using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Package locations supplied to <c>fn:transform</c> through <c>vendor-options</c>.
///
/// <c>vendor-options</c> is deliberately unstandardised, so a reader must recognise some
/// concrete vocabulary. Two are recognised: this engine's own, and Saxon's configuration
/// element — the latter purely so stylesheets already written against it keep working. The
/// native route exists so declaring a package never requires another implementation's file
/// format; before it, Saxon's was the ONLY way through fn:transform, which made a competitor's
/// format a de facto dependency for a core feature.
/// </summary>
public sealed class VendorOptionPackagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-pkg-" + Guid.NewGuid().ToString("N")[..8]);
    private const string NativeNs = "http://phoenixml.dev/ns/vendor-options";

    public VendorOptionPackagesTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "lib.xsl"), """
            <xsl:package xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                         name="http://example.org/lib.xsl" package-version="1.0"
                         xmlns:f="http://example.org/lib.xsl" exclude-result-prefixes="#all">
              <xsl:function name="f:greet" as="xs:string" visibility="public"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
                <xsl:sequence select="'hello'"/>
              </xsl:function>
            </xsl:package>
            """);
        File.WriteAllText(Path.Combine(_dir, "use.xsl"), """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:f="http://example.org/lib.xsl" exclude-result-prefixes="#all">
              <xsl:use-package name="http://example.org/lib.xsl" version="1.0"/>
              <xsl:template name="xsl:initial-template"><out><xsl:value-of select="f:greet()"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        File.WriteAllText(Path.Combine(_dir, "config.xml"), """
            <configuration xmlns="http://saxon.sf.net/ns/configuration">
              <xsltPackages>
                <package name="http://example.org/lib.xsl" sourceLocation="lib.xsl" version="1.0"/>
              </xsltPackages>
            </configuration>
            """);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task<string> Run(string vendorOptions)
    {
        PhoenixmlDb.XQuery.Functions.TransformFunction.Provider ??= new PhoenixmlDb.Xslt.XsltTransformProvider();
        var use = new Uri(Path.Combine(_dir, "use.xsl")).AbsoluteUri;
        var xq = $$"""
            transform(map {
              'stylesheet-location' : '{{use}}',
              'initial-template'    : QName('http://www.w3.org/1999/XSL/Transform','initial-template'),
              'delivery-format'     : 'serialized',
              'vendor-options'      : {{vendorOptions}}
            })?output
            """;
        return await new PhoenixmlDb.XQuery.XQueryFacade().EvaluateAsync(xq);
    }

    /// <summary>The native route: no other implementation's format required.</summary>
    [Fact]
    public async Task Native_vocabulary_declares_a_package()
    {
        var loc = new Uri(Path.Combine(_dir, "lib.xsl")).AbsoluteUri;
        (await Run($"map {{ QName('{NativeNs}','packages') : map {{ 'http://example.org/lib.xsl' : '{loc}' }} }}"))
            .Should().Contain("hello");
    }

    /// <summary>The richer native form, carrying a version alongside the location.</summary>
    [Fact]
    public async Task Native_vocabulary_accepts_location_and_version()
    {
        var loc = new Uri(Path.Combine(_dir, "lib.xsl")).AbsoluteUri;
        (await Run($"map {{ QName('{NativeNs}','packages') : map {{ 'http://example.org/lib.xsl' : map {{ 'location' : '{loc}', 'version' : '1.0' }} }} }}"))
            .Should().Contain("hello");
    }

    /// <summary>Saxon's configuration keeps working — that is the whole point of reading it.</summary>
    [Fact]
    public async Task Saxon_configuration_is_still_honoured()
    {
        var cfg = new Uri(Path.Combine(_dir, "config.xml")).AbsoluteUri;
        (await Run($"map {{ QName('http://saxon.sf.net/ns/configuration','configuration') : doc('{cfg}') }}"))
            .Should().Contain("hello");
    }

    /// <summary>
    /// Another vendor's option is LEFT ALONE, not claimed. The first version matched the bare
    /// local name "configuration" in any namespace, so a foreign option of that name would have
    /// been seized and scanned — and, being taken first, would have masked a real Saxon config.
    /// </summary>
    [Fact]
    public async Task A_foreign_configuration_option_is_not_claimed()
    {
        var cfg = new Uri(Path.Combine(_dir, "config.xml")).AbsoluteUri;
        var act = async () => await Run(
            $"map {{ QName('http://example.com/other-vendor','configuration') : doc('{cfg}') }}");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3052",
            "a foreign vendor's option must be ignored, leaving the package genuinely unresolved");
    }

    /// <summary>A foreign option alongside Saxon's must not hide it.</summary>
    [Fact]
    public async Task A_foreign_option_does_not_mask_a_real_configuration()
    {
        var cfg = new Uri(Path.Combine(_dir, "config.xml")).AbsoluteUri;
        (await Run($"map {{ QName('http://example.com/other','configuration') : 'irrelevant', "
                 + $"QName('http://saxon.sf.net/ns/configuration','configuration') : doc('{cfg}') }}"))
            .Should().Contain("hello");
    }
}
