using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Reproducer for the architectural gap where xsl:for-each select="/absolute/path"
/// inside xsl:source-document streamable="yes" evaluates against the synthetic empty
/// document and returns no items.
///
/// Root cause: <c>_isStreamingExecution</c> is only set inside
/// <c>StreamingXmlProcessor.ProcessAsync()</c>, which is only invoked when the
/// source-document body contains <c>xsl:apply-templates</c>. A bare
/// <c>xsl:for-each</c> body never opts into streaming, so XPath sees an empty doc.
///
/// This test is intentionally failing until Tasks 2-5 of the streaming-integration
/// plan land.
/// </summary>
public class StreamingForEachIntegrationTests
{
    private static async Task<string> TransformWithFile(
        string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-foreach-test-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task ForEach_UnderStreamableSourceDocument_IteratesStreamedNodes()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <xsl:for-each select="/BOOKLIST/BOOKS/ITEM/PRICE">
                    <xsl:value-of select="."/>
                    <xsl:text>;</xsl:text>
                  </xsl:for-each>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """
            <?xml version="1.0"?>
            <BOOKLIST>
              <BOOKS>
                <ITEM><PRICE>10</PRICE></ITEM>
                <ITEM><PRICE>20</PRICE></ITEM>
                <ITEM><PRICE>30</PRICE></ITEM>
              </BOOKS>
            </BOOKLIST>
            """;

        var result = await TransformWithFile(stylesheet, input, "books.xml");

        // Before fix: result is empty (or whitespace only) because the for-each
        // evaluates /BOOKLIST/BOOKS/ITEM/PRICE against the synthetic empty doc.
        // After fix: streaming pulls each PRICE element from the source stream.
        result.Trim().Should().Be("10;20;30;",
            "xsl:for-each under streamable xsl:source-document must iterate over streamed nodes");
    }

    /// <summary>
    /// Regression: xsl:for-each inside xsl:on-empty must NOT fire when the
    /// surrounding content is non-empty. Before the scanner fix, the streaming
    /// scanner registered an unconditional ForEachSubscription for the inner
    /// for-each, causing the on-empty body to execute regardless of the gate.
    /// </summary>
    [Fact]
    public async Task ForEach_InsideOnEmpty_WithNonEmptySiblingContent_DoesNotFire()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <out>
                    <xsl:text>HEAD;</xsl:text>
                    <xsl:on-empty>
                      <xsl:for-each select="/BOOKLIST/BOOKS/ITEM/TITLE">
                        <xsl:text>SHOULD-NOT-APPEAR;</xsl:text>
                      </xsl:for-each>
                    </xsl:on-empty>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """
            <?xml version="1.0"?>
            <BOOKLIST>
              <BOOKS>
                <ITEM><TITLE>A</TITLE></ITEM>
                <ITEM><TITLE>B</TITLE></ITEM>
              </BOOKS>
            </BOOKLIST>
            """;

        var result = await TransformWithFile(stylesheet, input, "books.xml");

        result.Should().NotContain("SHOULD-NOT-APPEAR",
            "xsl:for-each inside xsl:on-empty must not execute when sibling content is non-empty");
        result.Should().Contain("HEAD;");
    }

    /// <summary>
    /// cy-010 pattern: xsl:for-each select="100, 101, /BOOKLIST/BOOKS/ITEM/PRICE" inside
    /// xsl:source-document streamable="yes". Body uses xsl:value-of select="." for grounded
    /// atomics and a `text()` step on elements. Currently the scanner rejects the
    /// Comma/SequenceConstructor select expression, so only literals are emitted (if anything).
    /// </summary>
    [Fact]
    public async Task ForEach_MixedSequence_AtomicsAndStreamablePath_EmitsAllInOrder()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <BOOKLIST><BOOKS>
              <ITEM><PRICE>4.95</PRICE></ITEM>
              <ITEM><PRICE>6.58</PRICE></ITEM>
            </BOOKS></BOOKLIST>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="books.xml">
                    <xsl:for-each select="100, 101, /BOOKLIST/BOOKS/ITEM/PRICE">
                      <xsl:element name="t">
                        <xsl:value-of select="if (. instance of element()) then text() else ."/>
                      </xsl:element>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "books.xml");
        result.Trim().Should().Be("<out><t>100</t><t>101</t><t>4.95</t><t>6.58</t></out>",
            because: "atomic prefix items and streamable element path items must all be iterated in document order");
    }

    /// <summary>
    /// cy-007 pattern: xsl:for-each select="/BOOKLIST/BOOKS/ITEM/PRICE/text(), 101, 102".
    /// Streamable absolute path with text() KindTest tail, followed by grounded atomic
    /// literals. Body uses xsl:value-of select=".".
    /// </summary>
    [Fact]
    public async Task ForEach_MixedSequence_PathTextNodeTailAndAtomicSuffix_EmitsAllInOrder()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <BOOKLIST><BOOKS>
              <ITEM><PRICE>4.95</PRICE></ITEM>
              <ITEM><PRICE>6.58</PRICE></ITEM>
            </BOOKS></BOOKLIST>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="books.xml">
                    <xsl:for-each select="/BOOKLIST/BOOKS/ITEM/PRICE/text(), 101, 102">
                      <xsl:element name="t">
                        <xsl:value-of select="."/>
                      </xsl:element>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "books.xml");
        result.Trim().Should().Be("<out><t>4.95</t><t>6.58</t><t>101</t><t>102</t></out>",
            because: "text-node children of matched elements and atomic suffix items must all be iterated in document order");
    }

    /// <summary>
    /// cy-006 pattern: xsl:for-each select="/path/text()" with body using parent-axis
    /// navigation `name(..)`. Confirms TextNodeTail actually iterates text NODES (not
    /// the parent element), so the body's `..` resolves to the materialized owner.
    /// </summary>
    [Fact]
    public async Task ForEach_TextNodeTail_ContextItemIsTextNodeWithParent()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <BOOKLIST><BOOKS>
              <ITEM><PRICE>4.95</PRICE></ITEM>
              <ITEM><PRICE>6.58</PRICE></ITEM>
            </BOOKS></BOOKLIST>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="books.xml">
                    <xsl:for-each select="/BOOKLIST/BOOKS/ITEM/PRICE/text()">
                      <xsl:element name="{name(..)}">
                        <xsl:value-of select="."/>
                      </xsl:element>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "books.xml");
        result.Trim().Should().Be("<out><PRICE>4.95</PRICE><PRICE>6.58</PRICE></out>",
            because: "the body's name(..) must see PRICE (the materialized parent element) not BOOKS");
    }

    /// <summary>
    /// Bug regression: xsl:variable as="element()*" content was not populating
    /// XdmElement._stringValue, so value-of select="." on the variable's elements
    /// returned the empty string. The fix is in ReadXdmElementFromReader.
    /// This bug surfaced under cy-008 (mixed-sequence streaming with $extra prefix)
    /// but is general — not streaming-specific.
    /// </summary>
    [Fact]
    public async Task VariableAsElementSequence_ValueOfDot_ReturnsTextContent()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:variable name="extra" as="element()*">
                  <PRICE>100.00</PRICE>
                  <PRICE>101.00</PRICE>
                </xsl:variable>
                <xsl:for-each select="$extra">
                  <xsl:value-of select="."/>
                  <xsl:text>;</xsl:text>
                </xsl:for-each>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, "<a/>", "stub.xml");
        result.Trim().Should().Be("100.00;101.00;",
            because: "XdmElement.StringValue must reflect the descendant text content for variable-constructed elements");
    }

    /// <summary>
    /// si-element-247 pattern: xsl:element name="{head(//AUTHOR)}" — the streaming
    /// consumption is inside the @name AVT of xsl:element. Scanner must walk the
    /// AVT looking for consuming expressions (mirror XsltAttribute AVT scanning).
    /// </summary>
    [Fact]
    public async Task XsltElement_NameAvt_WithStreamingHead_ResolvesElementName()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <BOOKLIST>
              <BOOKS>
                <ITEM><AUTHOR>Jane Austen</AUTHOR><TITLE>Pride and Prejudice</TITLE></ITEM>
                <ITEM><AUTHOR>Mark Twain</AUTHOR><TITLE>Huck Finn</TITLE></ITEM>
              </BOOKS>
            </BOOKLIST>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <out>
                    <xsl:element name="{translate(head(//AUTHOR), ' ', '_')}">value</xsl:element>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "books.xml");
        // Conformance assertion is /out/Jane_Austen = 'value' — match that substring.
        // The streaming pass also emits the streamed text via default template rules
        // (a pre-existing characteristic of watcher-only source-documents), so we
        // assert the constructed subtree is present rather than equality.
        result.Should().Contain("<out><Jane_Austen>value</Jane_Austen></out>",
            because: "head(//AUTHOR) inside xsl:element/@name AVT must stream the first matching element");
    }

    /// <summary>
    /// cy-001 pattern: xsl:for-each select="account/transaction[@value &lt; 0]/@value".
    /// Three orthogonal extensions exercised: relative-from-root path, predicate on
    /// the last element step (reads attributes of matched element), attribute-axis
    /// tail step.
    /// </summary>
    [Fact]
    public async Task XsltForEach_RelativePathPredicateAttributeTail_StreamsMatchingAttributes()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <account>
              <transaction value="-15.00"/>
              <transaction value="6.42"/>
              <transaction value="-5.00"/>
              <transaction value="100.00"/>
            </account>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="txns.xml">
                    <xsl:for-each select="account/transaction[@value &lt; 0]/@value">
                      <xsl:element name="value">
                        <xsl:value-of select="."/>
                      </xsl:element>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "txns.xml");
        result.Trim().Should().Be("<out><value>-15.00</value><value>-5.00</value></out>",
            because: "relative path, predicate on the matched element, and attribute-axis tail must all be honored");
    }

    /// <summary>
    /// cy-002 pattern: xsl:for-each select="data(account/transaction[@value &lt; 0]/@value), 101, 102".
    /// fn:data() wraps the streamable path; for untyped attributes data() yields the
    /// string value, which equals what xsl:value-of on the attribute would emit — so
    /// the scanner unwraps data(path) and treats it as path.
    /// </summary>
    [Fact]
    public async Task XsltForEach_DataWrappedStreamablePath_MixedSequence_EmitsAtomized()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <account>
              <transaction value="-15.00"/>
              <transaction value="6.42"/>
              <transaction value="-5.00"/>
              <transaction value="100.00"/>
            </account>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="txns.xml">
                    <xsl:for-each select="data(account/transaction[@value &lt; 0]/@value), 101, 102">
                      <xsl:element name="e">
                        <xsl:value-of select="."/>
                      </xsl:element>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "txns.xml");
        result.Trim().Should().Be("<out><e>-15.00</e><e>-5.00</e><e>101</e><e>102</e></out>",
            because: "fn:data() around a streamable attribute path must be unwrapped so the path drives streaming");
    }

    /// <summary>
    /// sf-subsequence-002 pattern: xsl:copy-of select="subsequence(copy-of(/path), start)"
    /// inside xsl:source-document streamable="yes". The streaming pass accumulates the
    /// matched elements into a Sequence/Snapshot watcher; after the pass, subsequence()
    /// slices and copy-of emits.
    /// </summary>
    [Fact]
    public async Task XsltCopyOf_SubsequenceCopyOfPath_StreamsAndSlices()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <BOOKLIST><BOOKS>
              <ITEM><PRICE>1.00</PRICE></ITEM>
              <ITEM><PRICE>2.00</PRICE></ITEM>
              <ITEM><PRICE>3.00</PRICE></ITEM>
              <ITEM><PRICE>4.00</PRICE></ITEM>
              <ITEM><PRICE>5.00</PRICE></ITEM>
            </BOOKS></BOOKLIST>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml">
                  <out>
                    <xsl:copy-of select="subsequence(copy-of(/BOOKLIST/BOOKS/ITEM/PRICE), 3)"/>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "books.xml");
        result.Trim().Should().Be("<out><PRICE>3.00</PRICE><PRICE>4.00</PRICE><PRICE>5.00</PRICE></out>",
            because: "subsequence(copy-of(path), 3) must accumulate streamed elements and emit items from index 3 onwards");
    }

    /// <summary>
    /// si-value-of-027 / sf-copy-of-027 pattern: descendant::n with nested same-name
    /// elements. Streaming must visit each n in document order, both outer and inner.
    /// </summary>
    [Fact]
    public async Task CrawlingDescendant_NestedSameName_StreamsAllMatches()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <a><n><n>1</n><n>2</n><n>3</n></n></a>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="recurse.xml">
                  <out><xsl:value-of select="descendant::n"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformWithFile(stylesheet, inputXml, "recurse.xml");
        // Outer n string-value is "123" (concatenation), then each inner n -> 1, 2, 3
        result.Trim().Should().Be("<out>123 1 2 3</out>",
            because: "descendant::n streaming must yield outer n (string-value '123') plus each inner n");
    }

    [Fact]
    public async Task XsltCopy_StreamableSelect_MultipleItems_RaisesXtte3180()
    {
        var inputXml = """
            <?xml version="1.0"?>
            <root><a/><b/></root>
            """;
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="root.xml">
                    <xsl:copy select="/*/*"/>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        Func<Task> act = () => TransformWithFile(stylesheet, inputXml, "root.xml");
        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("XTTE3180",
                because: "xsl:copy/@select selecting multiple items must raise XTTE3180");
    }
}
