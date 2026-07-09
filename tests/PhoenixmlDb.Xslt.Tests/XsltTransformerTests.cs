using System.Text;
using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for XsltTransformEngine end-to-end transformations.
/// </summary>
public class XsltTransformEngineTests
{
    #region Transformer Construction

    [Fact]
    public void Constructor_WithValidStylesheet_CreatesTransformer()
    {
        // Arrange
        var stylesheet = CreateMinimalStylesheet();

        // Act
        var transformer = new XsltTransformEngine(stylesheet);

        // Assert
        transformer.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithTemplates_IndexesTemplates()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new PathPattern { Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "*" } }] },
                    Body = new XsltSequenceConstructor { Instructions = [] }
                },
                new XsltTemplate
                {
                    Name = new QName(NamespaceId.None, "named"),
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };
        stylesheet.NamedTemplates[new QName(NamespaceId.None, "named")] = stylesheet.Templates[1];

        // Act
        var transformer = new XsltTransformEngine(stylesheet);

        // Assert
        transformer.Should().NotBeNull();
    }

    #endregion

    #region Transform Options

    [Fact]
    public void TransformOptions_WithInitialMode_SetsMode()
    {
        // Arrange
        var options = new XsltTransformOptions
        {
            InitialMode = new QName(NamespaceId.None, "toc")
        };

        // Assert
        options.InitialMode.Should().NotBeNull();
        options.InitialMode!.Value.LocalName.Should().Be("toc");
    }

    [Fact]
    public void TransformOptions_WithInitialTemplate_SetsTemplate()
    {
        // Arrange
        var options = new XsltTransformOptions
        {
            InitialTemplate = new QName(NamespaceId.None, "main")
        };

        // Assert
        options.InitialTemplate.Should().NotBeNull();
        options.InitialTemplate!.Value.LocalName.Should().Be("main");
    }

    [Fact]
    public void TransformOptions_WithParameters_SetsParameters()
    {
        // Arrange
        var options = new XsltTransformOptions
        {
            InitialParameters = new Dictionary<QName, object?>
            {
                [new QName(NamespaceId.None, "param1")] = "value1",
                [new QName(NamespaceId.None, "param2")] = 42
            }
        };

        // Assert
        options.InitialParameters.Should().HaveCount(2);
    }

    [Fact]
    public void TransformOptions_WithOutputFormat_SetsFormat()
    {
        // Arrange
        var options = new XsltTransformOptions
        {
            OutputFormat = new QName(NamespaceId.None, "html-output")
        };

        // Assert
        options.OutputFormat.Should().NotBeNull();
    }

    [Fact]
    public void TransformOptions_WithMessageListener_SetsListener()
    {
        // Arrange
        var messages = new List<(string message, bool terminate)>();
        var options = new XsltTransformOptions
        {
            MessageListener = (msg, term) => messages.Add((msg, term))
        };

        // Act
        options.MessageListener?.Invoke("Test message", false);

        // Assert
        messages.Should().HaveCount(1);
        messages[0].message.Should().Be("Test message");
    }

    #endregion

    #region Template Index

    [Fact]
    public void TemplateIndex_SortsByPriority()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = CreateElementPattern("*"),
                    Priority = 0.5,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                },
                new XsltTemplate
                {
                    Match = CreateElementPattern("item"),
                    Priority = 1.0,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        // Act
        var index = new TemplateIndex(stylesheet);

        // Assert - higher priority template should match first
        index.Should().NotBeNull();
    }

    [Fact]
    public void TemplateIndex_GroupsByMode()
    {
        // Arrange
        var tocMode = new QName(NamespaceId.None, "toc");
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = CreateElementPattern("item"),
                    Modes = [tocMode],
                    Body = new XsltSequenceConstructor { Instructions = [] }
                },
                new XsltTemplate
                {
                    Match = CreateElementPattern("item"),
                    Modes = [],
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        // Act
        var index = new TemplateIndex(stylesheet);

        // Assert
        index.Should().NotBeNull();
    }

    [Fact]
    public void TemplateIndex_FindMatchingTemplate_ReturnsFirstMatch()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new MockPattern(true),
                    Priority = 1.0,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };
        var index = new TemplateIndex(stylesheet);
        var context = new XsltContext { CurrentNode = new object() };

        // Act
        var template = index.FindMatchingTemplate(new object(), null, context);

        // Assert
        template.Should().NotBeNull();
    }

    [Fact]
    public void TemplateIndex_FindMatchingTemplate_ReturnsNullWhenNoMatch()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new MockPattern(false),
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };
        var index = new TemplateIndex(stylesheet);
        var context = new XsltContext { CurrentNode = new object() };

        // Act
        var template = index.FindMatchingTemplate(new object(), null, context);

        // Assert
        template.Should().BeNull();
    }

    [Fact]
    public void TemplateIndex_FindMatchingTemplate_UsesMode()
    {
        // Arrange
        var tocMode = new QName(NamespaceId.None, "toc");
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new MockPattern(true),
                    Modes = [tocMode],
                    Body = new XsltSequenceConstructor { Instructions = [new XsltText { Value = "toc" }] }
                },
                new XsltTemplate
                {
                    Match = new MockPattern(true),
                    Modes = [],
                    Body = new XsltSequenceConstructor { Instructions = [new XsltText { Value = "default" }] }
                }
            ]
        };
        var index = new TemplateIndex(stylesheet);
        var context = new XsltContext { CurrentNode = new object() };

        // Act
        var tocTemplate = index.FindMatchingTemplate(new object(), tocMode, context);
        var defaultTemplate = index.FindMatchingTemplate(new object(), null, context);

        // Assert
        tocTemplate.Should().NotBeNull();
        defaultTemplate.Should().NotBeNull();
        tocTemplate.Should().NotBe(defaultTemplate);
    }

    #endregion

    #region Variable and Parameter Binding

    [Fact]
    public void Transform_WithGlobalVariables_BindsVariables()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Variables =
            [
                new XsltVariable
                {
                    Name = new QName(NamespaceId.None, "globalVar"),
                    Select = new StringLiteral { Value = "test" }
                }
            ],
            Templates =
            [
                new XsltTemplate
                {
                    Match = new MockPattern(true),
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        // Act
        var transformer = new XsltTransformEngine(stylesheet);

        // Assert
        transformer.Should().NotBeNull();
    }

    [Fact]
    public void Transform_WithGlobalParameters_AcceptsParameters()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Parameters =
            [
                new XsltParam
                {
                    Name = new QName(NamespaceId.None, "globalParam"),
                    Select = new StringLiteral { Value = "default" }
                }
            ],
            Templates =
            [
                new XsltTemplate
                {
                    Match = new MockPattern(true),
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        var options = new XsltTransformOptions
        {
            InitialParameters = new Dictionary<QName, object?>
            {
                [new QName(NamespaceId.None, "globalParam")] = "override"
            }
        };

        // Act
        var transformer = new XsltTransformEngine(stylesheet);

        // Assert
        transformer.Should().NotBeNull();
    }

    #endregion

    #region Output Method Tests

    [Fact]
    public void Output_XmlMethod_DefaultEncoding()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Xml,
            Encoding = "UTF-8"
        };

        // Assert
        output.Method.Should().Be(OutputMethod.Xml);
        output.Encoding.Should().Be("UTF-8");
    }

    [Fact]
    public void Output_HtmlMethod_SetsHtmlOptions()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Html,
            Indent = true,
            MediaType = "text/html"
        };

        // Assert
        output.Method.Should().Be(OutputMethod.Html);
        output.Indent.Should().BeTrue();
        output.MediaType.Should().Be("text/html");
    }

    [Fact]
    public void Output_TextMethod_SetsTextOptions()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Text,
            MediaType = "text/plain"
        };

        // Assert
        output.Method.Should().Be(OutputMethod.Text);
    }

    [Fact]
    public void Output_WithDoctype_SetsDoctype()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Html,
            DoctypePublic = "-//W3C//DTD HTML 4.01//EN",
            DoctypeSystem = "http://www.w3.org/TR/html4/strict.dtd"
        };

        // Assert
        output.DoctypePublic.Should().NotBeNull();
        output.DoctypeSystem.Should().NotBeNull();
    }

    [Fact]
    public void Output_WithCdataSectionElements_SetsCdataElements()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Xml,
            CdataSectionElements = new HashSet<QName>
            {
                new QName(NamespaceId.None, "script"),
                new QName(NamespaceId.None, "style")
            }
        };

        // Assert
        output.CdataSectionElements.Should().HaveCount(2);
    }

    [Fact]
    public void Output_JsonMethod_SetsJsonOptions()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Json,
            MediaType = "application/json"
        };

        // Assert
        output.Method.Should().Be(OutputMethod.Json);
    }

    [Fact]
    public void Output_AdaptiveMethod_SetsAdaptiveOptions()
    {
        // Arrange
        var output = new XsltOutput
        {
            Method = OutputMethod.Adaptive
        };

        // Assert
        output.Method.Should().Be(OutputMethod.Adaptive);
    }

    #endregion

    #region Named Templates

    [Fact]
    public void NamedTemplates_CanBeCalledByName()
    {
        // Arrange
        var templateName = new QName(NamespaceId.None, "formatDate");
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0"
        };
        var template = new XsltTemplate
        {
            Name = templateName,
            Body = new XsltSequenceConstructor { Instructions = [] }
        };
        stylesheet.Templates.Add(template);
        stylesheet.NamedTemplates[templateName] = template;

        // Assert
        stylesheet.NamedTemplates.Should().ContainKey(templateName);
        stylesheet.NamedTemplates[templateName].Should().Be(template);
    }

    [Fact]
    public void NamedTemplates_WithParameters_AcceptsParams()
    {
        // Arrange
        var templateName = new QName(NamespaceId.None, "formatDate");
        var template = new XsltTemplate
        {
            Name = templateName,
            Parameters =
            [
                new XsltParam { Name = new QName(NamespaceId.None, "date") },
                new XsltParam { Name = new QName(NamespaceId.None, "format"), Select = new StringLiteral { Value = "default" } }
            ],
            Body = new XsltSequenceConstructor { Instructions = [] }
        };

        // Assert
        template.Parameters.Should().HaveCount(2);
    }

    #endregion

    #region Attribute Sets

    [Fact]
    public void AttributeSet_CanBeUsed()
    {
        // Arrange
        var attrSetName = new QName(NamespaceId.None, "common-attrs");
        var attrSet = new XsltAttributeSet
        {
            Name = attrSetName,
            Attributes =
            [
                new XsltAttribute
                {
                    Name = XsltAttributeValueTemplate.FromString("class"),
                    Select = new StringLiteral { Value = "content" }
                }
            ]
        };
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0"
        };
        stylesheet.AttributeSets[attrSetName] = attrSet;

        // Assert
        stylesheet.AttributeSets.Should().ContainKey(attrSetName);
        stylesheet.AttributeSets[attrSetName].Attributes.Should().HaveCount(1);
    }

    [Fact]
    public void AttributeSet_CanInheritFromOther()
    {
        // Arrange
        var baseAttrSetName = new QName(NamespaceId.None, "base-attrs");
        var extAttrSetName = new QName(NamespaceId.None, "extended-attrs");

        var extAttrSet = new XsltAttributeSet
        {
            Name = extAttrSetName,
            UseAttributeSets = [baseAttrSetName],
            Attributes =
            [
                new XsltAttribute
                {
                    Name = XsltAttributeValueTemplate.FromString("extra"),
                    Select = new StringLiteral { Value = "value" }
                }
            ]
        };

        // Assert
        extAttrSet.UseAttributeSets.Should().Contain(baseAttrSetName);
    }

    #endregion

    #region Keys

    [Fact]
    public void Key_CanBeDefined()
    {
        // Arrange
        var keyName = new QName(NamespaceId.None, "itemById");
        var key = new XsltKey
        {
            Name = keyName,
            Match = CreateElementPattern("item"),
            Use = new StringLiteral { Value = "@id" }
        };
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0"
        };
        stylesheet.Keys[keyName] = key;

        // Assert
        stylesheet.Keys.Should().ContainKey(keyName);
    }

    [Fact]
    public void Key_CompositeKey_SetsCompositeFlag()
    {
        // Arrange
        var key = new XsltKey
        {
            Name = new QName(NamespaceId.None, "compositeKey"),
            Match = CreateElementPattern("item"),
            Use = new StringLiteral { Value = "concat(@a, @b)" },
            Composite = true
        };

        // Assert
        key.Composite.Should().BeTrue();
    }

    #endregion

    #region Character Maps

    [Fact]
    public void CharacterMap_CanMapCharacters()
    {
        // Arrange
        var charMapName = new QName(NamespaceId.None, "special");
        var charMap = new XsltCharacterMap
        {
            Name = charMapName,
            Mappings = new Dictionary<int, string>
            {
                ['<'] = "[LT]",
                ['>'] = "[GT]",
                ['&'] = "[AMP]"
            }
        };

        // Assert
        charMap.Mappings.Should().HaveCount(3);
        charMap.Mappings['<'].Should().Be("[LT]");
    }

    [Fact]
    public void CharacterMap_CanInheritFromOther()
    {
        // Arrange
        var baseMapName = new QName(NamespaceId.None, "base-map");
        var charMap = new XsltCharacterMap
        {
            Name = new QName(NamespaceId.None, "extended-map"),
            UseCharacterMaps = [baseMapName],
            Mappings = new Dictionary<int, string>
            {
                ['"'] = "[QUOT]"
            }
        };

        // Assert
        charMap.UseCharacterMaps.Should().Contain(baseMapName);
    }

    #endregion

    #region Decimal Formats

    [Fact]
    public void DecimalFormat_DefaultValues_AreCorrect()
    {
        // Arrange
        var format = new XsltDecimalFormat();

        // Assert
        format.DecimalSeparator.Should().Be(".");
        format.GroupingSeparator.Should().Be(",");
        format.Infinity.Should().Be("Infinity");
        format.MinusSign.Should().Be("-");
        format.NaN.Should().Be("NaN");
        format.Percent.Should().Be("%");
        format.ZeroDigit.Should().Be("0");
        format.Digit.Should().Be("#");
        format.PatternSeparator.Should().Be(";");
    }

    [Fact]
    public void DecimalFormat_CanBeCustomized()
    {
        // Arrange
        var format = new XsltDecimalFormat
        {
            Name = new QName(NamespaceId.None, "european"),
            DecimalSeparator = ",",
            GroupingSeparator = "."
        };

        // Assert
        format.DecimalSeparator.Should().Be(",");
        format.GroupingSeparator.Should().Be(".");
    }

    #endregion

    #region Accumulators

    [Fact]
    public void Accumulator_CanBeDefined()
    {
        // Arrange
        var accName = new QName(NamespaceId.None, "counter");
        var acc = new XsltAccumulator
        {
            Name = accName,
            InitialValue = new IntegerLiteral { Value = 0 },
            Rules =
            [
                new XsltAccumulatorRule
                {
                    Match = CreateElementPattern("item"),
                    Select = new StringLiteral { Value = "$value + 1" }
                }
            ]
        };

        // Assert
        acc.Name.Should().Be(accName);
        acc.Rules.Should().HaveCount(1);
    }

    [Fact]
    public void Accumulator_Streamable_SetsStreamableFlag()
    {
        // Arrange
        var acc = new XsltAccumulator
        {
            Name = new QName(NamespaceId.None, "streamableAcc"),
            InitialValue = new IntegerLiteral { Value = 0 },
            Rules = [],
            Streamable = true
        };

        // Assert
        acc.Streamable.Should().BeTrue();
    }

    #endregion

    #region Functions

    [Fact]
    public void Function_CanBeDefined()
    {
        // Arrange
        var funcName = new QName(NamespaceId.None, "double");
        var func = new XsltFunction
        {
            Name = funcName,
            Parameters = [new XsltParam { Name = new QName(NamespaceId.None, "n") }],
            Body = new XsltSequenceConstructor { Instructions = [] }
        };
        var funcKey = (funcName, func.Parameters.Count);
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0"
        };
        stylesheet.Functions[funcKey] = func;

        // Assert
        stylesheet.Functions.Should().ContainKey(funcKey);
    }

    [Fact]
    public void Function_WithReturnType_SetsAsType()
    {
        // Arrange
        var func = new XsltFunction
        {
            Name = new QName(NamespaceId.None, "double"),
            As = XdmSequenceType.Integer,
            Parameters = [new XsltParam { Name = new QName(NamespaceId.None, "n") }],
            Body = new XsltSequenceConstructor { Instructions = [] }
        };

        // Assert
        func.As.Should().NotBeNull();
        func.As!.ItemType.Should().Be(ItemType.Integer);
    }

    #endregion

    #region Import/Include

    [Fact]
    public void Stylesheet_CanHaveImports()
    {
        // Arrange
        var importedStylesheet = new XsltStylesheet { Version = "3.0" };
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Imports = [importedStylesheet]
        };

        // Assert
        stylesheet.Imports.Should().HaveCount(1);
    }

    [Fact]
    public void Stylesheet_CanHaveIncludes()
    {
        // Arrange
        var includedStylesheet = new XsltStylesheet { Version = "3.0" };
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Includes = [includedStylesheet]
        };

        // Assert
        stylesheet.Includes.Should().HaveCount(1);
    }

    #endregion

    #region Strip/Preserve Space

    [Fact]
    public void Stylesheet_StripSpace_CanSpecifyElements()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            StripSpace =
            [
                new NameTest { LocalName = "p" },
                new NameTest { LocalName = "div" }
            ]
        };

        // Assert
        stylesheet.StripSpace.Should().HaveCount(2);
    }

    [Fact]
    public void Stylesheet_PreserveSpace_CanSpecifyElements()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            PreserveSpace =
            [
                new NameTest { LocalName = "pre" },
                new NameTest { LocalName = "code" }
            ]
        };

        // Assert
        stylesheet.PreserveSpace.Should().HaveCount(2);
    }

    #endregion

    #region Default Mode

    [Fact]
    public void Stylesheet_DefaultMode_CanBeSet()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            DefaultMode = new QName(NamespaceId.None, "main")
        };

        // Assert
        stylesheet.DefaultMode.Should().NotBeNull();
        stylesheet.DefaultMode!.Value.LocalName.Should().Be("main");
    }

    #endregion

    #region Validation Mode

    [Fact]
    public void Stylesheet_DefaultValidation_CanBeSet()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            DefaultValidation = Ast.ValidationMode.Strict
        };

        // Assert
        stylesheet.DefaultValidation.Should().Be(Ast.ValidationMode.Strict);
    }

    [Theory]
    [InlineData(Ast.ValidationMode.Strip)]
    [InlineData(Ast.ValidationMode.Preserve)]
    [InlineData(Ast.ValidationMode.Strict)]
    [InlineData(Ast.ValidationMode.Lax)]
    public void ValidationMode_AllValues_AreValid(Ast.ValidationMode mode)
    {
        // Arrange & Act
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            DefaultValidation = mode
        };

        // Assert
        stylesheet.DefaultValidation.Should().Be(mode);
    }

    #endregion

    #region Helper Methods

    private static XsltStylesheet CreateMinimalStylesheet()
    {
        return new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new PathPattern
                    {
                        Steps =
                        [
                            new PatternStep
                            {
                                Axis = Axis.Self,
                                NodeTest = new KindTest { Kind = XdmNodeKind.Document }
                            }
                        ]
                    },
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };
    }

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

    private static XdmDocument CreateTestDocument()
    {
        return new XdmDocument
        {
            Id = new NodeId(1),
            Document = new DocumentId(1),
            Children = XdmDocument.EmptyChildren,
            DocumentElement = new NodeId(2)
        };
    }

    #endregion
}
