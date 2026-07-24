using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT-level wiring of the Core <see cref="PhoenixmlDb.Core.Xml.XIncludeProcessor"/> into the
/// principal-source loader (XInclude SP1). When <c>EnableXInclude</c> is set and a source base
/// URI is provided, an <c>xi:include</c> in the input is expanded before transformation, and
/// <c>base-uri()</c> of the included content reflects the origin file (W3C base-uri-052 shape).
/// </summary>
public sealed class XIncludeTests : IDisposable
{
    private readonly string _dir;

    public XIncludeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxdb-xi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static Uri FileUri(string path) => new(path, UriKind.Absolute);

    [Fact]
    public async Task Principal_source_xi_include_is_expanded_before_transform()
    {
        // a.xml holds the included content; the master xi:includes it by relative href.
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.xml"), "<item>included-two</item>");
        var masterPath = Path.Combine(_dir, "master.xml");
        const string master = """
            <doc xmlns:xi="http://www.w3.org/2001/XInclude">
              <chap><xi:include href="a.xml"/></chap>
            </doc>
            """;
        await File.WriteAllTextAsync(masterPath, master);

        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xml" omit-xml-declaration="yes"/>
              <t:template match="/">
                <out><t:value-of select="//item"/></out>
              </t:template>
            </t:transform>
            """;

        var transformer = new XsltTransformer();
        transformer.SetSourceDocumentUri(FileUri(masterPath));
        transformer.EnableXInclude();
        await transformer.LoadStylesheetAsync(ss, new Uri("file:///tmp/xi/style.xsl"));

        var result = (await transformer.TransformAsync(master)).Trim();
        result.Should().Be("<out>included-two</out>");
    }

    [Fact]
    public async Task Base_uri_of_included_element_reflects_the_include_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.xml"), "<item>x</item>");
        var masterPath = Path.Combine(_dir, "master.xml");
        const string master = """
            <doc xmlns:xi="http://www.w3.org/2001/XInclude">
              <chap><xi:include href="a.xml"/></chap>
            </doc>
            """;
        await File.WriteAllTextAsync(masterPath, master);

        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xml" omit-xml-declaration="yes"/>
              <t:template match="/">
                <out><t:value-of select="base-uri(//item)"/></out>
              </t:template>
            </t:transform>
            """;

        var transformer = new XsltTransformer();
        transformer.SetSourceDocumentUri(FileUri(masterPath));
        transformer.EnableXInclude();
        await transformer.LoadStylesheetAsync(ss, new Uri("file:///tmp/xi/style.xsl"));

        var result = (await transformer.TransformAsync(master)).Trim();
        result.Should().Contain("a.xml");
        result.Should().StartWith("<out>file:");
    }

    [Fact]
    public async Task Xi_include_is_left_untouched_when_expansion_is_off()
    {
        // Without EnableXInclude, the xi:include element is inert markup — no <item> appears.
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.xml"), "<item>included</item>");
        var masterPath = Path.Combine(_dir, "master.xml");
        const string master = """
            <doc xmlns:xi="http://www.w3.org/2001/XInclude">
              <chap><xi:include href="a.xml"/></chap>
            </doc>
            """;
        await File.WriteAllTextAsync(masterPath, master);

        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xml" omit-xml-declaration="yes"/>
              <t:template match="/">
                <out count="{count(//item)}"/>
              </t:template>
            </t:transform>
            """;

        var transformer = new XsltTransformer();
        transformer.SetSourceDocumentUri(FileUri(masterPath));
        await transformer.LoadStylesheetAsync(ss, new Uri("file:///tmp/xi/style.xsl"));

        var result = (await transformer.TransformAsync(master)).Trim();
        result.Should().Be("<out count=\"0\"/>");
    }
}
