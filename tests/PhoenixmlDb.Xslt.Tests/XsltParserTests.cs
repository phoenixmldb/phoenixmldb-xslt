using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for StylesheetParser.
/// </summary>
public class XsltParserTests
{
    private readonly StylesheetParser _parser;
    private readonly MockExpressionParser _expressionParser;

    public XsltParserTests()
    {
        _expressionParser = new MockExpressionParser();
        _parser = new StylesheetParser(_expressionParser);
    }

    #region Basic Stylesheet Parsing

    [Fact]
    public void Parse_MinimalStylesheet_ReturnsStylesheetWithVersion()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Should().NotBeNull();
        stylesheet.Version.Should().Be("3.0");
        stylesheet.Templates.Should().BeEmpty();
    }

    [Fact]
    public void Parse_StylesheetWithTransformElement_ReturnsStylesheet()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:transform version="4.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
            </xsl:transform>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Should().NotBeNull();
        stylesheet.Version.Should().Be("4.0");
    }

    [Fact]
    public void Parse_StylesheetWithNamespaces_ParsesNamespaceDeclarations()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:fn="http://www.w3.org/2005/xpath-functions"
                            xmlns:my="http://example.com/my">
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Namespaces.Should().ContainKey("xsl");
        stylesheet.Namespaces.Should().ContainKey("xs");
        stylesheet.Namespaces.Should().ContainKey("fn");
        stylesheet.Namespaces.Should().ContainKey("my");
        stylesheet.Namespaces["my"].Should().Be("http://example.com/my");
    }

    [Fact]
    public void Parse_StylesheetWithExcludeResultPrefixes_ParsesPrefixes()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:my="http://example.com/my"
                            xmlns:other="http://example.com/other"
                            exclude-result-prefixes="my other">
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.ExcludeResultPrefixes.Should().Contain("my");
        stylesheet.ExcludeResultPrefixes.Should().Contain("other");
    }

    [Fact]
    public void Parse_StylesheetWithExcludeResultPrefixesAll_ParsesAll()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            exclude-result-prefixes="#all">
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert — #all expands to actual namespace prefixes in scope (excluding xsl and xml)
        stylesheet.ExcludeResultPrefixes.Should().Contain("xs");
    }

    #endregion

    #region Template Parsing

    [Fact]
    public void Parse_TemplateWithMatchPattern_ParsesMatchAndBody()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Templates.Should().HaveCount(1);
        var template = stylesheet.Templates[0];
        template.Match.Should().NotBeNull();
        template.Name.Should().BeNull();
    }

    [Fact]
    public void Parse_NamedTemplate_ParsesNameAndBody()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template name="myTemplate">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Templates.Should().HaveCount(1);
        var template = stylesheet.Templates[0];
        template.Name.Should().NotBeNull();
        template.Name!.Value.LocalName.Should().Be("myTemplate");
        stylesheet.NamedTemplates.Should().ContainKey(template.Name.Value);
    }

    [Fact]
    public void Parse_TemplateWithPriority_ParsesPriority()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item" priority="10">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Templates[0].Priority.Should().Be(10.0);
    }

    [Fact]
    public void Parse_TemplateWithMode_ParsesMode()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item" mode="toc">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Templates[0].Modes.Should().HaveCount(1);
        stylesheet.Templates[0].Modes[0].LocalName.Should().Be("toc");
    }

    [Fact]
    public void Parse_TemplateWithMultipleModes_ParsesAllModes()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item" mode="toc index summary">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var modes = stylesheet.Templates[0].Modes;
        modes.Should().HaveCount(3);
        modes.Select(m => m.LocalName).Should().BeEquivalentTo(["toc", "index", "summary"]);
    }

    [Fact]
    public void Parse_TemplateWithParams_ParsesParameters()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:param name="param1"/>
                    <xsl:param name="param2" required="yes"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Templates[0].Parameters.Should().HaveCount(2);
        stylesheet.Templates[0].Parameters[0].Name.LocalName.Should().Be("param1");
        stylesheet.Templates[0].Parameters[0].Required.Should().BeFalse();
        stylesheet.Templates[0].Parameters[1].Name.LocalName.Should().Be("param2");
        stylesheet.Templates[0].Parameters[1].Required.Should().BeTrue();
    }

    [Fact]
    public void Parse_TemplateWithTunnelParam_ParsesTunnelAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:param name="tunnelParam" tunnel="yes"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Templates[0].Parameters[0].Tunnel.Should().BeTrue();
    }

    #endregion

    #region Variable and Parameter Parsing

    [Fact]
    public void Parse_GlobalVariable_ParsesVariableDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:variable name="globalVar" select="'hello'"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Variables.Should().HaveCount(1);
        stylesheet.Variables[0].Name.LocalName.Should().Be("globalVar");
        stylesheet.Variables[0].Select.Should().NotBeNull();
    }

    [Fact]
    public void Parse_GlobalParam_ParsesParameterDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:param name="globalParam" select="'default'"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Parameters.Should().HaveCount(1);
        stylesheet.Parameters[0].Name.LocalName.Should().Be("globalParam");
    }

    [Fact]
    public void Parse_StaticVariable_ParsesStaticAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:variable name="staticVar" select="100" static="yes"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Variables[0].Static.Should().BeTrue();
    }

    #endregion

    #region Function Parsing

    [Fact]
    public void Parse_Function_ParsesFunctionDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:my="http://example.com/my">
                <xsl:function name="my:double">
                    <xsl:param name="n"/>
                    <xsl:sequence select="$n * 2"/>
                </xsl:function>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Functions.Should().HaveCount(1);
        var func = stylesheet.Functions.Values.First();
        func.Name.LocalName.Should().Be("double");
        func.Parameters.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_FunctionWithOverride_ParsesOverrideAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0"
                            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:my="http://example.com/my">
                <xsl:function name="my:func" override="no">
                    <xsl:sequence select="1"/>
                </xsl:function>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Functions.Values.First().Override.Should().BeFalse();
    }

    #endregion

    #region Key Parsing

    [Fact]
    public void Parse_Key_ParsesKeyDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:key name="itemById" match="item" use="@id"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Keys.Should().HaveCount(1);
        var key = stylesheet.Keys.Values.First();
        key.Name.LocalName.Should().Be("itemById");
        key.Match.Should().NotBeNull();
        key.Use.Should().NotBeNull();
    }

    [Fact]
    public void Parse_CompositeKey_ParsesCompositeAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:key name="compositeKey" match="item" use="concat(@a, @b)" composite="yes"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Keys.Values.First().Composite.Should().BeTrue();
    }

    #endregion

    #region Output Parsing

    [Fact]
    public void Parse_Output_ParsesOutputDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:output method="xml" encoding="UTF-8" indent="yes"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Outputs.Should().HaveCount(1);
        var output = stylesheet.Outputs[0];
        output.Method.Should().Be(OutputMethod.Xml);
        output.Encoding.Should().Be("UTF-8");
        output.Indent.Should().BeTrue();
    }

    [Fact]
    public void Parse_OutputHtml_ParsesHtmlMethod()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:output method="html"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Outputs[0].Method.Should().Be(OutputMethod.Html);
    }

    [Fact]
    public void Parse_OutputText_ParsesTextMethod()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:output method="text"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Outputs[0].Method.Should().Be(OutputMethod.Text);
    }

    [Fact]
    public void Parse_OutputJson_ParsesJsonMethod()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:output method="json"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Outputs[0].Method.Should().Be(OutputMethod.Json);
    }

    [Fact]
    public void Parse_OutputWithDoctype_ParsesDoctypeInfo()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:output method="html"
                            doctype-public="-//W3C//DTD HTML 4.01//EN"
                            doctype-system="http://www.w3.org/TR/html4/strict.dtd"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var output = stylesheet.Outputs[0];
        output.DoctypePublic.Should().Be("-//W3C//DTD HTML 4.01//EN");
        output.DoctypeSystem.Should().Be("http://www.w3.org/TR/html4/strict.dtd");
    }

    [Fact]
    public void Parse_NamedOutput_ParsesOutputName()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:output name="html-output" method="html"/>
                <xsl:output name="xml-output" method="xml"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Outputs.Should().HaveCount(2);
        stylesheet.Outputs[0].Name!.Value.LocalName.Should().Be("html-output");
        stylesheet.Outputs[1].Name!.Value.LocalName.Should().Be("xml-output");
    }

    #endregion

    #region Attribute Set Parsing

    [Fact]
    public void Parse_AttributeSet_ParsesAttributeSetDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:attribute-set name="common-attrs">
                    <xsl:attribute name="class">content</xsl:attribute>
                    <xsl:attribute name="id">main</xsl:attribute>
                </xsl:attribute-set>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.AttributeSets.Should().HaveCount(1);
        var attrSet = stylesheet.AttributeSets.Values.First();
        attrSet.Name.LocalName.Should().Be("common-attrs");
        attrSet.Attributes.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_AttributeSetWithUseAttributeSets_ParsesReference()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:attribute-set name="base-attrs">
                    <xsl:attribute name="base">base-value</xsl:attribute>
                </xsl:attribute-set>
                <xsl:attribute-set name="extended-attrs" use-attribute-sets="base-attrs">
                    <xsl:attribute name="extra">value</xsl:attribute>
                </xsl:attribute-set>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var attrSet = stylesheet.AttributeSets.Values.First(a => a.Name.LocalName == "extended-attrs");
        attrSet.UseAttributeSets.Should().HaveCount(1);
        attrSet.UseAttributeSets[0].LocalName.Should().Be("base-attrs");
    }

    #endregion

    #region Character Map Parsing

    [Fact]
    public void Parse_CharacterMap_ParsesCharacterMappings()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:character-map name="special-chars">
                    <xsl:output-character character="&lt;" string="[LESS-THAN]"/>
                    <xsl:output-character character="&gt;" string="[GREATER-THAN]"/>
                </xsl:character-map>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.CharacterMaps.Should().HaveCount(1);
        var charMap = stylesheet.CharacterMaps.Values.First();
        charMap.Name.LocalName.Should().Be("special-chars");
        charMap.Mappings.Should().HaveCount(2);
        charMap.Mappings['<'].Should().Be("[LESS-THAN]");
        charMap.Mappings['>'].Should().Be("[GREATER-THAN]");
    }

    #endregion

    #region Decimal Format Parsing

    [Fact]
    public void Parse_DecimalFormat_ParsesDecimalFormatSettings()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:decimal-format name="european"
                                    decimal-separator=","
                                    grouping-separator="."/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.DecimalFormats.Should().HaveCount(1);
        var format = stylesheet.DecimalFormats.Values.First();
        format.Name!.Value.LocalName.Should().Be("european");
        format.DecimalSeparator.Should().Be(",");
        format.GroupingSeparator.Should().Be(".");
    }

    [Fact]
    public void Parse_DefaultDecimalFormat_ParsesWithNoName()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:decimal-format decimal-separator="," grouping-separator=" "/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.DecimalFormats.Should().HaveCount(1);
        var format = stylesheet.DecimalFormats.Values.First();
        format.Name.Should().BeNull();
    }

    #endregion

    #region Strip/Preserve Space Parsing

    [Fact]
    public void Parse_StripSpace_ParsesElementNames()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:strip-space elements="p div span"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.StripSpace.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_PreserveSpace_ParsesElementNames()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:preserve-space elements="pre code"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.PreserveSpace.Should().HaveCount(2);
    }

    #endregion

    #region Accumulator Parsing

    [Fact]
    public void Parse_Accumulator_ParsesAccumulatorDeclaration()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:accumulator name="item-count" initial-value="0">
                    <xsl:accumulator-rule match="item" select="$value + 1"/>
                </xsl:accumulator>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Accumulators.Should().HaveCount(1);
        var acc = stylesheet.Accumulators.Values.First();
        acc.Name.LocalName.Should().Be("item-count");
        acc.Rules.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_StreamableAccumulator_ParsesStreamableAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:accumulator name="stream-acc" initial-value="0" streamable="yes">
                    <xsl:accumulator-rule match="item" select="$value + 1"/>
                </xsl:accumulator>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Accumulators.Values.First().Streamable.Should().BeTrue();
    }

    #endregion

    #region Simplified Stylesheet Parsing

    [Fact]
    public void Parse_SimplifiedStylesheet_CreatesImplicitTemplate()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xsl:version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <body>
                    <h1>Simplified Stylesheet</h1>
                </body>
            </html>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Should().NotBeNull();
        stylesheet.Version.Should().Be("3.0");
        stylesheet.Templates.Should().HaveCount(1);
        stylesheet.Templates[0].Match.Should().NotBeNull();
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Parse_UnmatchedAvtBrace_ThrowsXsltException()
    {
        // Arrange - expand-text="yes" enables text value templates where braces are validated
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            expand-text="yes">
                <xsl:template match="item">
                    <xsl:attribute name="class">{$value</xsl:attribute>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act & Assert
        var action = () => _parser.Parse(xslt);
        action.Should().Throw<XsltException>()
            .WithMessage("*Unmatched*");
    }

    [Fact]
    public void Parse_UnknownInstruction_ThrowsXsltException()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:unknown-instruction/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act & Assert
        var action = () => _parser.Parse(xslt);
        action.Should().Throw<XsltException>()
            .WithMessage("*Unknown*");
    }

    #endregion

    #region Attribute Value Template Parsing

    [Fact]
    public void Parse_AvtWithLiteral_ParsesLiteralPart()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:attribute name="class">literal-value</xsl:attribute>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var template = stylesheet.Templates[0];
        var instr = template.Body.Instructions[0] as XsltAttribute;
        instr.Should().NotBeNull();
    }

    [Fact]
    public void Parse_AvtWithExpression_ParsesExpressionPart()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:element name="{@name}">content</xsl:element>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var template = stylesheet.Templates[0];
        var instr = template.Body.Instructions[0] as XsltElement;
        instr.Should().NotBeNull();
        instr!.Name.Parts.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void Parse_AvtWithMixedContent_ParsesAllParts()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:element name="prefix-{@id}-suffix">content</xsl:element>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var template = stylesheet.Templates[0];
        var instr = template.Body.Instructions[0] as XsltElement;
        instr.Should().NotBeNull();
        instr!.Name.Parts.Should().HaveCount(3); // literal, expression, literal
    }

    [Fact]
    public void Parse_AvtWithEscapedBraces_ParsesCorrectly()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:attribute name="style">{{literal braces}}</xsl:attribute>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert - should not throw
        stylesheet.Templates.Should().HaveCount(1);
    }

    #endregion

    #region Pattern Parsing

    [Fact]
    public void Parse_UnionPattern_ParsesAlternatives()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item | product | entry">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var pattern = stylesheet.Templates[0].Match as UnionPattern;
        pattern.Should().NotBeNull();
        pattern!.Patterns.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_PathPattern_ParsesSteps()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root/item/name">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var pattern = stylesheet.Templates[0].Match as PathPattern;
        pattern.Should().NotBeNull();
        pattern!.Steps.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_AttributePattern_ParsesAttributeAxis()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="@id">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var pattern = stylesheet.Templates[0].Match as PathPattern;
        pattern.Should().NotBeNull();
        pattern!.Steps[0].Axis.Should().Be(Axis.Attribute);
    }

    #endregion
}

/// <summary>
/// Mock expression parser for testing.
/// </summary>
internal sealed class MockExpressionParser : IExpressionParser
{
    public XQueryExpression Parse(string expression)
    {
        // Return a simple placeholder expression for testing
        if (expression.StartsWith('\'') && expression.EndsWith('\''))
        {
            return new StringLiteral { Value = expression.Trim('\'') };
        }

        if (expression.StartsWith('$'))
        {
            return new VariableReference { Name = new QName(NamespaceId.None, expression[1..]) };
        }

        if (int.TryParse(expression, out var intValue))
        {
            return new IntegerLiteral { Value = intValue };
        }

        if (expression == ".")
        {
            return ContextItemExpression.Instance;
        }

        // Return a generic string literal for other expressions
        return new StringLiteral { Value = expression };
    }
}
