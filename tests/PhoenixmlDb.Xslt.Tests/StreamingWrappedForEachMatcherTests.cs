using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Phase 2b of #143 "uniform consuming-expression streaming": matcher generality
/// for wrapped/inline-driven consuming <c>xsl:for-each</c>. The streaming-in-place
/// mechanism (phases 1/2a) is solved; this fixture exercises the element-wildcard
/// SELECT shape (<c>/*/*</c>) that the path-matcher decomposition previously
/// rejected.
///
/// NOTE: the descendant <c>//node()[name() = $v]</c> shape (si-for-each-801/802)
/// is intentionally NOT covered here. It is UNSOUND through the for-each
/// subscription mechanism, which materializes-and-skips a matched element's whole
/// subtree: a descendant-axis wildcard also matches the matched element's
/// ancestors, so matching an ancestor consumes the descendants the predicate would
/// have selected (the root element matches <c>//node()</c>, swallowing the
/// document). It remains deferred for a non-consuming watcher-based path.
/// </summary>
public class StreamingWrappedForEachMatcherTests
{
    private static async Task<string> TransformWithFile(
        string stylesheet, string inputXml, string inputFileName,
        (string name, string value)? param = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-wrapped-foreach-matcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, inputFileName);
        await File.WriteAllTextAsync(inputPath, inputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            if (param is { } p)
                transformer.SetParameter(p.name, p.value);
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// si-choose-008 shape: wrapped streamable for-each with an element-wildcard
    /// select <c>/*/*</c>. The matcher must accept <c>*</c> steps while still
    /// honoring the step count (children-of-children only).
    /// </summary>
    [Fact]
    public async Task WrappedForEach_WildcardSteps_StreamsChildrenOfChildren()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <chapter>
              <head>Heading</head>
              <p>One</p>
              <p>Two</p>
              <bullet>point one</bullet>
            </chapter>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="bullets.xml">
                    <xsl:for-each select="/*/*">
                      <xsl:choose>
                        <xsl:when test="name() = 'p'"><para/></xsl:when>
                        <xsl:otherwise><xsl:copy-of select="."/></xsl:otherwise>
                      </xsl:choose>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "bullets.xml");
        result.Trim().Should().Be(
            "<out><head>Heading</head><para/><para/><bullet>point one</bullet></out>",
            because: "the matcher must accept /*/* and stream each grandchild-of-root in document order");
    }

    /// <summary>
    /// Guards the step-count discipline of the wildcard extension: <c>/*/*</c> must
    /// NOT match the root element (one step short) nor great-grandchildren (one step
    /// deep). Only the direct grandchildren of the document element stream.
    /// </summary>
    [Fact]
    public async Task WrappedForEach_WildcardSteps_DoesNotMatchRootOrGreatGrandchildren()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <root>
              <mid>
                <leaf id="x"/>
              </mid>
              <other id="y"/>
            </root>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="tree.xml">
                    <xsl:for-each select="/*/*">
                      <hit name="{name()}"/>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "tree.xml");
        result.Trim().Should().Be(
            "<out><hit name=\"mid\"/><hit name=\"other\"/></out>",
            because: "/*/* selects only grandchildren of root — not root, not the deeper <leaf>");
    }
}
