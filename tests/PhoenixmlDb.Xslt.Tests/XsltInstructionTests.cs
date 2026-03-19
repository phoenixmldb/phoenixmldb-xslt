using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for XSLT instruction parsing and structure.
/// </summary>
public class XsltInstructionTests
{
    private readonly StylesheetParser _parser;

    public XsltInstructionTests()
    {
        _parser = new StylesheetParser(new MockExpressionParser());
    }

    #region xsl:template Tests

    [Fact]
    public void Template_WithMatch_CreatesTemplateRule()
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
        var template = stylesheet.Templates[0];

        // Assert
        template.Match.Should().NotBeNull();
        template.Name.Should().BeNull();
        template.Body.Should().NotBeNull();
        template.Body.Instructions.Should().HaveCount(1);
    }

    [Fact]
    public void Template_WithName_CreatesNamedTemplate()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template name="formatDate">
                    <xsl:param name="date"/>
                    <formatted-date/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var template = stylesheet.Templates[0];

        // Assert
        template.Name.Should().NotBeNull();
        template.Name!.Value.LocalName.Should().Be("formatDate");
        stylesheet.NamedTemplates.Should().ContainKey(template.Name.Value);
    }

    [Fact]
    public void Template_WithBothMatchAndName_CreatesBoth()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item" name="processItem">
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var template = stylesheet.Templates[0];

        // Assert
        template.Match.Should().NotBeNull();
        template.Name.Should().NotBeNull();
    }

    #endregion

    #region xsl:apply-templates Tests

    [Fact]
    public void ApplyTemplates_Basic_ParsesCorrectly()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:apply-templates/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltApplyTemplates;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().BeNull();
        instruction.Mode.Should().BeNull();
    }

    [Fact]
    public void ApplyTemplates_WithSelect_ParsesSelectExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:apply-templates select="item"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltApplyTemplates;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
    }

    [Fact]
    public void ApplyTemplates_WithMode_ParsesMode()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:apply-templates select="item" mode="toc"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltApplyTemplates;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Mode.Should().NotBeNull();
        instruction.Mode!.Value.LocalName.Should().Be("toc");
    }

    [Fact]
    public void ApplyTemplates_WithSort_ParsesSortCriteria()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:apply-templates select="item">
                        <xsl:sort select="@name"/>
                    </xsl:apply-templates>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltApplyTemplates;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Sorts.Should().HaveCount(1);
        instruction.Sorts[0].Select.Should().NotBeNull();
    }

    [Fact]
    public void ApplyTemplates_WithParams_ParsesParams()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:apply-templates select="item">
                        <xsl:with-param name="depth" select="1"/>
                    </xsl:apply-templates>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltApplyTemplates;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.WithParams.Should().HaveCount(1);
        instruction.WithParams[0].Name.LocalName.Should().Be("depth");
    }

    #endregion

    #region xsl:call-template Tests

    [Fact]
    public void CallTemplate_Basic_ParsesTemplateName()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:call-template name="formatItem"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltCallTemplate;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Name.LocalName.Should().Be("formatItem");
    }

    [Fact]
    public void CallTemplate_WithParams_ParsesParams()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:call-template name="formatItem">
                        <xsl:with-param name="value" select="@id"/>
                        <xsl:with-param name="label" select="'Item'"/>
                    </xsl:call-template>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltCallTemplate;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.WithParams.Should().HaveCount(2);
    }

    #endregion

    #region xsl:for-each Tests

    [Fact]
    public void ForEach_Basic_ParsesSelectAndBody()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:for-each select="item">
                        <result/>
                    </xsl:for-each>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltForEach;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
        instruction.Body.Should().NotBeNull();
        instruction.Body.Instructions.Should().HaveCount(1);
    }

    [Fact]
    public void ForEach_WithSort_ParsesSortCriteria()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:for-each select="item">
                        <xsl:sort select="@name" order="ascending"/>
                        <xsl:sort select="@date" order="descending"/>
                        <result/>
                    </xsl:for-each>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltForEach;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Sorts.Should().HaveCount(2);
    }

    #endregion

    #region xsl:if Tests

    [Fact]
    public void If_Basic_ParsesTestAndBody()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:if test="@active = 'yes'">
                        <active/>
                    </xsl:if>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltIf;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Test.Should().NotBeNull();
        instruction.Then.Should().NotBeNull();
        instruction.Then.Instructions.Should().HaveCount(1);
    }

    #endregion

    #region xsl:choose Tests

    [Fact]
    public void Choose_WithWhenAndOtherwise_ParsesAll()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:choose>
                        <xsl:when test="@type = 'A'"><typeA/></xsl:when>
                        <xsl:when test="@type = 'B'"><typeB/></xsl:when>
                        <xsl:otherwise><unknown/></xsl:otherwise>
                    </xsl:choose>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltChoose;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.When.Should().HaveCount(2);
        instruction.Otherwise.Should().NotBeNull();
    }

    [Fact]
    public void Choose_WithoutOtherwise_ParsesWhensOnly()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:choose>
                        <xsl:when test="@type = 'A'"><typeA/></xsl:when>
                        <xsl:when test="@type = 'B'"><typeB/></xsl:when>
                    </xsl:choose>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltChoose;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.When.Should().HaveCount(2);
        instruction.Otherwise.Should().BeNull();
    }

    #endregion

    #region xsl:value-of Tests

    [Fact]
    public void ValueOf_WithSelect_ParsesSelectExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:value-of select="@name"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltValueOf;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
    }

    [Fact]
    public void ValueOf_WithSeparator_ParsesSeparatorAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:value-of select="item" separator=", "/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltValueOf;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Separator.Should().NotBeNull();
        instruction.Separator!.Parts.Should().HaveCount(1);
        (instruction.Separator.Parts[0] as AvtLiteral)!.Value.Should().Be(", ");
    }

    [Fact]
    public void ValueOf_WithDisableOutputEscaping_ParsesAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:value-of select="@html" disable-output-escaping="yes"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltValueOf;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.DisableOutputEscaping.Should().BeTrue();
    }

    #endregion

    #region xsl:copy-of Tests

    [Fact]
    public void CopyOf_Basic_ParsesSelectExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:copy-of select="."/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltCopyOf;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
    }

    [Fact]
    public void CopyOf_WithCopyNamespaces_ParsesAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:copy-of select="." copy-namespaces="no"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltCopyOf;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.CopyNamespaces.Should().BeFalse();
    }

    #endregion

    #region xsl:element Tests

    [Fact]
    public void Element_Basic_ParsesNameAndContent()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:element name="result">
                        <child/>
                    </xsl:element>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Name.Should().NotBeNull();
        instruction.Content.Should().NotBeNull();
    }

    [Fact]
    public void Element_WithDynamicName_ParsesAvt()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:element name="{@tag}">content</xsl:element>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Name.Parts.Should().NotBeEmpty();
    }

    [Fact]
    public void Element_WithNamespace_ParsesNamespaceAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:element name="result" namespace="http://example.com">content</xsl:element>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Namespace.Should().NotBeNull();
    }

    [Fact]
    public void Element_WithUseAttributeSets_ParsesAttributeSets()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:attribute-set name="common-attrs">
                    <xsl:attribute name="class">default</xsl:attribute>
                </xsl:attribute-set>
                <xsl:attribute-set name="table-attrs">
                    <xsl:attribute name="border">1</xsl:attribute>
                </xsl:attribute-set>
                <xsl:template match="item">
                    <xsl:element name="div" use-attribute-sets="common-attrs table-attrs">content</xsl:element>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.UseAttributeSets.Should().HaveCount(2);
    }

    #endregion

    #region xsl:attribute Tests

    [Fact]
    public void Attribute_Basic_ParsesNameAndValue()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <result>
                        <xsl:attribute name="class">content</xsl:attribute>
                    </result>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var element = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;
        var attribute = element!.Content.Instructions[0] as XsltAttribute;

        // Assert
        attribute.Should().NotBeNull();
        attribute!.Name.Should().NotBeNull();
    }

    [Fact]
    public void Attribute_WithSelect_ParsesSelectExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <result>
                        <xsl:attribute name="id" select="@id"/>
                    </result>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var element = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;
        var attribute = element!.Content.Instructions[0] as XsltAttribute;

        // Assert
        attribute.Should().NotBeNull();
        attribute!.Select.Should().NotBeNull();
    }

    [Fact]
    public void Attribute_WithSeparator_ParsesSeparator()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <result>
                        <xsl:attribute name="classes" separator=" ">
                            <xsl:value-of select="class"/>
                        </xsl:attribute>
                    </result>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var element = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;
        var attribute = element!.Content.Instructions[0] as XsltAttribute;

        // Assert
        attribute.Should().NotBeNull();
        attribute!.Separator.Should().NotBeNull();
        (attribute.Separator!.Parts[0] as AvtLiteral)!.Value.Should().Be(" ");
    }

    #endregion

    #region xsl:variable Tests

    [Fact]
    public void Variable_WithSelect_ParsesSelectExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:variable name="x" select="10"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltVariableInstruction;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Name.LocalName.Should().Be("x");
        instruction.Select.Should().NotBeNull();
    }

    [Fact]
    public void Variable_WithContent_ParsesContentAsBody()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:variable name="content">
                        <root><child/></root>
                    </xsl:variable>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltVariableInstruction;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Content.Should().NotBeNull();
    }

    [Fact]
    public void Variable_WithAsType_ParsesTypeAnnotation()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:variable name="x" as="xs:integer" select="10"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltVariableInstruction;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.As.Should().NotBeNull();
    }

    #endregion

    #region xsl:param Tests

    [Fact]
    public void Param_WithDefault_ParsesDefaultValue()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:param name="depth" select="0"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var param = stylesheet.Templates[0].Parameters[0];

        // Assert
        param.Name.LocalName.Should().Be("depth");
        param.Select.Should().NotBeNull();
    }

    [Fact]
    public void Param_Required_ParsesRequiredAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:param name="id" required="yes"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var param = stylesheet.Templates[0].Parameters[0];

        // Assert
        param.Required.Should().BeTrue();
    }

    [Fact]
    public void Param_Tunnel_ParsesTunnelAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:param name="format" tunnel="yes"/>
                    <result/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var param = stylesheet.Templates[0].Parameters[0];

        // Assert
        param.Tunnel.Should().BeTrue();
    }

    #endregion

    #region xsl:text Tests

    [Fact]
    public void Text_Basic_ParsesTextContent()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:text>Hello, World!</xsl:text>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltText;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Value.Should().Be("Hello, World!");
    }

    [Fact]
    public void Text_WithDisableOutputEscaping_ParsesAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:text disable-output-escaping="yes">&lt;raw&gt;</xsl:text>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltText;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.DisableOutputEscaping.Should().BeTrue();
    }

    #endregion

    #region xsl:copy Tests

    [Fact]
    public void Copy_Basic_ParsesAsIdentityInstruction()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="@*|node()">
                    <xsl:copy>
                        <xsl:apply-templates select="@*|node()"/>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltCopy;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Content.Should().NotBeNull();
    }

    [Fact]
    public void Copy_WithCopyNamespaces_ParsesAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="element">
                    <xsl:copy copy-namespaces="no">
                        <xsl:apply-templates/>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltCopy;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.CopyNamespaces.Should().BeFalse();
    }

    #endregion

    #region xsl:comment Tests

    [Fact]
    public void Comment_Basic_ParsesCommentContent()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:comment>Generated comment</xsl:comment>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltComment;

        // Assert
        instruction.Should().NotBeNull();
    }

    [Fact]
    public void Comment_WithSelect_ParsesSelectExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:comment select="concat('ID: ', @id)"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltComment;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
    }

    #endregion

    #region xsl:processing-instruction Tests

    [Fact]
    public void ProcessingInstruction_Basic_ParsesNameAndContent()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="root">
                    <xsl:processing-instruction name="xml-stylesheet">type="text/xsl" href="style.xsl"</xsl:processing-instruction>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltProcessingInstruction;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Name.Should().NotBeNull();
    }

    #endregion

    #region xsl:message Tests

    [Fact]
    public void Message_Basic_ParsesContent()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:message>Processing item</xsl:message>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltMessage;

        // Assert
        instruction.Should().NotBeNull();
    }

    [Fact]
    public void Message_WithTerminate_ParsesTerminateAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="error">
                    <xsl:message terminate="yes">Fatal error!</xsl:message>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltMessage;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Terminate.Should().BeTrue();
    }

    #endregion

    #region xsl:number Tests

    [Fact]
    public void Number_Basic_ParsesNumberInstruction()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:number/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltNumber;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Level.Should().Be(NumberLevel.Single); // Default
    }

    [Fact]
    public void Number_WithLevel_ParsesLevelAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="section">
                    <xsl:number level="multiple" count="chapter|section" format="1.1"/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltNumber;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Level.Should().Be(NumberLevel.Multiple);
        instruction.Count.Should().NotBeNull();
        instruction.Format.Should().NotBeNull();
    }

    #endregion

    #region xsl:sort Tests

    [Fact]
    public void Sort_WithOptions_ParsesAllSortAttributes()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:for-each select="item">
                        <xsl:sort select="@name" order="descending" case-order="upper-first" data-type="text"/>
                        <xsl:value-of select="."/>
                    </xsl:for-each>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var forEach = stylesheet.Templates[0].Body.Instructions[0] as XsltForEach;

        // Assert
        forEach.Should().NotBeNull();
        forEach!.Sorts.Should().HaveCount(1);
        var sort = forEach.Sorts[0];
        sort.Select.Should().NotBeNull();
        sort.Order.Should().NotBeNull();
        sort.CaseOrder.Should().NotBeNull();
        sort.DataType.Should().NotBeNull();
    }

    #endregion

    #region Literal Result Element Tests

    [Fact]
    public void LiteralResultElement_Basic_ParsesElementAndContent()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <div class="container">
                        <span>Content</span>
                    </div>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Name.LocalName.Should().Be("div");
        instruction.Attributes.Should().HaveCount(1);
    }

    [Fact]
    public void LiteralResultElement_WithAvtAttributes_ParsesAvts()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <div id="{@id}" class="item-{@type}">Content</div>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Attributes.Should().HaveCount(2);
    }

    [Fact]
    public void LiteralResultElement_WithNamespaceDeclaration_ParsesNamespaces()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <html xmlns="http://www.w3.org/1999/xhtml">
                        <body>Content</body>
                    </html>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.NamespaceDeclarations.Should().NotBeEmpty();
    }

    [Fact]
    public void LiteralResultElement_WithUseAttributeSets_ParsesXsltAttribute()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:attribute-set name="common-attrs">
                    <xsl:attribute name="class">default</xsl:attribute>
                </xsl:attribute-set>
                <xsl:template match="item">
                    <div xsl:use-attribute-sets="common-attrs">Content</div>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltLiteralResultElement;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.UseAttributeSets.Should().HaveCount(1);
    }

    #endregion

    #region Sequence Constructor Tests

    [Fact]
    public void SequenceConstructor_WithMixedContent_ParsesAll()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:variable name="x" select="1"/>
                    <div>
                        <xsl:value-of select="@name"/>
                    </div>
                    <xsl:if test="@active">
                        <span>Active</span>
                    </xsl:if>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var body = stylesheet.Templates[0].Body;

        // Assert
        body.Instructions.Should().HaveCount(3);
        body.Instructions[0].Should().BeOfType<XsltVariableInstruction>();
        body.Instructions[1].Should().BeOfType<XsltLiteralResultElement>();
        body.Instructions[2].Should().BeOfType<XsltIf>();
    }

    #endregion

    #region XSLT 3.0 Instructions

    [Fact]
    public void Try_WithCatch_ParsesTryCatchStructure()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:try>
                        <xsl:value-of select="dangerous-function()"/>
                        <xsl:catch>
                            <error>Failed</error>
                        </xsl:catch>
                    </xsl:try>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltTry;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Catches.Should().HaveCount(1);
    }

    [Fact]
    public void Iterate_WithParams_ParsesIterateStructure()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:iterate select="item">
                        <xsl:param name="total" select="0"/>
                        <xsl:next-iteration>
                            <xsl:with-param name="total" select="$total + @value"/>
                        </xsl:next-iteration>
                    </xsl:iterate>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltIterate;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
        instruction.Params.Should().HaveCount(1);
    }

    [Fact]
    public void ForEachGroup_WithGroupBy_ParsesGroupingExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:for-each-group select="item" group-by="@category">
                        <group>
                            <xsl:apply-templates select="current-group()"/>
                        </group>
                    </xsl:for-each-group>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltForEachGroup;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
        instruction.GroupBy.Should().NotBeNull();
    }

    [Fact]
    public void Map_Basic_ParsesMapConstruction()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:map>
                        <xsl:map-entry key="'name'" select="@name"/>
                        <xsl:map-entry key="'value'" select="@value"/>
                    </xsl:map>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltMap;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Content.Should().NotBeNull();
    }

    [Fact]
    public void Array_Basic_ParsesArrayConstruction()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:array>
                        <xsl:array-member select="item/@name"/>
                    </xsl:array>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltArray;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Content.Should().NotBeNull();
    }

    [Fact]
    public void Assert_Basic_ParsesTestExpression()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="item">
                    <xsl:assert test="@id">Item must have an ID</xsl:assert>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltAssert;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Test.Should().NotBeNull();
    }

    [Fact]
    public void AnalyzeString_Basic_ParsesRegexProcessing()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="text">
                    <xsl:analyze-string select="." regex="\d+">
                        <xsl:matching-substring>
                            <number><xsl:value-of select="."/></number>
                        </xsl:matching-substring>
                        <xsl:non-matching-substring>
                            <text><xsl:value-of select="."/></text>
                        </xsl:non-matching-substring>
                    </xsl:analyze-string>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltAnalyzeString;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Select.Should().NotBeNull();
        instruction.Regex.Should().NotBeNull();
        instruction.MatchingSubstring.Should().NotBeNull();
        instruction.NonMatchingSubstring.Should().NotBeNull();
    }

    #endregion
}
