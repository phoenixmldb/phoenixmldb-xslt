using System.Text;
using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// End-to-end integration tests exercising the public <see cref="XsltTransformer"/> API.
/// Each test creates an XsltTransformer, loads a stylesheet, transforms input, and verifies output.
/// </summary>
public class XsltTransformerIntegrationTests
{
    #region 1. Basic transformation

    [Fact]
    public async Task Transform_identity_produces_same_xml()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode on-no-match="shallow-copy"/>
            </xsl:stylesheet>
            """);

        var input = "<root><child>text</child></root>";
        var result = await transformer.TransformAsync(input);

        result.Should().Contain("<root>");
        result.Should().Contain("<child>text</child>");
        result.Should().Contain("</root>");
    }

    [Fact]
    public async Task Transform_with_template_matching()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <output>matched-root</output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<root/>");

        result.Should().Contain("<output>matched-root</output>");
    }

    [Fact]
    public async Task Transform_with_value_of()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result><xsl:value-of select="/data/name"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<data><name>Alice</name></data>");

        result.Should().Contain("<result>Alice</result>");
    }

    #endregion

    #region 2. Serialization options

    [Fact]
    public async Task Output_indent_yes_produces_indented_xml()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output indent="yes"/>
              <xsl:template match="/">
                <root><child>text</child></root>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("\n", "indented output should contain newlines");
    }

    [Fact]
    public async Task Output_indent_no_produces_single_line()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output indent="no"/>
              <xsl:template match="/">
                <root><child>text</child></root>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        // Remove the XML declaration line to check just the body
        var body = result;
        var declEnd = result.IndexOf("?>", StringComparison.Ordinal);
        if (declEnd >= 0)
            body = result[(declEnd + 2)..].TrimStart('\r', '\n');

        body.Should().NotContain("\n", "non-indented output body should be a single line");
    }

    [Fact]
    public async Task Output_doctype_public_and_system()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html"
                          doctype-public="-//W3C//DTD HTML 4.0//EN"
                          doctype-system="http://www.w3.org/TR/html4/strict.dtd"/>
              <xsl:template match="/">
                <html><body>Hello</body></html>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<!DOCTYPE", "output should contain a DOCTYPE declaration");
        result.Should().Contain("-//W3C//DTD HTML 4.0//EN");
    }

    [Fact]
    public async Task Output_method_html()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html"/>
              <xsl:template match="/">
                <html><body><br/></body></html>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        // HTML method should serialize <br> without self-closing slash
        result.Should().Contain("<br>", "HTML method should produce <br>, not <br/>");
    }

    [Fact]
    public async Task Output_method_text()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:text>Hello, World!</xsl:text>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Be("Hello, World!", "text method should produce plain text without markup");
    }

    [Fact]
    public async Task Output_method_json()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="json"/>
              <xsl:template match="/">
                <xsl:map xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:map-entry key="'name'" select="'Alice'"/>
                  <xsl:map-entry key="'age'" select="30"/>
                </xsl:map>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("name", "JSON output should contain the key");
        result.Should().Contain("Alice", "JSON output should contain the value");
    }

    [Fact]
    public async Task Output_omit_xml_declaration_yes()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <root>content</root>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().NotStartWith("<?xml", "output should not contain XML declaration");
    }

    [Fact]
    public async Task Output_encoding_utf8()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output encoding="UTF-8"/>
              <xsl:template match="/">
                <root/>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("encoding=\"UTF-8\"", "XML declaration should specify UTF-8 encoding");
    }

    [Fact]
    public async Task Output_cdata_section_elements()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output cdata-section-elements="data"/>
              <xsl:template match="/">
                <data>some &amp; content</data>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<![CDATA[", "data element content should be wrapped in CDATA");
    }

    [Fact]
    public async Task Output_byte_order_mark_yes()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output byte-order-mark="yes"/>
              <xsl:template match="/">
                <root/>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().StartWith("\uFEFF", "output should start with BOM when byte-order-mark='yes'");
    }

    #endregion

    #region 3. Stream API

    [Fact]
    public async Task TransformAsync_with_TextReader_input()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <output>from-reader</output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        using var reader = new StringReader("<input/>");
        var result = await transformer.TransformAsync(reader);

        result.Should().Contain("<output>from-reader</output>");
    }

    [Fact]
    public async Task TransformAsync_with_Stream_input()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <output>from-stream</output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<input/>"));
        var result = await transformer.TransformAsync(stream);

        result.Should().Contain("<output>from-stream</output>");
    }

    [Fact]
    public async Task TransformAsync_with_TextWriter_output()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <output>to-writer</output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        using var writer = new StringWriter();
        await transformer.TransformAsync("<input/>", writer);
        var result = writer.ToString();

        result.Should().Contain("<output>to-writer</output>");
    }

    [Fact]
    public async Task TransformAsync_stream_to_stream()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <output>stream-to-stream</output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes("<input/>"));
        using var outputStream = new MemoryStream();
        await transformer.TransformAsync(inputStream, outputStream);

        outputStream.Position = 0;
        using var reader = new StreamReader(outputStream);
        var result = await reader.ReadToEndAsync();

        result.Should().Contain("<output>stream-to-stream</output>");
    }

    [Fact]
    public async Task ResultDocumentHandler_writes_secondary_docs()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <primary>main</primary>
                <xsl:result-document href="chapter1.html">
                  <chapter>Chapter 1</chapter>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """, baseUri: new Uri("file:///tmp/test/"));

        var secondaryDocs = new Dictionary<string, StringWriter>();
        transformer.ResultDocumentHandler = href =>
        {
            var sw = new StringWriter();
            secondaryDocs[href] = sw;
            return sw;
        };

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<primary>main</primary>");
        secondaryDocs.Should().NotBeEmpty("ResultDocumentHandler should have been called");
        secondaryDocs.Values.First().ToString().Should().Contain("Chapter 1");
    }

    #endregion

    #region 4. Invocation styles

    [Fact]
    public async Task CallTemplate_invocation()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">
                <output>from-named-template</output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        transformer.SetInitialTemplate("main");
        var result = await transformer.TransformAsync((string?)null);

        result.Should().Contain("<output>from-named-template</output>");
    }

    [Fact]
    public async Task CallFunction_invocation()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:my="http://example.com/my">
              <xsl:function name="my:greet" visibility="public">
                <xsl:param name="name"/>
                <greeting>Hello, <xsl:value-of select="$name"/>!</greeting>
              </xsl:function>
            </xsl:stylesheet>
            """);

        transformer.SetInitialFunction("greet", "http://example.com/my");
        transformer.AddInitialFunctionArgument("World");
        var result = await transformer.TransformAsync((string?)null);

        result.Should().Contain("Hello, World!");
    }

    [Fact]
    public async Task ApplyTemplates_with_mode()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" mode="toc">
                <toc>Table of Contents</toc>
              </xsl:template>
              <xsl:template match="/">
                <body>Default</body>
              </xsl:template>
            </xsl:stylesheet>
            """);

        transformer.SetInitialMode("toc");
        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<toc>Table of Contents</toc>");
        result.Should().NotContain("<body>Default</body>");
    }

    #endregion

    #region 5. Parameters

    [Fact]
    public async Task String_parameter()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="title"/>
              <xsl:template match="/">
                <heading><xsl:value-of select="$title"/></heading>
              </xsl:template>
            </xsl:stylesheet>
            """);

        transformer.SetParameter("title", "Hello");
        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<heading>Hello</heading>");
    }

    [Fact]
    public async Task Typed_parameter_integer()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="count"/>
              <xsl:template match="/">
                <result><xsl:value-of select="$count * 2"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        transformer.SetParameter("count", (object?)42);
        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<result>84</result>");
    }

    [Fact]
    public async Task InitialTemplateParameter()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">
                <xsl:param name="greeting"/>
                <output><xsl:value-of select="$greeting"/></output>
              </xsl:template>
            </xsl:stylesheet>
            """);

        transformer.SetInitialTemplate("main");
        transformer.SetInitialTemplateParameter(
            new QName(NamespaceId.None, "greeting"), "Hi there");
        var result = await transformer.TransformAsync((string?)null);

        result.Should().Contain("<output>Hi there</output>");
    }

    #endregion

    #region 6. Streaming and system-property

    [Fact]
    public async Task Streamable_mode_processes_document()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template match="/">
                <result>streamed</result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<data><item>1</item></data>");

        // Streaming mode should produce output (may differ from non-streaming)
        result.Should().NotBeNullOrEmpty("streaming mode should produce output");
    }

    [Fact]
    public async Task SystemProperty_supports_streaming_returns_yes()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result><xsl:value-of select="system-property('xsl:supports-streaming')"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<result>yes</result>");
    }

    [Fact]
    public async Task SystemProperty_supports_namespace_axis_returns_yes()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result><xsl:value-of select="system-property('xsl:supports-namespace-axis')"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<result>yes</result>");
    }

    [Fact]
    public async Task SystemProperty_version_returns_3_0()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result><xsl:value-of select="system-property('xsl:version')"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<result>3.0</result>");
    }

    [Fact]
    public async Task SystemProperty_xpath_version_returns_4_0()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result><xsl:value-of select="system-property('xsl:xpath-version')"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<result>4.0</result>");
    }

    #endregion

    #region 7. XSLT 4.0 features

    [Fact]
    public async Task Xsl_switch()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="4.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result>
                  <xsl:switch select="/data/status">
                    <xsl:when test="'active'">Active</xsl:when>
                    <xsl:when test="'inactive'">Inactive</xsl:when>
                    <xsl:otherwise>Unknown</xsl:otherwise>
                  </xsl:switch>
                </result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<data><status>active</status></data>");

        result.Should().Contain("Active");
    }

    [Fact]
    public async Task Xsl_for_each_member()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="4.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <result>
                  <xsl:variable name="m" select="map { 'a': 1, 'b': 2 }"/>
                  <xsl:for-each-member select="$m">
                    <entry key="{.?key}"><xsl:value-of select=".?value"/></entry>
                  </xsl:for-each-member>
                </result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<entry");
    }

    [Fact]
    public async Task Xsl_record()
    {
        // xsl:record is an XSLT 4.0 instruction that creates a map with string keys.
        // The parser should accept xsl:record with xsl:entry children.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="4.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:variable name="rec" as="map(*)">
                  <xsl:record>
                    <xsl:entry key="name">Alice</xsl:entry>
                    <xsl:entry key="age">30</xsl:entry>
                  </xsl:record>
                </xsl:variable>
                <result><xsl:value-of select="$rec?name"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("Alice");
    }

    #endregion

    #region 8. Error handling

    [Fact]
    public async Task Invalid_stylesheet_throws_XsltException()
    {
        var transformer = new XsltTransformer();

        var act = () => transformer.LoadStylesheetAsync("<not-a-stylesheet/>");

        await act.Should().ThrowAsync<XsltException>();
    }

    [Fact]
    public async Task OnNoMatch_invalid_value_throws_XTSE0020()
    {
        var transformer = new XsltTransformer();

        var act = () => transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode on-no-match="typo"/>
            </xsl:stylesheet>
            """);

        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.Which.Message.Should().Contain("XTSE0020");
    }

    #endregion

    #region 9. Packages

    [Fact]
    public async Task UsePackage_basic()
    {
        // Create a simple package and load it via the package catalog
        var packageStylesheet = """
            <xsl:package version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                name="http://example.com/utils"
                package-version="1.0">
              <xsl:function name="Q{http://example.com/utils}double" visibility="public">
                <xsl:param name="n"/>
                <xsl:sequence select="$n * 2"/>
              </xsl:function>
            </xsl:package>
            """;

        // Write the package to a temp file
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, packageStylesheet);

            var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
            {
                ["http://example.com/utils"] = [("1.0", tempFile)]
            };

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync("""
                <xsl:stylesheet version="3.0"
                    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                    xmlns:util="http://example.com/utils">
                  <xsl:use-package name="http://example.com/utils"/>
                  <xsl:template match="/">
                    <result><xsl:value-of select="util:double(21)"/></result>
                  </xsl:template>
                </xsl:stylesheet>
                """, packageCatalog: catalog);

            var result = await transformer.TransformAsync("<input/>");

            result.Should().Contain("42");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion

    #region 10. xsl:result-document

    [Fact]
    public async Task ResultDocument_produces_secondary_output()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <primary>main-content</primary>
                <xsl:result-document href="out.xml">
                  <secondary>extra-content</secondary>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """, baseUri: new Uri("file:///tmp/test/"));

        var result = await transformer.TransformAsync("<input/>");

        result.Should().Contain("<primary>main-content</primary>");
        transformer.SecondaryResultDocuments.Should().NotBeEmpty("xsl:result-document should produce secondary output");
        transformer.SecondaryResultDocuments.Values.First().Should().Contain("<secondary>extra-content</secondary>");
    }

    #endregion

    #region fn:transform post-process

    [Fact]
    public async Task Transform_fn_transform_post_process_chains_stylesheets()
    {
        // Write two simple stylesheets to temp files
        var dir = Path.Combine(Path.GetTempPath(), "xslt-postprocess-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var sheet1Path = Path.Combine(dir, "sheet1.xsl");
            var sheet2Path = Path.Combine(dir, "sheet2.xsl");

            await File.WriteAllTextAsync(sheet1Path, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:mode on-no-match="shallow-copy"/>
                  <xsl:template match="root">
                    <root><step1/><xsl:apply-templates/></root>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            await File.WriteAllTextAsync(sheet2Path, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:mode on-no-match="shallow-copy"/>
                  <xsl:template match="root">
                    <root><step2/><xsl:apply-templates/></root>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            // Main stylesheet uses fn:transform with post-process to chain sheet1 → sheet2
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync($$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                  expand-text="yes">
                  <xsl:template match="/" name="xsl:initial-template">
                    <xsl:copy-of select="transform(
                      map {
                        'source-node' : .,
                        'stylesheet-location' : '{{sheet1Path}}',
                        'delivery-format' : 'document',
                        'post-process' : function($uri, $result) {
                          transform(map {
                            'source-node' : $result,
                            'stylesheet-location' : '{{sheet2Path}}',
                            'delivery-format' : 'document'
                          })?output
                        }
                      }
                    )?output"/>
                  </xsl:template>
                </xsl:stylesheet>
                """, baseUri: new Uri("file:///tmp/test/"));

            var result = await transformer.TransformAsync("<root><item>data</item></root>");

            // Both sheet1 (step1) and sheet2 (step2) should have been applied
            result.Should().Contain("<step1");
            result.Should().Contain("<step2");
            result.Should().Contain("data");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion

    #region Regression: prefixed atomic types in cast/castable/instance-of (martin's report)

    // Bug: castable/cast/instance-of with a prefixed atomic type like xs:integer or xs:Name was
    // wrongly raising XPST0051 "unprefixed type names require xpath-default-namespace=...".
    // Root cause: the XQuery parser populated XdmSequenceType.UnprefixedTypeName with the local
    // name regardless of whether the source was prefixed; the XSLT validator then mistook every
    // prefixed reference for an unprefixed one. UnprefixedTypeName is now only set when the
    // source name was actually unprefixed; LocalTypeName carries the local-name component used
    // by cast/castable derived-type checks.

    [Fact]
    public async Task Castable_with_prefixed_xs_integer_compiles()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <r><xsl:value-of select="'42' castable as xs:integer"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">true</r>");
    }

    [Fact]
    public async Task Castable_with_prefixed_xs_Name_compiles()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <r><xsl:value-of select="'foo' castable as xs:Name"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">true</r>");
    }

    [Fact]
    public async Task InstanceOf_with_prefixed_xs_integer_compiles()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <r><xsl:value-of select="42 instance of xs:integer"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">true</r>");
    }

    [Fact]
    public async Task Cast_with_prefixed_xs_int_validates_range()
    {
        // Verifies LocalTypeName carries the local part for derived-integer range validation
        // even when the source was prefixed. xs:int has range [-2^31, 2^31-1]; 99999999999 overflows.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <r><xsl:value-of select="99999999999 castable as xs:int"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">false</r>");
    }

    [Fact]
    public async Task Castable_unprefixed_without_xpath_default_namespace_raises_XPST0051()
    {
        // Regression guard: the original validator semantics must still fire when an
        // unprefixed type name is used without xpath-default-namespace.
        var transformer = new XsltTransformer();
        var act = async () => await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <r><xsl:value-of select="'42' castable as integer"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.Which.Message.Should().Contain("XPST0051");
    }

    [Fact]
    public async Task Castable_unprefixed_with_xpath_default_namespace_compiles()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xpath-default-namespace="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <r><xsl:value-of select="'42' castable as integer"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">true</r>");
    }

    #endregion

    #region Regression: namespace-aware static variable conflict (DocBook v:debug vs debug)

    // Bug: XTSE3450 mistakenly fired when an importing module declared a static `xsl:variable`
    // whose local-name matched an imported static `xsl:param` *in a different namespace*. DocBook
    // xslTNG declares `<xsl:variable name="v:debug" static="yes" .../>` (in the docbook variables
    // namespace) while `param.xsl` declares `<xsl:param name="debug" static="yes" .../>` (no
    // namespace) — these are distinct names and must not collide.

    [Fact]
    public async Task Static_var_with_prefix_does_not_conflict_with_unprefixed_param_of_same_local_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-ns-static-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "param.xsl"), """
                <xsl:stylesheet version="3.0"
                                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:xs="http://www.w3.org/2001/XMLSchema">
                  <xsl:param name="debug" static="yes" as="xs:string" select="''"/>
                </xsl:stylesheet>
                """);
            var mainPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(mainPath, """
                <xsl:stylesheet version="3.0"
                                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:xs="http://www.w3.org/2001/XMLSchema"
                                xmlns:v="http://example.com/variables">
                  <xsl:import href="param.xsl"/>
                  <xsl:variable name="v:debug" as="xs:boolean" select="false()" static="yes"/>
                  <xsl:template match="/"><r/></xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(await File.ReadAllTextAsync(mainPath),
                baseUri: new Uri(mainPath));
            var result = await transformer.TransformAsync("<x/>");
            result.Should().Contain("<r");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion

    #region Regression: namespace axis available in XPath/XSLT (XQuery raises XQST0134; XPath does not)

    [Fact]
    public async Task Namespace_axis_compiles_in_xslt()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <r><xsl:value-of select="count(/*/namespace::*)"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<root xmlns:a='http://a' xmlns:b='http://b'/>");
        result.Should().Contain("<r");
    }

    #endregion

    #region Regression: locally-declared xmlns on xsl:when visible to its test expression

    // Bug: <xsl:when xmlns:ls="..." test="/ls:locale"> raised XPST0081 because the parser's
    // namespace-context was anchored at the enclosing xsl:choose / xsl:template ancestor, not
    // the xsl:when itself, so locally-declared prefixes weren't visible. Reported against
    // DocBook xslTNG docbook.xsl line 152.

    [Fact]
    public async Task LocalXmlns_on_xsl_when_is_visible_to_its_test_expression()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:choose>
                  <xsl:when xmlns:ls="http://example.com/ls" test="/ls:locale">
                    <hit/>
                  </xsl:when>
                  <xsl:otherwise><miss/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain("<miss");
    }

    #endregion

    #region Regression: XPST0051 includes source location

    [Fact]
    public async Task XPST0051_error_includes_line_and_column()
    {
        var transformer = new XsltTransformer();
        var act = async () => await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <r><xsl:value-of select="'42' castable as integer"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.Which.Location.Should().NotBeNull();
        ex.Which.Location!.Line.Should().BeGreaterThan(0);
    }

    #endregion
}
