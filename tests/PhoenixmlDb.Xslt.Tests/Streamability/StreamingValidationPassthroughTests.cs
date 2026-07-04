using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Streamability;

/// <summary>
/// Phase 5.1 — <c>validation="strip|preserve|lax"</c> on an LRE / <c>xsl:element</c> /
/// <c>xsl:copy</c> constructed under streaming must be a NO-OP passthrough on a
/// non-schema-aware processor: the instruction constructs its element and content
/// exactly as if <c>validation</c> were absent.
///
/// INVESTIGATION FINDING: <c>validation</c> was ALREADY a no-op passthrough (see
/// <see cref="PlainPath_Validation_IsPassthrough"/>). The corpus failures
/// (si-lre/element/document/copy-022/023/024) actually stemmed from their
/// SELECT path — <c>/*/*:description</c> — whose namespace-wildcard step
/// (<c>*:name</c>) made the streaming path matcher bail to null, so no watcher was
/// registered, the striding path ran against the closed synthetic document, and the
/// for-each produced nothing (<c>&lt;out/&gt;</c>). The flat <c>StreamPathMatcher</c>
/// keys purely on LOCAL names — it already ignores namespace for concrete-prefix
/// steps — so a <c>*:name</c> ("any namespace, this local name") step is exactly
/// what it already matches. The fix folds <c>*:name</c> to its local name instead of
/// bailing (StreamingExpressionScanner.TryBuildPathMatcher).
/// </summary>
public class StreamingValidationPassthroughTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-validation-passthrough-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, inputFileName);
        await File.WriteAllTextAsync(inputPath, inputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // A single namespaced striding child of the root, mirroring the corpus shape
    // (/*/*:description over citygml.xml, where description is in a namespace and
    // the step uses the *: wildcard) that the si-*-022/023/024 cases exercise.
    private const string DocXml = """
        <root xmlns:g="urn:g"><g:thing>hello</g:thing></root>
        """;

    private static string LreSheet(string validation) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="xsl:initial-template">
            <out>
              <xsl:source-document streamable="yes" href="b.xml">
                <xsl:for-each select="/*/*:thing">
                  <description xsl:validation="{{validation}}"><xsl:value-of select="."/></description>
                </xsl:for-each>
              </xsl:source-document>
            </out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static string ElementSheet(string validation) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="xsl:initial-template">
            <out>
              <xsl:source-document streamable="yes" href="b.xml">
                <xsl:for-each select="/*/*:thing">
                  <xsl:element name="description" validation="{{validation}}"><xsl:value-of select="."/></xsl:element>
                </xsl:for-each>
              </xsl:source-document>
            </out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static string CopySheet(string validation) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="xsl:initial-template">
            <out>
              <xsl:source-document streamable="yes" href="b.xml">
                <xsl:for-each select="/*/*:thing">
                  <xsl:copy copy-namespaces="no" validation="{{validation}}"><xsl:value-of select="."/></xsl:copy>
                </xsl:for-each>
              </xsl:source-document>
            </out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // A plain (non-wildcard, non-namespaced) striding path already round-trips a
    // validation="strip" body under streaming — proving validation was ALREADY a
    // no-op passthrough. The corpus si-*-022/023/024 failures were actually the
    // namespace-wildcard step (`*:description`) bailing the path matcher to empty;
    // this control guards that the plain path stays green.
    [Fact]
    public async Task PlainPath_Validation_IsPassthrough()
    {
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="b.xml">
                    <xsl:for-each select="/root/thing">
                      <description xsl:validation="strip"><xsl:value-of select="."/></description>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(sheet, "<root><thing>hello</thing></root>", "b.xml");
        result.Trim().Should().Be("<out><description>hello</description></out>");
    }

    // The core fix: a namespace-wildcard step (`*:thing`) over a streamed namespaced
    // node must register a watcher (match by local name) rather than bailing to empty.
    [Fact]
    public async Task NamespaceWildcardStep_Streams()
    {
        var sheet = LreSheet("strip").Replace(" xsl:validation=\"strip\"", "");
        var result = await TransformWithFile(sheet, DocXml, "b.xml");
        result.Trim().Should().Be("<out><description>hello</description></out>");
    }

    [Theory]
    [InlineData("strip")]
    [InlineData("preserve")]
    [InlineData("lax")]
    public async Task Lre_Validation_IsPassthrough(string validation)
    {
        var result = await TransformWithFile(LreSheet(validation), DocXml, "b.xml");
        result.Trim().Should().Be("<out><description>hello</description></out>");
    }

    [Theory]
    [InlineData("strip")]
    [InlineData("preserve")]
    [InlineData("lax")]
    public async Task Element_Validation_IsPassthrough(string validation)
    {
        var result = await TransformWithFile(ElementSheet(validation), DocXml, "b.xml");
        result.Trim().Should().Be("<out><description>hello</description></out>");
    }

    [Theory]
    [InlineData("strip")]
    [InlineData("preserve")]
    [InlineData("lax")]
    public async Task Copy_Validation_IsPassthrough(string validation)
    {
        // xsl:copy of the streamed g:thing context node with validation passthrough.
        var result = await TransformWithFile(CopySheet(validation), DocXml, "b.xml");
        // copy-namespaces="no" drops the inherited g: declaration; the element keeps
        // its expanded name so a prefix is re-synthesized on serialization.
        result.Should().Contain("hello");
        result.Should().NotBe("<out/>");
    }
}
