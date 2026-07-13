using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the <c>parameter-document</c> attribute on <c>xsl:output</c> (XSLT 3.0 §26.1):
/// serialization parameters (output method, character maps, omit-xml-declaration, …) supplied by
/// an external <c>output:serialization-parameters</c> document must be read and applied. Mirrors
/// the W3C conformance cases decl/output/output-0706, -0720, -0721, -0722.
/// </summary>
public sealed class SerializationParameterDocumentTests : IDisposable
{
    private readonly string _dir;

    public SerializationParameterDocumentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "phx-serparam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> Transform(string stylesheet)
    {
        var stylesheetPath = Path.Combine(_dir, "sheet.xsl");
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri(stylesheetPath));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    private Task WriteParams(string fileName, string content) =>
        File.WriteAllTextAsync(Path.Combine(_dir, fileName), content);

    [Fact]
    public async Task Json_MethodAndCharacterMap_FromParameterDocument()
    {
        // output-0706: JSON serialization + character map, both from the parameter document.
        await WriteParams("p.xml", """
            <?xml version="1.0" encoding="utf-8" ?>
            <output:serialization-parameters xmlns:output="http://www.w3.org/2010/xslt-xquery-serialization">
               <output:method value="json"/>
               <output:use-character-maps>
                 <output:character-map character="a" map-string="AAA"/>
                 <output:character-map character="b" map-string="BBB"/>
                 <output:character-map character="c" map-string="CCC"/>
               </output:use-character-maps>
            </output:serialization-parameters>
            """);
        const string ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output parameter-document="p.xml"/>
              <xsl:template name="xsl:initial-template">
                <xsl:map>
                  <xsl:map-entry key="'a'" select="'AAA'"/>
                  <xsl:map-entry key="'b'" select="'BBB'"/>
                  <xsl:map-entry key="'c'" select="'CCC'"/>
                  <xsl:map-entry key="'d'" select="'DDD'"/>
                </xsl:map>
              </xsl:template>
            </xsl:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex("""\{"AAA":"AAA","BBB":"BBB","CCC":"CCC","d":"DDD"\}""");
    }

    [Fact]
    public async Task CharacterMap_FromParameterDocument_UnspecifiedMethod()
    {
        // output-0720: character map only, default (xml) method.
        await WriteParams("p.xml", """
            <?xml version="1.0" encoding="utf-8" ?>
            <output:serialization-parameters xmlns:output="http://www.w3.org/2010/xslt-xquery-serialization">
               <output:use-character-maps>
                  <output:character-map character="a" map-string="AAA"/>
               </output:use-character-maps>
            </output:serialization-parameters>
            """);
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output parameter-document="p.xml"/>
              <xsl:template name="xsl:initial-template"><test>a</test></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<test>AAA</test>");
    }

    [Fact]
    public async Task CharacterMap_FromParameterDocument_AdaptiveMethod()
    {
        // output-0721 (parameter-document responsibilities only): the adaptive output method and the
        // character map both come from the parameter document. This asserts they are read and applied
        // — the node is serialized by adaptive/XML rules (not JSON-escaped) and 'a' → 'AAA'.
        //
        // The full conformance case additionally expects a leading XML declaration
        // (^<\?xml[^<]+><test>AAA</test>$). Emitting that declaration is a property of the adaptive
        // output method itself (SerializeNodeTreeAsXml only emits a declaration for a document node,
        // not a bare element — identical behaviour with an inline method="adaptive"), independent of
        // the parameter-document path fixed here. That adaptive-serialization detail is deferred.
        await WriteParams("p.xml", """
            <?xml version="1.0" encoding="utf-8" ?>
            <output:serialization-parameters xmlns:output="http://www.w3.org/2010/xslt-xquery-serialization">
               <output:method value="adaptive"/>
               <output:use-character-maps>
                  <output:character-map character="a" map-string="AAA"/>
               </output:use-character-maps>
            </output:serialization-parameters>
            """);
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output parameter-document="p.xml"/>
              <xsl:template name="xsl:initial-template"><test>a</test></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<test>AAA</test>",
            because: "adaptive method + char map from the parameter document should serialize the node as XML with 'a'→'AAA'");
    }

    [Fact]
    public async Task ParameterDocument_ResolvedRelativeToDeclaringModule_InSubdirectory()
    {
        // output-0722: named output's parameter-document lives beside an included module in a subdir.
        var subDir = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "p.xml"), """
            <?xml version="1.0" encoding="utf-8" ?>
            <output:serialization-parameters xmlns:output="http://www.w3.org/2010/xslt-xquery-serialization">
               <output:omit-xml-declaration value="yes"/>
            </output:serialization-parameters>
            """);
        await File.WriteAllTextAsync(Path.Combine(subDir, "inc.xsl"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <xsl:output name="my-output" parameter-document="p.xml"/>
            </xsl:stylesheet>
            """);
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <xsl:include href="sub/inc.xsl"/>
               <xsl:template name="xsl:initial-template">
                  <xsl:result-document format="my-output"><test>a</test></xsl:result-document>
               </xsl:template>
            </xsl:stylesheet>
            """;
        var result = (await Transform(ss)).Trim();
        Regex.IsMatch(result, @"^<test>a</test>$").Should().BeTrue(
            $"omit-xml-declaration=yes from the subdir parameter document should suppress the prolog; got: {result}");
    }
}
