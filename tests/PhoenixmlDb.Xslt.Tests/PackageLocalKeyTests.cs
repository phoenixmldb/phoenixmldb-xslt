using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:key declarations are LOCAL to the package that declares them (XSLT 3.0 §3.6.2).
/// A key name declared in a used (library) package is not visible to the using package,
/// and a key name of the same local name declared in two different packages indexes
/// independently. Mirrors W3C decl/use-package cases use-package-102 and use-package-105.
/// </summary>
public sealed class PackageLocalKeyTests : IDisposable
{
    private readonly string _dir;

    public PackageLocalKeyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string fileName, string xml)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private static Dictionary<string, List<(string? Version, string FilePath)>> Cat(
        params (string Name, string Path)[] entries)
    {
        var cat = new Dictionary<string, List<(string?, string)>>();
        foreach (var (name, path) in entries)
            cat[name] = new List<(string?, string)> { ("1.0.0", path) };
        return cat;
    }

    // The base (library) package declares key 'k' on column[1]; a public function
    // find-base() looks it up. Shared across both tests.
    private const string BaseWithKeyOnColumn1 = """
        <xsl:package name="urn:base-k" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema"
          xmlns:p="urn:base-k" exclude-result-prefixes="xs p">
          <xsl:key name="k" match="row" use="column[1]"/>
          <xsl:function name="p:find-base" as="element(row)?" visibility="public">
            <xsl:param name="data" as="document-node()"/>
            <xsl:param name="search" as="xs:string"/>
            <xsl:sequence select="key('k', $search, $data)"/>
          </xsl:function>
        </xsl:package>
        """;

    private const string DataVariable = """
        <xsl:variable name="data">
          <row><column>aaa</column><column>bbb</column><column>one</column></row>
          <row><column>ccc</column><column>ddd</column><column>two</column></row>
          <row><column>eee</column><column>aaa</column><column>three</column></row>
        </xsl:variable>
        """;

    // use-package-102: the using package declares its OWN key 'k' on column[2].
    // key('k') inside the base package must still see only column[1]; key('k') inside
    // the using package must see only column[2]. They must NOT merge into one index.
    [Fact]
    public async Task Keys_AreLocalToDeclaringPackage_SameNameDifferentMatch()
    {
        var basePath = Write("base-k.xsl", BaseWithKeyOnColumn1);
        var principal = $$"""
            <xsl:package name="urn:main-k" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-k" xmlns:q="urn:main-k" exclude-result-prefixes="xs p q">
              <xsl:use-package name="urn:base-k" package-version="1.0.0"/>
              <xsl:key name="k" match="row" use="column[2]"/>
              <xsl:function name="q:find-here" as="element(row)?" visibility="public">
                <xsl:param name="data" as="document-node()"/>
                <xsl:param name="search" as="xs:string"/>
                <xsl:sequence select="key('k', $search, $data)"/>
              </xsl:function>
              <xsl:template name="main" visibility="public">
                <out p="{p:find-base($data, 'aaa')/column[3]}" q="{q:find-here($data, 'aaa')/column[3]}"/>
              </xsl:template>
              {{DataVariable}}
            </xsl:package>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "main-k.xsl")),
            null, Cat(("urn:base-k", basePath)));
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");

        // base key (column[1]): 'aaa' -> row 1 -> column[3] = 'one'
        // local key (column[2]): 'aaa' -> row 3 -> column[3] = 'three'
        Assert.Contains("p=\"one\"", result, StringComparison.Ordinal);
        Assert.Contains("q=\"three\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("one three", result, StringComparison.Ordinal);
    }

}
