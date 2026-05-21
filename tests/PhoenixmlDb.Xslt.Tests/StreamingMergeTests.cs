using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:merge: K-way merge over pre-sorted inputs. Tests start with non-streaming
/// to lock the baseline; streaming-mode follow-ups verify that merge operates
/// over xsl:source-document streams without buffering full inputs.
/// </summary>
public class StreamingMergeTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Merge_single_source_produces_one_group_per_item()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/root">
                    <out>
                        <xsl:merge>
                            <xsl:merge-source select="a/item">
                                <xsl:merge-key select="@n" data-type="number"/>
                            </xsl:merge-source>
                            <xsl:merge-action>
                                <g k="{current-merge-key()}"/>
                            </xsl:merge-action>
                        </xsl:merge>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><a><item n=\"1\"/><item n=\"2\"/><item n=\"3\"/></a></root>");
        r.Should().Contain("k=\"1\"", $"actual={r}");
        r.Should().Contain("k=\"2\"", $"actual={r}");
        r.Should().Contain("k=\"3\"", $"actual={r}");
    }

    [Fact]
    public async Task Merge_two_sorted_sources_in_order_produces_merged_sequence()
    {
        // Two pre-sorted sequences in the input doc; merge by @n ascending.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/root">
                    <out>
                        <xsl:merge>
                            <xsl:merge-source select="a/item">
                                <xsl:merge-key select="@n" data-type="number"/>
                            </xsl:merge-source>
                            <xsl:merge-source select="b/item">
                                <xsl:merge-key select="@n" data-type="number"/>
                            </xsl:merge-source>
                            <xsl:merge-action>
                                <merged><xsl:value-of select="current-merge-group()/@n" separator=","/></merged>
                            </xsl:merge-action>
                        </xsl:merge>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><a><item n=\"1\"/><item n=\"3\"/><item n=\"5\"/></a><b><item n=\"2\"/><item n=\"4\"/><item n=\"6\"/></b></root>");
        // Expect merged n values in order 1,2,3,4,5,6
        var idx1 = r.IndexOf(">1<", System.StringComparison.Ordinal);
        var idx2 = r.IndexOf(">2<", System.StringComparison.Ordinal);
        var idx6 = r.IndexOf(">6<", System.StringComparison.Ordinal);
        idx1.Should().BeGreaterThan(0, $"actual={r}");
        idx2.Should().BeGreaterThan(idx1, $"merge order broken. actual={r}");
        idx6.Should().BeGreaterThan(idx2, $"merge order broken. actual={r}");
    }

    [Fact]
    public async Task Merge_with_streamable_attribute_executes_correctly()
    {
        // streamable="yes" is parsed and stored on XsltMergeSource AST. Runtime
        // still executes non-streaming (XmlReader pull-on-demand merge is a future
        // wave); this probe locks in the parse + correct execution baseline.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/root">
                    <out>
                        <xsl:merge>
                            <xsl:merge-source select="a/item" streamable="yes">
                                <xsl:merge-key select="@n" data-type="number"/>
                            </xsl:merge-source>
                            <xsl:merge-action>
                                <g k="{current-merge-key()}"/>
                            </xsl:merge-action>
                        </xsl:merge>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><a><item n=\"1\"/><item n=\"2\"/></a></root>");
        r.Should().Contain("k=\"1\"", $"actual={r}");
        r.Should().Contain("k=\"2\"", $"actual={r}");
    }

    [Fact]
    public async Task Merge_streaming_two_for_each_source_files_kway_merges_pre_sorted_inputs()
    {
        // Drive xsl:merge through the streaming runtime: each merge-source uses
        // for-each-source + streamable="yes" + select="item". The XmlReader path
        // walks each file's child::item without materialising the document.
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xsl-merge-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var aPath = System.IO.Path.Combine(tempDir, "a.xml");
            var bPath = System.IO.Path.Combine(tempDir, "b.xml");
            await System.IO.File.WriteAllTextAsync(aPath, "<a><item n=\"1\"/><item n=\"3\"/><item n=\"5\"/></a>");
            await System.IO.File.WriteAllTextAsync(bPath, "<b><item n=\"2\"/><item n=\"4\"/><item n=\"6\"/></b>");

            // The select expression on each merge-source is the *child* of the
            // for-each-source document — i.e. <a> / <b>. The classifier requires
            // a single child-axis name test, which matches "item".
            var aUri = new System.Uri(aPath).AbsoluteUri;
            var bUri = new System.Uri(bPath).AbsoluteUri;
            var stylesheet = $$"""
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                    <xsl:template match="/root">
                        <out>
                            <xsl:merge>
                                <xsl:merge-source streamable="yes" for-each-source="'{{aUri}}'" select="item">
                                    <xsl:merge-key select="@n" data-type="number"/>
                                </xsl:merge-source>
                                <xsl:merge-source streamable="yes" for-each-source="'{{bUri}}'" select="item">
                                    <xsl:merge-key select="@n" data-type="number"/>
                                </xsl:merge-source>
                                <xsl:merge-action>
                                    <g k="{current-merge-key()}"/>
                                </xsl:merge-action>
                            </xsl:merge>
                        </out>
                    </xsl:template>
                </xsl:stylesheet>
                """;

            var result = await Run(stylesheet, "<root/>");

            // K-way merge produces interleaved sorted output.
            var k1 = result.IndexOf("k=\"1\"", System.StringComparison.Ordinal);
            var k2 = result.IndexOf("k=\"2\"", System.StringComparison.Ordinal);
            var k3 = result.IndexOf("k=\"3\"", System.StringComparison.Ordinal);
            var k6 = result.IndexOf("k=\"6\"", System.StringComparison.Ordinal);
            k1.Should().BeGreaterThan(0, $"actual={result}");
            k2.Should().BeGreaterThan(k1, $"merge order broken (k2 not after k1). actual={result}");
            k3.Should().BeGreaterThan(k2, $"merge order broken (k3 not after k2). actual={result}");
            k6.Should().BeGreaterThan(k3, $"actual={result}");
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public async Task Merge_groups_equal_keys_into_single_action()
    {
        // Both sources contribute item with n="2" — current-merge-group should yield both.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/root">
                    <out>
                        <xsl:merge>
                            <xsl:merge-source select="a/item">
                                <xsl:merge-key select="@n" data-type="number"/>
                            </xsl:merge-source>
                            <xsl:merge-source select="b/item">
                                <xsl:merge-key select="@n" data-type="number"/>
                            </xsl:merge-source>
                            <xsl:merge-action>
                                <group key="{current-merge-key()}" count="{count(current-merge-group())}" joined="{string-join(current-merge-group()/@n, '+')}"/>
                            </xsl:merge-action>
                        </xsl:merge>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><a><item n=\"1\"/><item n=\"2\"/></a><b><item n=\"2\"/><item n=\"3\"/></b></root>");
        r.Should().Contain("key=\"2\" count=\"2\"", $"actual={r}");
        r.Should().Contain("key=\"1\" count=\"1\"", $"actual={r}");
        r.Should().Contain("key=\"3\" count=\"1\"", $"actual={r}");
    }
}
