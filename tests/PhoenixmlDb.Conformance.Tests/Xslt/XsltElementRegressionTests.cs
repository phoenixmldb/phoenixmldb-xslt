using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// Regression tests for xsl:element issues identified in conformance testing.
/// These are fast, isolated tests for TDD-style development.
///
/// Run with: dotnet test --filter "FullyQualifiedName~XsltElementRegressionTests"
/// </summary>
[Trait("Category", "Regression")]
public class XsltElementRegressionTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new PhoenixmlDb.Xslt.XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    #region Issue: xsl:where-populated should suppress empty elements

    [Fact]
    public async Task WherePopulated_EmptyElement_ShouldBeSuppressed()
    {
        // element-0104: xsl:where-populated should suppress empty <name/> element
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <one>
                            <xsl:where-populated>
                                <xsl:element name="name">
                                    <xsl:if test="1 = 1"><e/></xsl:if>
                                </xsl:element>
                            </xsl:where-populated>
                        </one>
                        <two>
                            <xsl:where-populated>
                                <xsl:element name="name">
                                    <xsl:if test="1 = 2">never</xsl:if>
                                </xsl:element>
                            </xsl:where-populated>
                        </two>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        // <two> should NOT contain <name/> because the element is empty
        result.Should().Contain("<one><name><e/></name></one>");
        result.Should().Contain("<two/>");
        result.Should().NotContain("<two><name");
    }

    [Fact]
    public async Task WherePopulated_ElementWithContent_ShouldBeIncluded()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:where-populated>
                            <xsl:element name="result">
                                <child/>
                            </xsl:element>
                        </xsl:where-populated>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Contain("<result><child/></result>");
    }

    [Fact]
    public async Task WherePopulated_EmptyText_ShouldBeSuppressed()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:where-populated>
                            <xsl:element name="empty">
                                <xsl:value-of select="''"/>
                            </xsl:element>
                        </xsl:where-populated>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        // Empty value-of should result in suppressed element
        result.Should().Be("<out/>");
    }

    #endregion

    #region Issue: xsl:sequence with attribute nodes should become element attributes

    [Fact]
    public async Task Sequence_AttributeNodes_ShouldBecomeElementAttributes()
    {
        // element-0101: <xsl:sequence select="//e1/@*"/> inside xsl:element
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:element name="Asset">
                            <xsl:sequence select="//e1/@*"/>
                            <child>content</child>
                        </xsl:element>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<doc><e1 type="junk" value="0.0"/></doc>""";
        var result = await TransformAsync(stylesheet, input);

        // Attributes from xsl:sequence should become attributes of <Asset>
        result.Should().Contain("<Asset");
        result.Should().Contain("type=\"junk\"");
        result.Should().Contain("value=\"0.0\"");
        result.Should().Contain("<child>content</child>");
    }

    [Fact]
    public async Task Sequence_SingleAttribute_ShouldBecomeElementAttribute()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:element name="result">
                            <xsl:sequence select="/root/@id"/>
                        </xsl:element>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<root id="123"/>""";
        var result = await TransformAsync(stylesheet, input);

        result.Should().Contain("<result");
        result.Should().Contain("id=\"123\"");
    }

    #endregion

    #region Issue: inherit-namespaces="no" should emit xmlns="" undeclaration

    [Fact]
    public async Task Element_InheritNamespacesNo_ShouldUndeclareNamespace()
    {
        // element-0102: inherit-namespaces="no" on child element
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:element name="Outer" namespace="http://www.test.com">
                            <xsl:element name="Inner" inherit-namespaces="no">
                                <child/>
                            </xsl:element>
                        </xsl:element>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        // Inner should undeclare the default namespace
        result.Should().Contain("xmlns=\"http://www.test.com\"");
        result.Should().Contain("<Inner");
        result.Should().Contain("xmlns=\"\"");
    }

    [Fact]
    public async Task Element_InheritNamespacesYes_ShouldInheritNamespace()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:element name="Outer" namespace="http://www.test.com">
                            <xsl:element name="Inner">
                                <child/>
                            </xsl:element>
                        </xsl:element>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        // Per XSLT 3.0 §11.9.1: xsl:element without namespace attribute uses the
        // *static* default namespace from the stylesheet, not the runtime parent's namespace.
        // Since the stylesheet has no default namespace, Inner is in no namespace,
        // so xmlns="" is emitted to undeclare the parent's default namespace.
        result.Should().Contain("xmlns=\"http://www.test.com\"");
        result.Should().Contain("<Inner xmlns=\"\">");
        // Two xmlns declarations: one on Outer (the namespace), one on Inner (undeclaration)
        result.Split("xmlns=").Length.Should().Be(3); // 2 declarations = 3 parts
    }

    #endregion

    #region Issue: xsl:number lang attribute should use language-specific formatting

    [Fact]
    public async Task Number_GermanLang_ShouldProduceGermanWords()
    {
        // number-0802: xsl:number with lang="de" should produce German words
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out>
                        <xsl:number value="3" format="w" lang="de"/>;
                        <xsl:number value="13" format="w" lang="de"/>;
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Contain("drei");
        result.Should().Contain("dreizehn");
        result.Should().NotContain("three");
        result.Should().NotContain("thirteen");
    }

    #endregion

    #region Issue: xsl:number with empty sequence should produce no output

    [Fact]
    public async Task Number_EmptySequence_ShouldProduceNoOutput()
    {
        // number-0814: empty sequence value should produce no output in XSLT 2.0+
        // Exact match to conformance test
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="doc">
                    <out>
                        <xsl:variable name="empty" select="()"/>
                        <v2><xsl:number value="$empty"/></v2>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // Empty sequence should produce empty element, not NaN
        result.Should().Be("<out><v2/></out>");
    }

    [Fact]
    public async Task Number_EmptySequence_WithBackwardsCompatibility()
    {
        // number-0814 full test: v2 mode = empty, v1 mode = NaN
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
            <xsl:output encoding="iso-8859-1"/>

              <xsl:template match="doc">
                <out>
                    <xsl:variable name="empty" select="()"/>
                    <v2><xsl:number value="$empty"/></v2>
                    <v1 xsl:version="1.0"><xsl:number value="$empty"/></v1>
                </out>
              </xsl:template>

            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // v2 (XSLT 2.0 mode): empty sequence = empty output
        // v1 (XSLT 1.0 backwards compatible): empty sequence = NaN
        result.Should().Contain("<out><v2/><v1>NaN</v1></out>");
    }

    #endregion

    #region Issue: xsl:number format with single punctuation should be prefix AND suffix

    [Fact]
    public async Task Number_SinglePunctuationFormat_ShouldBePrefixAndSuffix()
    {
        // number-0810: format="*" should produce *1*
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out><xsl:number value="1" format="*"/></out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Be("<out>*1*</out>");
    }

    [Fact]
    public async Task Number_EmptyFormat_ShouldDefaultToNumeric()
    {
        // number-0811: format="" should produce just the number
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="/">
                    <out><xsl:number value="1" format=""/></out>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<root/>");

        result.Should().Be("<out>1</out>");
    }

    #endregion

    #region Issue: Simplified stylesheets should work

    [Fact]
    public async Task SimplifiedStylesheet_NumberFormat_ShouldWork()
    {
        // number-0810: simplified stylesheet with format="*"
        // The document element is both the stylesheet AND the template body
        var stylesheet = """
            <doc xsl:version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:number format="*"/>
            </doc>
            """;

        // Use the actual test environment input: <doc/>
        var result = await TransformAsync(stylesheet, "<doc/>");

        // xsl:number with no value/select counts the context node (should be 1)
        // format="*" means prefix AND suffix, so *1*
        result.Should().Be("<doc>*1*</doc>");
    }

    [Fact]
    public async Task SimplifiedStylesheet_EmptyFormat_ShouldWork()
    {
        // number-0811: simplified stylesheet with format=""
        var stylesheet = """
            <doc xsl:version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:number format=""/>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // format="" defaults to numeric "1"
        result.Should().Be("<doc>1</doc>");
    }

    [Fact]
    public async Task SimplifiedStylesheet_Debug_ShouldShowOutput()
    {
        // Debug test to see what xsl:number produces in a simplified stylesheet
        var stylesheet = """
            <doc xsl:version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:value-of select="'hello'"/>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // Just value-of should work
        result.Should().Be("<doc>hello</doc>");
    }

    [Fact]
    public async Task SimplifiedStylesheet_NumberWithValue_ShouldWork()
    {
        // Test with explicit value to isolate the issue
        var stylesheet = """
            <doc xsl:version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:number value="1" format="*"/>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // With explicit value=1 and format="*", should output *1*
        result.Should().Be("<doc>*1*</doc>");
    }

    [Fact]
    public async Task NormalStylesheet_NumberWithoutValue_ShouldWork()
    {
        // Test counting context node in a normal stylesheet
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out><xsl:number format="*"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // xsl:number with no value should count context node (document), which is position 1
        result.Should().Be("<out>*1*</out>");
    }

    [Fact]
    public async Task SimplifiedStylesheet_NumberWithSelect_ShouldWork()
    {
        // Test with select="." to isolate counting logic vs parsing issue
        var stylesheet = """
            <doc xsl:version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:number select="." format="*"/>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // select="." should count the context node (document)
        result.Should().Be("<doc>*1*</doc>");
    }

    [Fact]
    public async Task Number_FractionalValue_ShouldRound()
    {
        // number-0805: test numbering of fractional value argument
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="doc">
                <out>
                    <xsl:number value="3.6" format="01"/>|<xsl:number value="0.3" format="01"/>|<xsl:number value="0.7" format="01"/>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // 3.6 rounds to 4, 0.3 rounds to 0, 0.7 rounds to 1
        result.Should().Be("<out>04|00|01</out>");
    }

    [Fact]
    public async Task Number_FractionalValue_FullConformanceMatch()
    {
        // Exact number-0805.xsl test (partial - excluding 200 div 3 which has XPath evaluator issue)
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out>
                    10 = <xsl:number value="10" format="01" />;
                    3.6 = <xsl:number value="3.6" format="01" />;
                    0 = <xsl:number value="0" format="01" />;
                    0.3 = <xsl:number value="0.3" format="01"  />;
                    0.7 = <xsl:number value="0.7" format="01" />;
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // Compare with expected output (normalizing whitespace)
        result.Should().Contain("10 = 10;");
        result.Should().Contain("3.6 = 04;");
        result.Should().Contain("0 = 00;");
        result.Should().Contain("0.3 = 00;");
        result.Should().Contain("0.7 = 01;");
        // Note: 200 div 3 = 67 test excluded - XPath div operator returns integer 66 instead of float 66.67
    }

    #endregion

    #region From Attribute Tests

    [Fact]
    public async Task Number_FromAttribute_LevelAny_ShouldResetAtFromNode()
    {
        // Simplified number-2801 test: from="chapter" with level="any"
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out><xsl:apply-templates/></out>
              </xsl:template>
              <xsl:template match="note">
                <xsl:number level="any" from="chapter" format="(1) "/>
                <xsl:apply-templates/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """
            <doc>
              <note>aaa</note>
              <note>bbb</note>
              <chapter>
                <note>ddd</note>
                <note>eee</note>
              </chapter>
              <note>ggg</note>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, input);

        // Before first chapter: notes should be numbered (1), (2)
        result.Should().Contain("(1) aaa");
        result.Should().Contain("(2) bbb");
        // After chapter starts: counting resets, so (1), (2) inside chapter
        result.Should().Contain("(1) ddd");
        result.Should().Contain("(2) eee");
        // After chapter ends: continues from (3)
        result.Should().Contain("(3) ggg");
    }

    [Fact]
    public async Task Number_FromAttribute_FullNumber2801_ShouldMatch()
    {
        // Exact number-2801 test
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out>
                  <xsl:apply-templates/>
                </out>
              </xsl:template>
              <xsl:template match="note">
                <xsl:number level="any" from="chapter" format="(1) "/>
                <xsl:apply-templates/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """
            <?xml version="1.0"?>
            <doc>
              <note>aaa</note>
              <note>bbb</note>
              <note>ccc</note>
              <chapter>
                <note>ddd</note>
                <note>eee</note>
                <note>fff</note>
              </chapter>
              <note>ggg</note>
              <note>hhh</note>
              <note>iii</note>
              <chapter>
                <note>jjj</note>
                <note>kkk</note>
                <note>lll</note>
              </chapter>
              <note>mmm</note>
              <note>nnn</note>
              <note>ooo</note>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, input);

        // Expected: notes before first chapter numbered 1,2,3
        result.Should().Contain("(1) aaa");
        result.Should().Contain("(2) bbb");
        result.Should().Contain("(3) ccc");
        // Notes inside first chapter: reset to 1,2,3
        result.Should().Contain("(1) ddd");
        result.Should().Contain("(2) eee");
        result.Should().Contain("(3) fff");
        // Notes after first chapter: continue 4,5,6
        result.Should().Contain("(4) ggg");
        result.Should().Contain("(5) hhh");
        result.Should().Contain("(6) iii");
        // Notes inside second chapter: reset to 1,2,3
        result.Should().Contain("(1) jjj");
        result.Should().Contain("(2) kkk");
        result.Should().Contain("(3) lll");
        // Notes after second chapter: continue 4,5,6
        result.Should().Contain("(4) mmm");
        result.Should().Contain("(5) nnn");
        result.Should().Contain("(6) ooo");
    }

    [Fact]
    public async Task Number_LevelMultiple_AlternatingSeparators()
    {
        // Test from number-3201: format="1+1-1+1-1" with level="multiple"
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out><xsl:apply-templates/></out>
              </xsl:template>
              <xsl:template match="title">
                <xsl:number level="multiple" count="a|b|c|d|e" format="1+1-1+1-1"/>: <xsl:value-of select="."/>;
              </xsl:template>
              <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;

        // Simplified structure similar to number-32.xml
        var input = """
            <doc>
              <a><title>Level A</title>
                <b><title>Level B</title>
                  <c><title>Level C</title></c>
                </b>
              </a>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, input);

        // Expected numbering with format="1+1-1+1-1":
        // a -> 1
        // a/b -> 1+1
        // a/b/c -> 1+1-1
        result.Should().Contain("1: Level A");
        result.Should().Contain("1+1: Level B");
        result.Should().Contain("1+1-1: Level C");
    }

    [Fact]
    public async Task Number_LevelMultiple_DeepNesting_SeparatorRepeat()
    {
        // When there are more levels than separators, the last separator should repeat
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out><xsl:apply-templates/></out>
              </xsl:template>
              <xsl:template match="x">
                [<xsl:number level="multiple" count="x" format="1-1"/>]<xsl:apply-templates/>
              </xsl:template>
              <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;

        // 4 levels deep
        var input = "<doc><x><x><x><x/></x></x></x></doc>";

        var result = await TransformAsync(stylesheet, input);

        // format="1-1" has only one separator "-"
        // 4 levels should produce "1-1-1-1" (repeating the last separator)
        result.Should().Contain("[1]");      // level 1
        result.Should().Contain("[1-1]");    // level 2
        result.Should().Contain("[1-1-1]");  // level 3
        result.Should().Contain("[1-1-1-1]"); // level 4 - should repeat "-"
    }

    [Fact]
    public async Task Number_LevelMultiple_FirstTitleNoAncestors()
    {
        // When title is not inside a|b|c|d|e, it should produce empty number
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out><xsl:apply-templates/></out>
              </xsl:template>
              <xsl:template match="title">
                <xsl:number level="multiple" count="a|b|c|d|e" format="1+1"/>: <xsl:value-of select="."/>;
              </xsl:template>
              <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;

        // Title directly under doc, not inside a|b|c|d|e
        var input = """
            <doc>
              <title>Root Title</title>
              <a><title>Level A</title></a>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, input);

        // First title has no matching ancestors, produces empty number
        result.Should().Contain(": Root Title");
        // Second title inside <a> produces "1"
        result.Should().Contain("1: Level A");
    }

    [Fact]
    public async Task Number_CountDot_LevelMultiple()
    {
        // Test count="." with level="multiple" (XSLT 3.0 feature)
        // With level="multiple" and count=".", we count at each element level where "." matches
        // "." pattern becomes self::* which only matches elements (not document nodes)
        var stylesheet = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/">
                    <out>
                        <xsl:apply-templates select="//number"/>
                    </out>
                </xsl:template>
                <xsl:template match="number">
                    <n><xsl:number count="." format="0" level="multiple"/></n>
                </xsl:template>
            </xsl:stylesheet>
            """;

        var input = "<root><number>234</number><number>9234</number><number>1234</number></root>";
        var result = await TransformAsync(stylesheet, input);

        // With level="multiple" and count=".", every ancestor-or-self is counted:
        // document(1) . root(1) . number(1|2|3) = three levels
        // format="0" uses default separator "." between levels
        // This matches W3C test number-0110 expected output
        result.Should().Contain("<n>1.1.1</n>");
        result.Should().Contain("<n>1.1.2</n>");
        result.Should().Contain("<n>1.1.3</n>");
    }

    [Fact]
    public async Task Number_3201_ExactExpectedOutput()
    {
        // Check exact output format for debugging conformance test failures
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
            <xsl:template match="doc">
              <out><xsl:apply-templates/></out>
            </xsl:template>
            <xsl:template match="title"><xsl:number level="multiple" count="a|b|c|d|e" format="1+1-1+1-1"/><xsl:text>: </xsl:text><xsl:value-of select="."/>
            </xsl:template>
            <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;

        var input = """
            <?xml version="1.0"?>
            <doc>
              <title>Test for source tree numbering</title>
              <a>
                <title>Level A</title>
                <b>
                  <title>Level B</title>
                </b>
                <b>
                  <title>Level B</title>
                  <c>
                    <title>Level C</title>
                  </c>
                </b>
                <b>
                  <title>Level B</title>
                  <c>
                    <title>Level C</title>
                    <d>
                      <title>Level D</title>
                    </d>
                  </c>
                </b>
              </a>
              <a>
                <title>Level A</title>
                <b>
                  <title>Level B</title>
                  <c>
                    <title>Level C</title>
                    <d>
                      <title>Level D</title>
                      <e>
                        <title>Level E</title>
                      </e>
                    </d>
                  </c>
                </b>
              </a>
            </doc>
            """;

        var result = await TransformAsync(stylesheet, input);

        // Expected from number-3201.out
        result.Should().Contain(": Test for source tree numbering");
        result.Should().Contain("1: Level A");
        result.Should().Contain("1+1: Level B");
        result.Should().Contain("1+2: Level B");
        result.Should().Contain("1+2-1: Level C");
        result.Should().Contain("1+3: Level B");
        result.Should().Contain("1+3-1: Level C");
        result.Should().Contain("1+3-1+1: Level D");
        result.Should().Contain("2: Level A");
        result.Should().Contain("2+1: Level B");
        result.Should().Contain("2+1-1: Level C");
        result.Should().Contain("2+1-1+1: Level D");
        result.Should().Contain("2+1-1+1-1: Level E");
    }

    #endregion

    #region XPath Division Tests

    [Fact]
    public async Task XPath_Div_ShouldReturnDecimal()
    {
        // XPath div operator should return decimal/float, not integer
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="/">
                <out><xsl:value-of select="200 div 3"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // 200 div 3 = 66.666... (should NOT be integer 66)
        result.Should().Contain("66.6");
    }

    [Fact]
    public async Task XPath_Idiv_ShouldReturnInteger()
    {
        // XPath idiv operator should return integer
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="/">
                <out><xsl:value-of select="200 idiv 3"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // 200 idiv 3 = 66 (integer division)
        result.Should().Contain(">66<");
    }

    [Fact]
    public async Task Number_WithDivExpression_ShouldRoundCorrectly()
    {
        // Original failing test from number-0805: 200 div 3 should round to 67
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="doc">
                <out>200 div 3 = <xsl:number value="200 div 3" format="01"/>;</out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // 200 div 3 = 66.666... rounds to 67
        result.Should().Contain("200 div 3 = 67;");
    }

    #endregion

    #region Unicode Number Format Tests

    [Fact]
    public async Task Number_CircledDigits_WithZero()
    {
        // Test circled digit format including zero
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="/">
                <out>
                    <xsl:for-each select="0 to 5">
                        <xsl:number value="." format="①"/><xsl:text> </xsl:text>
                    </xsl:for-each>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // ⓪ ① ② ③ ④ ⑤
        result.Should().Contain("⓪"); // Circled zero
        result.Should().Contain("①"); // Circled one
        result.Should().Contain("⑤"); // Circled five
    }

    [Fact]
    public async Task Number_CircledDigits_Overflow()
    {
        // When number exceeds circled digit range, fall back to Arabic numerals
        var stylesheet = """
            <?xml version="1.0"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="/">
                <out>
                    <xsl:number value="100" format="①"/>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");

        // 100 is outside circled digit range (0-50), should fall back to "100"
        result.Should().Contain("100");
    }

    #endregion

    #region Issue: Parentless attribute node matching (match-102 family)

    [Fact]
    public async Task ParentlessAttribute_ShouldBeOutputByBuiltInTemplate()
    {
        // Test 1: value-of on attribute variable
        var stylesheet1 = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="att" as="attribute()">
                <xsl:attribute name="my_att">att-value</xsl:attribute>
              </xsl:variable>
              <xsl:template match="doc">
                <out><xsl:value-of select="$att"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = "<doc attribute=\"attr_val\"/>";
        var result1 = await TransformAsync(stylesheet1, input);
        // Check: does value-of $att produce the attribute value?
        Assert.Equal("<out>att-value</out>", result1);

        // Test 2: apply-templates on attribute variable
        var stylesheet2 = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="att" as="attribute()">
                <xsl:attribute name="my_att">att-value</xsl:attribute>
              </xsl:variable>
              <xsl:template match="doc">
                <out><xsl:apply-templates select="$att"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result2 = await TransformAsync(stylesheet2, input);
        Assert.Equal("<out>att-value</out>", result2);
    }

    #endregion

    #region Issue: Muenchian grouping with key() and generate-id()

    [Fact]
    public async Task KeyFunction_ShouldReturnSameNodesAsDocument()
    {
        // Debug: check if key() returns nodes with same generate-id as original
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="k" match="item" use="@type"/>
              <xsl:template match="root">
                <out>
                  <direct><xsl:value-of select="generate-id(item[1])"/></direct>
                  <via-key><xsl:value-of select="generate-id(key('k','a')[1])"/></via-key>
                  <match><xsl:value-of select="generate-id(item[1]) = generate-id(key('k','a')[1])"/></match>
                  <key-count><xsl:value-of select="count(key('k','a'))"/></key-count>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = """<root><item type="a">X</item><item type="b">Y</item><item type="a">Z</item></root>""";
        var result = await TransformAsync(stylesheet, input);
        // key-count should be 2 (two items with type="a")
        result.Should().Contain("<key-count>2</key-count>");
        // generate-id should match
        result.Should().Contain("<match>true</match>");
    }

    [Fact]
    public async Task MuenchianGrouping_GenerateIdShouldMatchKeyResult()
    {
        // Muenchian grouping: item[generate-id() = generate-id(key('k',@type)[1])]
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="k" match="item" use="@type"/>
              <xsl:template match="root">
                <out>
                  <xsl:for-each select="item[generate-id() = generate-id(key('k',@type)[1])]">
                    <g type="{@type}"/>
                  </xsl:for-each>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = """<root><item type="a">X</item><item type="b">Y</item><item type="a">Z</item></root>""";
        var result = await TransformAsync(stylesheet, input);
        // Should have exactly 2 groups (a and b), not 3
        result.Should().Contain("type=\"a\"");
        result.Should().Contain("type=\"b\"");
    }

    #endregion

    #region Issue: key() in match patterns (key-030, key-038, etc.)

    [Fact]
    public async Task KeyPatternInMatch_ShouldMatchDescendants()
    {
        // Simplified version of key-030: key('mykey','Introduction')//p
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="mykey" match="div" use="title"/>
              <xsl:template match="doc">
                <out><xsl:apply-templates/></out>
              </xsl:template>
              <xsl:template match="div"><xsl:apply-templates/></xsl:template>
              <xsl:template match="p"><xsl:apply-templates/></xsl:template>
              <xsl:template match="key('mykey','Introduction')//p">FOUND:<xsl:value-of select="."/>;</xsl:template>
              <xsl:template match="text()"/>
            </xsl:stylesheet>
            """;
        var input = """
            <doc>
              <div><title>Introduction</title><p>Hello</p><p>World</p></div>
              <div><title>Other</title><p>Ignored</p></div>
            </doc>
            """;
        var result = await TransformAsync(stylesheet, input);
        result.Should().Contain("FOUND:Hello");
        result.Should().Contain("FOUND:World");
        result.Should().NotContain("FOUND:Ignored");
    }

    [Fact]
    public async Task KeyPatternInMatch_SimpleMatch()
    {
        // key('k', 'v') without continuation — match nodes directly in key result
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="k" match="item" use="@type"/>
              <xsl:template match="/">
                <out><xsl:apply-templates select="//item"/></out>
              </xsl:template>
              <xsl:template match="key('k', 'special')">MATCH:<xsl:value-of select="."/>;</xsl:template>
              <xsl:template match="item"><xsl:value-of select="."/>;</xsl:template>
            </xsl:stylesheet>
            """;
        var input = """<root><item type="normal">A</item><item type="special">B</item><item type="normal">C</item></root>""";
        var result = await TransformAsync(stylesheet, input);
        result.Should().Contain("MATCH:B");
        result.Should().NotContain("MATCH:A");
    }

    #endregion

    #region Issue: Typed key comparison and RTF handling

    [Fact]
    public async Task TypedKeyComparison_StringKeyLookup_ShouldMatch()
    {
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="k" match="item" use="@type"/>
              <xsl:template match="root">
                <out>
                  <count><xsl:value-of select="count(key('k','a'))"/></count>
                  <values><xsl:for-each select="key('k','a')"><xsl:value-of select="."/>,</xsl:for-each></values>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = """<root><item type="a">X</item><item type="b">Y</item><item type="a">Z</item></root>""";
        var result = await TransformAsync(stylesheet, input);
        Assert.Contains("<count>2</count>", result);
    }

    [Fact]
    public async Task KeyPatternWithVariable_ShouldMatchDescendants()
    {
        // Regression: key() in match pattern with $variable reference (RTF value)
        // Tests that ResultTreeFragment values are properly atomized to strings
        var stylesheet = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:key name="k1" match="*" use="Name/@First"/>
              <xsl:key name="k2" match="key('k1', $name)//*" use="@ID"/>
              <xsl:param name="name">Ted</xsl:param>
              <xsl:template match="/">
                <out><xsl:value-of select="count(key('k2', 'id8'))"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = """
            <Tree>
              <Level2 ID="id3">
                <Name First="Ted">Ted</Name>
                <Level3 ID="id8"><Name First="Joshua">Joshua</Name></Level3>
              </Level2>
            </Tree>
            """;
        var result = await TransformAsync(stylesheet, input);
        Assert.Equal("<out>1</out>", result);
    }

    #endregion

}
