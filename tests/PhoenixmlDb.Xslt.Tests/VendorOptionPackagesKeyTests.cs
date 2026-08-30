using FluentAssertions;
using PhoenixmlDb.Core;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Saxon has two namespaces here and conflating them costs the whole option.
///
///   http://saxon.sf.net/ns/configuration   the CONFIGURATION DOCUMENT element
///   http://saxon.sf.net/                   the EXTENSION namespace - saxon:threads, and the
///                                          vendor-options map KEY
///
/// <c>BuildCatalog</c> originally matched the key against the configuration namespace. Nothing
/// matched, the option was skipped, and five XSpec package suites went on raising XTDE3052
/// "Package not found" even though the document-side parsing underneath was correct. XSpec
/// writes the key as <c>QName('http://saxon.sf.net/', 'configuration')</c>.
///
/// These tests go straight at BuildCatalog rather than through fn:transform: the defect is a
/// key comparison, and an end-to-end test would need package files on disk and could fail for
/// a dozen unrelated reasons.
/// </summary>
public class VendorOptionPackagesKeyTests
{
    private const string ConfigNs = "http://saxon.sf.net/ns/configuration";
    private const string ExtensionNs = "http://saxon.sf.net/";

    private const string Config = $"""
        <configuration xmlns="{ConfigNs}">
          <xsltPackages>
            <package name="http://example.org/filter.xsl" sourceLocation="filter.xsl" version="1.0"/>
          </xsltPackages>
        </configuration>
        """;

    /// <summary>
    /// BuildCatalog takes a serializer because its two callers differ in how they serialize.
    /// The catalog only ever reads the config back as text, so the test supplies the text.
    /// </summary>
    private static Dictionary<string, List<(string? Version, string FilePath)>>? Build(string keyNamespace)
    {
        // BuildCatalog only reads BaseUri off this node - the serializer delegate supplies the
        // text - so the cheapest concrete XdmNode is enough. Constructing an XdmElement would
        // mean satisfying seven required members that this test never looks at.
        var node = new Xdm.Nodes.XdmText
        {
            Value = "",
            Id = default,
            Document = default,
            BaseUri = "file:///pkgs/config.xml",
        };
        var vendorOptions = new Dictionary<object, object?>
        {
            // fn:QName(uri, local) populates RuntimeNamespace, which is exactly the shape
            // BuildCatalog has to read - the whole class of bug this guards.
            [new QName(NamespaceId.None, "configuration") { RuntimeNamespace = keyNamespace }] = node,
        };
        var options = new Dictionary<object, object?> { ["vendor-options"] = vendorOptions };
        return VendorOptionPackages.BuildCatalog(options, _ => Config, new Uri("file:///pkgs/config.xml"));
    }

    [Fact]
    public void ExtensionNamespaceKey_IsRecognised()
    {
        var catalog = Build(ExtensionNs);
        catalog.Should().NotBeNull("XSpec writes QName('http://saxon.sf.net/', 'configuration')");
        catalog!.Should().ContainKey("http://example.org/filter.xsl");
    }

    /// <summary>
    /// Still accepted: a producer reaching for the document's own namespace is unambiguously
    /// naming Saxon's option too. This was the only namespace accepted before, so keeping it
    /// means the fix is purely additive.
    /// </summary>
    [Fact]
    public void ConfigurationNamespaceKey_IsAlsoRecognised()
    {
        Build(ConfigNs).Should().NotBeNull();
    }

    /// <summary>
    /// The matching must stay strict on the full name. Another vendor's option that happens to
    /// use the local name "configuration" is not ours to read, and a fix that matched on local
    /// name alone would pass both tests above while claiming everyone else's options.
    /// </summary>
    [Fact]
    public void AnotherVendorsConfigurationKey_IsIgnored()
    {
        Build("http://example.com/some-other-processor").Should().BeNull();
    }

    [Fact]
    public void SourceLocationIsResolvedAgainstTheConfigFile_NotTheUsingStylesheet()
    {
        var catalog = Build(ExtensionNs);
        catalog!["http://example.org/filter.xsl"].Should().ContainSingle()
            .Which.FilePath.Should().Be("/pkgs/filter.xsl",
                "locations in the config are relative to the config file");
    }
}
