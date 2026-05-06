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

    #region xsl:import-schema → ISchemaProvider

    // The XsltTransformer ships with an XsdSchemaProvider by default. xsl:import-schema
    // declarations are captured during parsing and forwarded to the provider's ImportSchema
    // method when the stylesheet loads, so subsequent schema-element/attribute references
    // and validation="strict" attributes can resolve.

    [Fact]
    public async Task ImportSchema_with_no_location_hints_throws_with_provider_error()
    {
        var transformer = new XsltTransformer();
        var act = async () => await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:po="http://example.com/po">
              <xsl:import-schema namespace="http://example.com/po"/>
              <xsl:template match="/"><r/></xsl:template>
            </xsl:stylesheet>
            """);
        var ex = await act.Should().ThrowAsync<XsltException>();
        // Default XsdSchemaProvider raises XQST0059 when it can't locate the schema.
        ex.Which.Message.Should().Contain("XQST0059");
    }

    [Fact]
    public async Task ImportSchema_loads_xsd_and_makes_namespace_available()
    {
        // Write a schema to disk and reference it via schema-location.
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-import-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var xsdPath = Path.Combine(dir, "items.xsd");
            await File.WriteAllTextAsync(xsdPath, """
                <?xml version="1.0"?>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://example.com/items"
                           elementFormDefault="qualified">
                  <xs:element name="item" type="xs:string"/>
                </xs:schema>
                """);

            var stylesheetPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(stylesheetPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:import-schema namespace="http://example.com/items"
                                     schema-location="items.xsd"/>
                  <xsl:template match="/"><ok/></xsl:template>
                </xsl:stylesheet>
                """);

            // LoadStylesheetAsync should resolve the schema-location relative to baseUri and
            // hand it to the default XsdSchemaProvider without throwing.
            var transformer = new XsltTransformer();
            var act = async () => await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(stylesheetPath),
                baseUri: new Uri(stylesheetPath));
            await act.Should().NotThrowAsync();

            // The provider was populated. (Verifying HasElementDeclaration round-trips through
            // the provider's NamespaceId mapping is slice-3 work; here we only assert that
            // schema loading succeeded.)
            transformer.SchemaProvider.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ImportSchema_replaceable_with_custom_provider()
    {
        // Verifies the extension-point story: callers can swap in a custom ISchemaProvider.
        var custom = new RecordingSchemaProvider();
        var transformer = new XsltTransformer { SchemaProvider = custom };
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:import-schema namespace="http://example.com/x" schema-location="x.xsd"/>
              <xsl:template match="/"><r/></xsl:template>
            </xsl:stylesheet>
            """);

        custom.Imports.Should().ContainSingle()
            .Which.targetNamespace.Should().Be("http://example.com/x");
    }

    /// <summary>Test double for ISchemaProvider that records every ImportSchema call.</summary>
    private sealed class RecordingSchemaProvider : PhoenixmlDb.XQuery.ISchemaProvider
    {
        public List<(string targetNamespace, IReadOnlyList<string>? hints)> Imports { get; } = new();

        public void ImportSchema(string targetNamespace, IReadOnlyList<string>? locationHints = null)
            => Imports.Add((targetNamespace, locationHints));

        public bool IsSubtypeOf(PhoenixmlDb.Xdm.XdmTypeName a, PhoenixmlDb.Xdm.XdmTypeName b) => a == b;
        public bool HasElementDeclaration(PhoenixmlDb.Xdm.XdmQName name) => false;
        public bool HasAttributeDeclaration(PhoenixmlDb.Xdm.XdmQName name) => false;
        public PhoenixmlDb.Xdm.XdmTypeName? GetElementType(PhoenixmlDb.Xdm.XdmQName name) => null;
        public PhoenixmlDb.Xdm.XdmTypeName? GetAttributeType(PhoenixmlDb.Xdm.XdmQName name) => null;
        public bool MatchesSchemaElement(PhoenixmlDb.Xdm.Nodes.XdmElement e, PhoenixmlDb.Xdm.XdmQName n) => false;
        public bool MatchesSchemaAttribute(PhoenixmlDb.Xdm.Nodes.XdmAttribute a, PhoenixmlDb.Xdm.XdmQName n) => false;
        public PhoenixmlDb.Xdm.Nodes.XdmNode Validate(PhoenixmlDb.Xdm.Nodes.XdmNode node,
            PhoenixmlDb.XQuery.ValidationMode mode, string? typeNamespaceUri = null, string? typeLocalName = null)
            => node;
    }

    #endregion

    #region xsl:result-document validation= → ISchemaProvider.ValidateXml

    [Fact]
    public async Task ResultDocument_validation_strict_against_loaded_schema_passes_when_valid()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-validate-rd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var xsdPath = Path.Combine(dir, "ok.xsd");
            await File.WriteAllTextAsync(xsdPath, """
                <?xml version="1.0"?>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://example.com/ok"
                           elementFormDefault="qualified">
                  <xs:element name="ok" type="xs:string"/>
                </xs:schema>
                """);

            var xslPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(xslPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:k="http://example.com/ok">
                  <xsl:import-schema namespace="http://example.com/ok" schema-location="ok.xsd"/>
                  <xsl:template match="/">
                    <xsl:result-document href="out.xml" validation="strict">
                      <k:ok>hello</k:ok>
                    </xsl:result-document>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(xslPath),
                baseUri: new Uri(xslPath));

            await transformer.TransformAsync("<x/>");
            transformer.SecondaryResultDocuments.Should().ContainKey("out.xml");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ResultDocument_validation_strict_throws_when_content_violates_schema()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-validate-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var xsdPath = Path.Combine(dir, "must-int.xsd");
            await File.WriteAllTextAsync(xsdPath, """
                <?xml version="1.0"?>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://example.com/i"
                           elementFormDefault="qualified">
                  <xs:element name="n" type="xs:integer"/>
                </xs:schema>
                """);

            var xslPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(xslPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:i="http://example.com/i">
                  <xsl:import-schema namespace="http://example.com/i" schema-location="must-int.xsd"/>
                  <xsl:template match="/">
                    <xsl:result-document href="out.xml" validation="strict">
                      <i:n>not-a-number</i:n>
                    </xsl:result-document>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(xslPath),
                baseUri: new Uri(xslPath));

            var act = async () => await transformer.TransformAsync("<x/>");
            var ex = await act.Should().ThrowAsync<XsltException>();
            ex.Which.Message.Should().Contain("XQDY0027");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ResultDocument_validation_lax_passes_when_namespace_unknown()
    {
        // Lax mode: skip validation when no declaration is found (XSLT 3.0 §27.2).
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:result-document href="out.xml" validation="lax">
                  <whatever>fine in lax mode</whatever>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """, baseUri: new Uri("file:///tmp/test/"));
        await transformer.TransformAsync("<x/>");
        transformer.SecondaryResultDocuments.Should().ContainSingle()
            .Which.Value.Should().Contain("fine in lax mode");
    }

    #endregion

    #region as="schema-element(name)" sequence types

    [Fact]
    public async Task Variable_as_schema_element_passes_when_value_matches_declared_element()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-as-schema-elem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var xsdPath = Path.Combine(dir, "items.xsd");
            await File.WriteAllTextAsync(xsdPath, """
                <?xml version="1.0"?>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://example.com/items"
                           elementFormDefault="qualified">
                  <xs:element name="item" type="xs:string"/>
                </xs:schema>
                """);

            var xslPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(xslPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:i="http://example.com/items">
                  <xsl:import-schema namespace="http://example.com/items" schema-location="items.xsd"/>
                  <xsl:template match="/">
                    <xsl:variable name="x" as="schema-element(i:item)">
                      <i:item>hello</i:item>
                    </xsl:variable>
                    <result><xsl:copy-of select="$x"/></result>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(xslPath),
                baseUri: new Uri(xslPath));

            var result = await transformer.TransformAsync("<x/>");
            result.Should().Contain("hello");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion

    #region validation= on xsl:document, xsl:element, xsl:copy, xsl:copy-of, xsl:attribute

    [Fact]
    public async Task Element_validation_strict_throws_when_content_violates_schema()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-elem-validate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var xsdPath = Path.Combine(dir, "n.xsd");
            await File.WriteAllTextAsync(xsdPath, """
                <?xml version="1.0"?>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://example.com/n"
                           elementFormDefault="qualified">
                  <xs:element name="n" type="xs:integer"/>
                </xs:schema>
                """);

            var xslPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(xslPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:n="http://example.com/n">
                  <xsl:import-schema namespace="http://example.com/n" schema-location="n.xsd"/>
                  <xsl:template match="/">
                    <root>
                      <xsl:element name="n:n" namespace="http://example.com/n" validation="strict">
                        <xsl:text>not-a-number</xsl:text>
                      </xsl:element>
                    </root>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(xslPath),
                baseUri: new Uri(xslPath));

            var act = async () => await transformer.TransformAsync("<x/>");
            var ex = await act.Should().ThrowAsync<XsltException>();
            ex.Which.Message.Should().Contain("XQDY0027");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Document_validation_lax_skips_when_namespace_unknown()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:document validation="lax">
                  <whatever>fine in lax</whatever>
                </xsl:document>
              </xsl:template>
            </xsl:stylesheet>
            """);
        // Lax mode against an empty schema set: validation runs but emits no errors.
        // Should not throw.
        var act = async () => await transformer.TransformAsync("<x/>");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Attribute_validation_strict_throws_when_no_global_declaration()
    {
        // xsl:import-schema is required to put the parser in schema-aware mode (otherwise
        // XTSE1660 rejects validation="strict" before runtime). The empty import is enough
        // to flip the flag; the actual attribute lookup happens at runtime against the
        // registered XsdSchemaProvider, which has no schemas loaded → strict validation
        // can't find the global decl → XQDY0027.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:import-schema/>
              <xsl:template match="/">
                <root>
                  <xsl:attribute name="undeclared" validation="strict">value</xsl:attribute>
                </root>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var act = async () => await transformer.TransformAsync("<x/>");
        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.Which.Message.Should().Contain("XQDY0027");
        ex.Which.Message.Should().Contain("undeclared");
    }

    [Fact]
    public async Task CopyOf_validation_strict_throws_when_copy_violates_schema()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xslt-copyof-validate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var xsdPath = Path.Combine(dir, "n.xsd");
            await File.WriteAllTextAsync(xsdPath, """
                <?xml version="1.0"?>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://example.com/n"
                           elementFormDefault="qualified">
                  <xs:element name="n" type="xs:integer"/>
                </xs:schema>
                """);

            var xslPath = Path.Combine(dir, "main.xsl");
            await File.WriteAllTextAsync(xslPath, """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:n="http://example.com/n">
                  <xsl:import-schema namespace="http://example.com/n" schema-location="n.xsd"/>
                  <xsl:template match="/">
                    <root>
                      <xsl:variable name="bad">
                        <n:n>not-a-number</n:n>
                      </xsl:variable>
                      <xsl:copy-of select="$bad" validation="strict"/>
                    </root>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(xslPath),
                baseUri: new Uri(xslPath));

            var act = async () => await transformer.TransformAsync("<x/>");
            var ex = await act.Should().ThrowAsync<XsltException>();
            ex.Which.Message.Should().Contain("XQDY0027");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion

    #region Regression: xsl:variable as="map(*)" preserves the map item

    // Bug: <xsl:variable name="m" as="map(*)"><xsl:map>…</xsl:map></xsl:variable> ended up
    // as a JSON-serialized string because the global-init non-sequence-type branch captured
    // _output text (which the xsl:map top-level-fallback path had written via WriteText). The
    // resulting string failed downstream `map:contains($m, …)` calls with XPTY0004 "must be
    // a single map". Fix: route map/array/function/record item types through the sequence
    // accumulator so xsl:map's Dictionary lands as a live map. Reported by Martin Honnen
    // running DocBook xslTNG 2.8.0 docbook.xsl against samples/article.xml.

    [Fact]
    public async Task GlobalVariable_as_map_preserves_map_item_for_map_contains()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:map="http://www.w3.org/2005/xpath-functions/map">
              <xsl:variable name="m" as="map(*)">
                <xsl:map>
                  <xsl:map-entry key="'a'" select="1"/>
                  <xsl:map-entry key="'b'" select="2"/>
                </xsl:map>
              </xsl:variable>
              <xsl:template match="/">
                <r>
                  <a><xsl:value-of select="map:contains($m, 'a')"/></a>
                  <z><xsl:value-of select="map:contains($m, 'z')"/></z>
                </r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">true</a>");
        result.Should().Contain(">false</z>");
    }

    // Note: a similar test for `as="array(*)"` is not included yet — `xsl:sequence` inside an
    // array-typed xsl:variable currently flattens the array members into the sequence
    // accumulator (`array:size` returns 0). That's a separate bug in xsl:sequence's interaction
    // with the array container and isn't on the same call path as the map regression. Tracked
    // separately; the fix here is scoped to map/array/function/record items NOT being JSON-
    // serialized at the variable boundary.

    #endregion

    #region Regression: external static params accept bare boolean / numeric values

    // Schxslt2 declares `<xsl:param name="schxslt:debug" static="yes" select="false()"/>` and
    // gates `<xsl:output indent="yes" use-when="$schxslt:debug"/>` on it. Martin asked how to
    // override that from the CLI's `-p name=value` syntax. The CLI now passes -p values to
    // both the static-param compile-time path and the runtime-param path; the static-param
    // value parser additionally recognizes bare `true` / `false` / integers / doubles, on top
    // of XPath-shaped literals (true(), false(), '...', "...", ()).

    [Fact]
    public async Task StaticParam_accepts_bare_true_via_LoadStylesheetAsync_staticParams()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="debug" static="yes" select="false()"/>
              <xsl:template match="/">
                <r><xsl:value-of select="if ($debug) then 'on' else 'off'"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """, staticParams: new Dictionary<string, string> { ["debug"] = "true" });

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">on</r>");
    }

    [Fact]
    public async Task StaticParam_default_select_is_used_when_no_external_value()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="debug" static="yes" select="false()"/>
              <xsl:template match="/">
                <r><xsl:value-of select="if ($debug) then 'on' else 'off'"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">off</r>");
    }

    #endregion

    #region Regression: mode="#current" preserved across xsl:for-each

    // Bug: xsl:for-each (and xsl:for-each-group) cleared the engine's _currentMode field
    // alongside _currentTemplate. Per XSLT 3.0 §13.4.1 the current template *rule* is absent
    // inside xsl:for-each but the current *mode* is unchanged. The bug meant a nested
    // <xsl:apply-templates mode="#current"> resolved to the unnamed mode and silently failed
    // to match templates declared `mode="m1"`. Reported by Martin Honnen against Schxslt2 —
    // the transpile pass relies on map:keys / for-each / apply-templates mode="#current"
    // to dispatch sch:rule templates, which never fired.

    [Fact]
    public async Task ApplyTemplates_mode_current_preserved_across_xsl_for_each()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            default-mode="m1">
              <xsl:mode name="m1" on-no-match="shallow-skip"/>
              <xsl:template match="root" mode="m1">
                <r>
                  <xsl:for-each select="group">
                    <xsl:apply-templates select="item" mode="#current"/>
                  </xsl:for-each>
                </r>
              </xsl:template>
              <xsl:template match="item" mode="m1">
                <hit><xsl:value-of select="@id"/></hit>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var result = await transformer.TransformAsync(
            "<root><group><item id='1'/><item id='2'/></group></root>");
        // Without the fix, the inner mode would resolve to the unnamed mode and the m1
        // template wouldn't match — output would be `<r/>`. With the fix, both items match.
        result.Should().Contain("<hit>1</hit>");
        result.Should().Contain("<hit>2</hit>");
    }

    [Fact]
    public async Task ApplyTemplates_mode_current_preserved_across_xsl_for_each_group()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            default-mode="m1">
              <xsl:mode name="m1" on-no-match="shallow-skip"/>
              <xsl:template match="root" mode="m1">
                <r>
                  <xsl:for-each-group select="item" group-by="@cat">
                    <xsl:apply-templates select="current-group()" mode="#current"/>
                  </xsl:for-each-group>
                </r>
              </xsl:template>
              <xsl:template match="item" mode="m1">
                <hit cat="{@cat}" id="{@id}"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var result = await transformer.TransformAsync(
            "<root><item id='1' cat='a'/><item id='2' cat='b'/><item id='3' cat='a'/></root>");
        result.Should().Contain("id=\"1\"");
        result.Should().Contain("id=\"2\"");
        result.Should().Contain("id=\"3\"");
    }

    #endregion

    #region Regression: fn:serialize(method=adaptive) on a map of nodes

    // Bug: `$patterns => serialize(map { 'method': 'adaptive' })` from inside an XSLT stylesheet
    // produced JSON output (`{"k":"text"}`) instead of adaptive (`map{"k":<elem/>}`). Two
    // causes: (1) Serialize2Function only routed through XQueryResultSerializer when
    // NodeProvider was XdmDocumentStore — XSLT uses its own InMemoryNodeStore; (2) the
    // fallback SerializeItem path always used SerializeMapAsJson regardless of the requested
    // method. Reported by Martin Honnen against the Schxslt2 transpile workflow.

    [Fact]
    public async Task fn_serialize_adaptive_method_emits_map_with_node_values()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:map="http://www.w3.org/2005/xpath-functions/map">
              <xsl:template match="/">
                <xsl:variable name="m" as="map(xs:string, element()+)">
                  <xsl:map>
                    <xsl:map-entry key="'all'" select="//item"/>
                  </xsl:map>
                </xsl:variable>
                <r><xsl:value-of select="$m => serialize(map { 'method': 'adaptive' })"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync(
            "<root><item id='1'>a</item><item id='2'>b</item></root>");

        // Adaptive form: `map{` opener (not `{`), node values serialized as XML markup
        // (escaped here because it's inside an XSLT `xsl:value-of`).
        result.Should().Contain("map{");
        result.Should().Contain("&lt;item");
        result.Should().Contain("id=\"1\"");
        result.Should().Contain("id=\"2\"");
    }

    // Regression: fn:doc-available accepts xs:untypedAtomic per XPath 3.1 function
    // conversion rules. Previously raised XPTY0004 when $uri came from
    // `xs:untypedAtomic('foo.xml')`. Reported by Martin Honnen.
    [Fact]
    public async Task doc_available_accepts_xs_untypedAtomic()
    {
        // Spec-required: xs:untypedAtomic must cast to xs:string per the function
        // conversion rules. The previous behavior raised XPTY0004 instead. Asserting
        // here that the call type-checks; we don't care whether the file exists, only
        // that doc-available returns a boolean rather than throwing.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:param name="uri" select="xs:untypedAtomic('http://example.com/missing.xml')"/>
              <xsl:template match="/">
                <r><xsl:value-of select="doc-available($uri) instance of xs:boolean"/></r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain(">true</r>");
    }

    // Regression: a matched template (apply-templates dispatch path, not call-template)
    // typed `as="element()*"` whose body emits an LRE *before* an xsl:sequence (or any
    // accumulator-routed item) must reassemble result items in source order. Previously
    // the apply-templates code path collected accumulator items first and serialized
    // output last, so Schxslt2's transpiled validation stylesheet emitted
    // svrl:active-pattern / svrl:fired-rule *after* svrl:failed-assert /
    // svrl:successful-report inside svrl:schematron-output. Reported by Martin Honnen.
    [Fact]
    public async Task apply_templates_as_body_preserves_source_order()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/x" as="element()*">
                <first/>
                <xsl:variable name="v" as="element(second)"><second/></xsl:variable>
                <xsl:sequence select="$v"/>
                <third/>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        var firstIdx = result.IndexOf("<first/>", StringComparison.Ordinal);
        var secondIdx = result.IndexOf("<second/>", StringComparison.Ordinal);
        var thirdIdx = result.IndexOf("<third/>", StringComparison.Ordinal);
        firstIdx.Should().BeGreaterThan(-1);
        secondIdx.Should().BeGreaterThan(firstIdx);
        thirdIdx.Should().BeGreaterThan(secondIdx);
    }

    // Regression: a template typed `as="node()*"` whose body produces an `xsl:attribute`
    // before an LRE must reassemble its result in source order. Previously the engine
    // collected accumulator items (xsl:attribute → XdmAttribute) after serialized output
    // (LREs in _output) regardless of source order, which made the parent constructor see
    // attribute-after-children → spurious XTDE0410. Reported by Martin Honnen against
    // Schxslt2 1.10.3 transpile.xsl which composes svrl:failed-assert from an `as="node()*"`
    // helper that returns attrs followed by an svrl:text element.
    [Fact]
    public async Task as_node_template_returns_items_in_source_order()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="emit-content" as="node()*">
                <xsl:attribute name="ruleId" select="'r1'"/>
                <text-elem>some text</text-elem>
              </xsl:template>
              <xsl:template match="/">
                <root>
                  <xsl:call-template name="emit-content"/>
                </root>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain("ruleId=\"r1\"");
        result.Should().Contain("<text-elem>some text</text-elem>");
    }

    // Regression: xsl:where-populated must filter zero-length-valued xsl:attribute even when
    // those attributes route via _sequenceAccumulator (i.e. inside an `as=` body). Without
    // this, Schxslt2's `failed-assertion-attributes` template emitted spurious empty
    // attributes like `ruleId=""` whenever the source rule had no @id.
    [Fact]
    public async Task where_populated_filters_empty_attribute_in_as_body()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="emit-content" as="node()*">
                <xsl:attribute name="keep" select="'yes'"/>
                <xsl:where-populated>
                  <xsl:attribute name="drop" select="''"/>
                </xsl:where-populated>
              </xsl:template>
              <xsl:template match="/">
                <root>
                  <xsl:call-template name="emit-content"/>
                </root>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await transformer.TransformAsync("<x/>");
        result.Should().Contain("keep=\"yes\"");
        result.Should().NotContain("drop=");
    }

    // Regression: XTDE0410 (attribute after children) must carry the source location of
    // the offending xsl:attribute / xsl:copy. Reported by Martin Honnen — Schxslt2 produces
    // this error and the bare message gave no clue which template/instruction was at fault.
    [Fact]
    public async Task XTDE0410_attribute_after_children_carries_location()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <r>
                  <xsl:text>oops</xsl:text>
                  <xsl:attribute name="late">value</xsl:attribute>
                </r>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var act = async () => await transformer.TransformAsync("<x/>");
        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.Which.Message.Should().Contain("XTDE0410");
        ex.Which.Location.Should().NotBeNull();
        ex.Which.Location!.Line.Should().BeGreaterThan(0);
    }

    // Regression: XPST0008 in static use-when must show full QName (with prefix) and the
    // location of the offending element. Reported by Martin Honnen — diagnosing a use-when
    // failure in a multi-module stylesheet (DocBook xslTNG) was unworkable when the message
    // showed only `$debug` for an actual `$v:debug` reference and gave no line number.
    [Fact]
    public async Task XPST0008_use_when_includes_prefix_and_location()
    {
        var transformer = new XsltTransformer();
        var act = async () => await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:v="http://example.com/v">
              <xsl:variable name="x" use-when="$v:debug" select="1"/>
              <xsl:template match="/"><r/></xsl:template>
            </xsl:stylesheet>
            """);

        var ex = await act.Should().ThrowAsync<XsltException>();
        ex.Which.Message.Should().Contain("XPST0008");
        ex.Which.Message.Should().Contain("$v:debug");
        ex.Which.Location.Should().NotBeNull();
        ex.Which.Location!.Line.Should().BeGreaterThan(0);
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

    // Regression: stylesheets loaded over HTTP(S) must resolve their xsl:import / xsl:include
    // hrefs against the entry stylesheet's HTTP base URI. Previously the parser only handled
    // file:// imports and raised XTSE0165 for HTTP. Reported by Martin Honnen against the
    // schxslt2 transpile.xsl hosted on github.io. We spin up a local HttpListener so the
    // test exercises the HTTP path without depending on external network reachability.
    #region Regression: HTTP(S) stylesheet imports

    [Fact]
#pragma warning disable CA2000  // listener disposed in finally; analyzer can't see through Stop()
    public async Task stylesheet_loaded_over_http_resolves_imports_over_http()
    {
        // HttpListener requires an explicit prefix; pick a free port.
        using var listener = new System.Net.HttpListener();
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
#pragma warning restore CA2000

        const string mainXsl = """
            <xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:lib="http://example.com/lib">
              <xsl:import href="lib.xsl"/>
              <xsl:template match="/"><out><xsl:call-template name="lib:greet"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        const string libXsl = """
            <xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:lib="http://example.com/lib">
              <xsl:template name="lib:greet">hello-from-lib</xsl:template>
            </xsl:stylesheet>
            """;

        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                var body = path.EndsWith("main.xsl", StringComparison.Ordinal) ? mainXsl
                         : path.EndsWith("lib.xsl", StringComparison.Ordinal) ? libXsl
                         : "";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/xml";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.OutputStream.Close();
            }
        });

        try
        {
            using var http = new System.Net.Http.HttpClient();
            var mainUri = new Uri(prefix + "main.xsl");
            var xml = await http.GetStringAsync(mainUri);

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(xml, mainUri);

            var result = await transformer.TransformAsync("<x/>");
            result.Should().Contain(">hello-from-lib</out>");
        }
        finally
        {
            listener.Stop();
            try { await serverTask; } catch { /* listener stopped */ }
        }
    }

    private static int GetFreeTcpPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    #endregion
}
