using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression: in streamable mode, a global <c>xsl:template match="/"</c> (or
/// <c>document-node()</c>) must fire. The streaming processor's event loop starts
/// at the first ELEMENT event, so without explicit document-node dispatch the
/// root template was skipped and the built-in document rule (recurse into
/// children → text-only copy) ran instead — emitting bare text rather than the
/// constructed/copied elements the template produces.
///
/// Reported by Martin Honnen.
/// </summary>
public class StreamingRootTemplateTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Streaming_root_template_for_each_constructs_element_wrappers()
    {
        // The match="/" body iterates records/record and constructs a
        // <record-name>/<value> pair per record. Before the fix the output was
        // the bare text content ("record1value 1...") because the root template
        // never fired.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:output method="xml" indent="no"/>
                <xsl:template match="/">
                    <xsl:for-each select="records/record">
                        <record-name><xsl:value-of select="name"/></record-name>
                        <value><xsl:value-of select="value"/></value>
                    </xsl:for-each>
                </xsl:template>
            </xsl:stylesheet>
            """,
            "<records>"
            + "<record><name>record1</name><value>value 1</value></record>"
            + "<record><name>record2</name><value>value 2</value></record>"
            + "<record><name>record3</name><value>value 3</value></record>"
            + "</records>");

        r.Should().Contain("<record-name>record1</record-name>", $"actual={r}");
        r.Should().Contain("<value>value 1</value>", $"actual={r}");
        r.Should().Contain("<record-name>record2</record-name>", $"actual={r}");
        r.Should().Contain("<value>value 2</value>", $"actual={r}");
        r.Should().Contain("<record-name>record3</record-name>", $"actual={r}");
        r.Should().Contain("<value>value 3</value>", $"actual={r}");
        // The built-in text-only copy must NOT have run.
        r.Should().NotContain("record1value 1", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_root_template_literal_only_body_emits_constructed_element()
    {
        // Minimal confirmation: a streamable-mode stylesheet whose only template is
        // match="/" producing a single literal result element must emit that
        // element, not the input's text content.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:output method="xml" indent="no"/>
                <xsl:template match="/">
                    <ROOTRAN/>
                </xsl:template>
            </xsl:stylesheet>
            """,
            "<records><record><name>r1</name><value>v1</value></record></records>");

        r.Should().Contain("<ROOTRAN/>", $"actual={r}");
        r.Should().NotContain("r1", $"actual={r}");
        r.Should().NotContain("v1", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_document_node_pattern_fires()
    {
        // The document-node() pattern is the explicit spelling of "/"; it must
        // fire the same way.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:output method="xml" indent="no"/>
                <xsl:template match="document-node()">
                    <hit/>
                </xsl:template>
            </xsl:stylesheet>
            """,
            "<root><a>CONTENT</a></root>");

        r.Should().Contain("<hit/>", $"actual={r}");
        // The built-in text-only copy must NOT have run.
        r.Should().NotContain("CONTENT", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_for_each_select_with_trailing_copy_of_step_emits_body()
    {
        // Regression (Martin Honnen): a streamable xsl:for-each whose select ends
        // in a trailing copy-of() step — records/record/copy-of() — parses as a
        // SimpleMap(path, copy-of()). The streaming scanner previously failed to
        // recognize this shape, registered no subscription, and produced empty
        // output (only the XML declaration). Iterating the per-record snapshot is
        // equivalent to iterating the record element directly, so the body must
        // run once per record with that record as context.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:output method="xml" indent="no"/>
                <xsl:template match="/">
                    <xsl:for-each select="records/record/copy-of()">
                        <record-name><xsl:value-of select="name"/></record-name>
                        <xsl:sequence select="value"/>
                    </xsl:for-each>
                </xsl:template>
            </xsl:stylesheet>
            """,
            "<records>"
            + "<record><name>record1</name><value>value 1</value></record>"
            + "<record><name>record2</name><value>value 2</value></record>"
            + "<record><name>record3</name><value>value 3</value></record>"
            + "</records>");

        r.Should().Contain("<record-name>record1</record-name>", $"actual={r}");
        r.Should().Contain("<value>value 1</value>", $"actual={r}");
        r.Should().Contain("<record-name>record2</record-name>", $"actual={r}");
        r.Should().Contain("<value>value 2</value>", $"actual={r}");
        r.Should().Contain("<record-name>record3</record-name>", $"actual={r}");
        r.Should().Contain("<value>value 3</value>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_for_each_select_with_trailing_snapshot_step_emits_body()
    {
        // Same fix exercised through the snapshot() spelling of the trailing step.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:output method="xml" indent="no"/>
                <xsl:template match="/">
                    <xsl:for-each select="records/record/snapshot()">
                        <record-name><xsl:value-of select="name"/></record-name>
                    </xsl:for-each>
                </xsl:template>
            </xsl:stylesheet>
            """,
            "<records>"
            + "<record><name>record1</name></record>"
            + "<record><name>record2</name></record>"
            + "</records>");

        r.Should().Contain("<record-name>record1</record-name>", $"actual={r}");
        r.Should().Contain("<record-name>record2</record-name>", $"actual={r}");
    }
}
