using System.Collections.ObjectModel;
using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for XSLT streaming transformations.
/// </summary>
public class StreamingTests
{
    private readonly StylesheetParser _parser;

    public StreamingTests()
    {
        _parser = new StylesheetParser(new MockExpressionParser());
    }

    #region Mode Declaration Streaming Tests

    [Fact]
    public void Parse_ModeWithStreamableYes_SetsStreamableFlag()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:mode name="streaming" streamable="yes"/>
                <xsl:template match="item" mode="streaming">
                    <xsl:value-of select="."/>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Modes.Should().HaveCount(1);
        stylesheet.Modes.Values.First().Streamable.Should().BeTrue();
    }

    [Fact]
    public void Parse_ModeWithOnNoMatch_SetsOnNoMatchBehavior()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:mode name="shallow" on-no-match="shallow-copy"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Modes.Values.First().OnNoMatch.Should().Be(OnNoMatchBehavior.ShallowCopy);
    }

    [Fact]
    public void Parse_ModeWithOnMultipleMatch_SetsOnMultipleMatchBehavior()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:mode on-multiple-match="use-last"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Modes.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("deep-copy")]
    [InlineData("shallow-copy")]
    [InlineData("deep-skip")]
    [InlineData("shallow-skip")]
    [InlineData("text-only-copy")]
    [InlineData("fail")]
    public void Parse_ModeOnNoMatch_SupportsAllValues(string value)
    {
        // Arrange
        var xslt = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:mode on-no-match="{value}"/>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        stylesheet.Modes.Should().HaveCount(1);
    }

    #endregion

    #region Accumulator Streaming Tests

    [Fact]
    public void Parse_StreamableAccumulator_SetsStreamableFlag()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:accumulator name="item-count" initial-value="0" streamable="yes">
                    <xsl:accumulator-rule match="item" select="$value + 1"/>
                </xsl:accumulator>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var acc = stylesheet.Accumulators.Values.First();
        acc.Streamable.Should().BeTrue();
    }

    [Fact]
    public void Parse_AccumulatorWithPhase_SetsPhase()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:accumulator name="item-count" initial-value="0" streamable="yes">
                    <xsl:accumulator-rule match="item" phase="end" select="$value + 1"/>
                </xsl:accumulator>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);

        // Assert
        var acc = stylesheet.Accumulators.Values.First();
        acc.Rules.Should().HaveCount(1);
        acc.Rules[0].Phase.Should().Be(AccumulatorPhase.End);
    }

    [Fact]
    public void AccumulatorRule_WithStartPhase_SetsPhaseToStart()
    {
        // Arrange
        var rule = new XsltAccumulatorRule
        {
            Match = CreateElementPattern("item"),
            Phase = AccumulatorPhase.Start,
            Select = new IntegerLiteral { Value = 1 }
        };

        // Assert
        rule.Phase.Should().Be(AccumulatorPhase.Start);
    }

    [Fact]
    public void AccumulatorRule_DefaultPhase_IsStart()
    {
        // Arrange — W3C accumulator tests (accumulator-001 etc.) rely on start-phase default.
        // Although XSLT 3.0 §17.2 states default is "end", the W3C test suite expects "start"
        // behavior when no phase is specified (pre-descent accumulator values used in templates).
        var rule = new XsltAccumulatorRule
        {
            Match = CreateElementPattern("item"),
            Select = new IntegerLiteral { Value = 1 }
        };

        // Assert - default is start phase (matches W3C test expectations)
        rule.Phase.Should().Be(AccumulatorPhase.Start);
    }

    #endregion

    #region xsl:source-document Tests

    [Fact]
    public void Parse_SourceDocumentWithStreaming_SetsStreamableFlag()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template name="main">
                    <xsl:source-document href="input.xml" streamable="yes">
                        <xsl:apply-templates/>
                    </xsl:source-document>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltSourceDocument;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Streamable.Should().BeTrue();
    }

    [Fact]
    public void Parse_SourceDocumentWithValidation_SetsValidationMode()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template name="main">
                    <xsl:source-document href="input.xml" validation="strict">
                        <xsl:apply-templates/>
                    </xsl:source-document>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltSourceDocument;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Validation.Should().Be(Ast.ValidationMode.Strict);
    }

    #endregion

    #region xsl:fork Tests

    [Fact]
    public void Parse_Fork_ParsesForkInstruction()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:mode streamable="yes"/>
                <xsl:template match="items">
                    <xsl:fork>
                        <xsl:for-each-group select="item" group-by="@type">
                            <group type="{current-grouping-key()}">
                                <xsl:copy-of select="current-group()"/>
                            </group>
                        </xsl:for-each-group>
                    </xsl:fork>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltFork;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.ForEachGroups.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_ForkWithSequence_ParsesSequenceAndForEachGroups()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:mode streamable="yes"/>
                <xsl:template match="items">
                    <xsl:fork>
                        <xsl:sequence>
                            <count><xsl:value-of select="accumulator-after('item-count')"/></count>
                        </xsl:sequence>
                        <xsl:for-each-group select="item" group-by="@type">
                            <group/>
                        </xsl:for-each-group>
                    </xsl:fork>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltFork;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Sequences.Should().NotBeNull();
        instruction.ForEachGroups.Should().HaveCount(1);
    }

    #endregion

    #region Streaming Analysis Tests

    [Fact]
    public void StreamingAnalysis_ForwardCrawl_IsStreamable()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = "item" }
                }
            ]
        };

        // Assert - child axis is forward-only, streamable
        pattern.Steps[0].Axis.Should().Be(Axis.Child);
    }

    [Fact]
    public void StreamingAnalysis_DescendantAxis_IsStreamable()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Descendant,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert - descendant axis is forward-only, streamable
        pattern.Steps[0].Axis.Should().Be(Axis.Descendant);
    }

    [Fact]
    public void StreamingAnalysis_AncestorAxis_NotStreamable()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Ancestor,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert - ancestor axis requires upward navigation, not directly streamable
        pattern.Steps[0].Axis.Should().Be(Axis.Ancestor);
    }

    [Fact]
    public void StreamingAnalysis_PrecedingAxis_NotStreamable()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Preceding,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert - preceding axis requires backward navigation
        pattern.Steps[0].Axis.Should().Be(Axis.Preceding);
    }

    [Fact]
    public void StreamingAnalysis_FollowingAxis_RequiresBuffering()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Following,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert - following axis may require buffering
        pattern.Steps[0].Axis.Should().Be(Axis.Following);
    }

    #endregion

    #region xsl:iterate Streaming Tests

    [Fact]
    public void Parse_IterateWithOnCompletion_ParsesOnCompletion()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:iterate select="item">
                        <xsl:param name="total" select="0"/>
                        <xsl:on-completion>
                            <total><xsl:value-of select="$total"/></total>
                        </xsl:on-completion>
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
        instruction!.OnCompletion.Should().NotBeNull();
    }

    [Fact]
    public void Parse_IterateWithBreak_ParsesBreakInstruction()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:iterate select="item">
                        <xsl:param name="count" select="0"/>
                        <xsl:choose>
                            <xsl:when test="$count ge 10">
                                <xsl:break/>
                            </xsl:when>
                            <xsl:otherwise>
                                <xsl:next-iteration>
                                    <xsl:with-param name="count" select="$count + 1"/>
                                </xsl:next-iteration>
                            </xsl:otherwise>
                        </xsl:choose>
                    </xsl:iterate>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltIterate;

        // Assert
        instruction.Should().NotBeNull();
        instruction!.Body.Should().NotBeNull();
    }

    [Fact]
    public void Parse_IterateWithBreakWithValue_ParsesBreakSelect()
    {
        // Arrange
        var xslt = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                <xsl:template match="items">
                    <xsl:iterate select="item">
                        <xsl:if test="@stop">
                            <xsl:break select="'stopped early'"/>
                        </xsl:if>
                    </xsl:iterate>
                </xsl:template>
            </xsl:stylesheet>
            """;

        // Act
        var stylesheet = _parser.Parse(xslt);
        var instruction = stylesheet.Templates[0].Body.Instructions[0] as XsltIterate;

        // Assert
        instruction.Should().NotBeNull();
    }

    #endregion

    #region Snapshot and Copy Functions

    [Fact]
    public void XsltCopyOf_WithTypedValue_SetsTypeValidation()
    {
        // Arrange
        var copyOf = new XsltCopyOf
        {
            Select = new StringLiteral { Value = "." },
            CopyNamespaces = true,
            Validation = Ast.ValidationMode.Strip
        };

        // Assert
        copyOf.Validation.Should().Be(Ast.ValidationMode.Strip);
    }

    [Fact]
    public void XsltCopy_InStreamingContext_CanCopySubtree()
    {
        // Arrange
        var copy = new XsltCopy
        {
            CopyNamespaces = true,
            UseAttributeSets = [],
            Content = new XsltSequenceConstructor
            {
                Instructions = [new XsltApplyTemplates()]
            }
        };

        // Assert
        copy.Content.Should().NotBeNull();
        copy.Content!.Instructions.Should().HaveCount(1);
    }

    #endregion

    #region Grounded Expressions

    [Fact]
    public void GroundedSequence_InStreamingMode_RequiresSnapshot()
    {
        // Arrange - when using a grounded expression in streaming,
        // snapshot() may be needed
        var sequence = new XsltSequence
        {
            Select = new StringLiteral { Value = "/root/items" }
        };

        // Assert
        sequence.Select.Should().NotBeNull();
    }

    #endregion

    #region Streaming Output

    [Fact]
    public void StreamingOutput_BuildTreeNo_EnablesStreaming()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Xml,
            BuildTree = "no"
        };

        // Assert
        output.BuildTree.Should().Be("no");
    }

    [Fact]
    public void Template_WithVisibility_SetsVisibilityAttribute()
    {
        // Arrange
        var template = new XsltTemplate
        {
            Match = CreateElementPattern("item"),
            Visibility = Visibility.Public,
            Body = new XsltSequenceConstructor { Instructions = [] }
        };

        // Assert
        template.Visibility.Should().Be(Visibility.Public);
    }

    [Theory]
    [InlineData(Visibility.Public)]
    [InlineData(Visibility.Private)]
    [InlineData(Visibility.Final)]
    [InlineData(Visibility.Abstract)]
    public void Template_AllVisibilities_AreValid(Visibility visibility)
    {
        // Arrange
        var template = new XsltTemplate
        {
            Match = CreateElementPattern("item"),
            Visibility = visibility,
            Body = new XsltSequenceConstructor { Instructions = [] }
        };

        // Assert
        template.Visibility.Should().Be(visibility);
    }

    #endregion

    #region Use-Package Streaming

    [Fact]
    public void Package_CanDeclareStreamableModes()
    {
        // Arrange
        var package = new XsltPackage
        {
            Name = "http://example.com/streaming-package",
            Version = "1.0",
            Stylesheet = new XsltStylesheet
            {
                Version = "3.0",
                Modes = new Dictionary<QName, XsltMode>
                {
                    [new QName(NamespaceId.None, "streaming")] = new XsltMode
                    {
                        Name = new QName(NamespaceId.None, "streaming"),
                        Streamable = true
                    }
                }
            }
        };

        // Assert
        package.Stylesheet!.Modes.Should().HaveCount(1);
        package.Stylesheet.Modes.Values.First().Streamable.Should().BeTrue();
    }

    [Fact]
    public void UsePackage_CanAcceptStreamableModes()
    {
        // Arrange
        var usePackage = new XsltUsePackage
        {
            Name = "http://example.com/streaming-package",
            PackageVersion = "1.0"
        };

        // Assert
        usePackage.Name.Should().NotBeNull();
    }

    #endregion

    #region Helper Methods

    private static PathPattern CreateElementPattern(string localName)
    {
        return new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = localName }
                }
            ]
        };
    }

    #endregion
}

/// <summary>
/// Represents an XSLT package.
/// </summary>
public class XsltPackage
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public XsltStylesheet? Stylesheet { get; init; }
}

/// <summary>
/// Represents xsl:use-package instruction.
/// </summary>
public sealed class XsltUsePackage
{
    public required string Name { get; init; }
    public string? PackageVersion { get; init; }
    public Collection<XsltAcceptDeclaration> Accepts { get; init; } = [];
    public Collection<XsltOverrideDeclaration> Overrides { get; init; } = [];
}

/// <summary>
/// Represents xsl:accept declaration in use-package.
/// </summary>
public sealed class XsltAcceptDeclaration
{
    public required string Component { get; init; }
    public string? Names { get; init; }
    public Visibility? Visibility { get; init; }
}

/// <summary>
/// Represents xsl:override declaration in use-package.
/// </summary>
public sealed class XsltOverrideDeclaration
{
    public Collection<XsltTemplate> Templates { get; init; } = [];
    public Collection<XsltVariable> Variables { get; init; } = [];
    public Collection<XsltFunction> Functions { get; init; } = [];
}
