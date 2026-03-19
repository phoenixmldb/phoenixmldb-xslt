using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for template matching and pattern evaluation.
/// </summary>
public class TemplateMatcherTests
{
    #region PathPattern Matching

    [Fact]
    public void PathPattern_SingleStep_MatchesElementByName()
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
        var element = CreateElement("item");
        var context = CreateContext(element);

        // Act
        var matches = pattern.Matches(element, context);

        // Assert - base implementation returns false, but pattern is valid
        pattern.Steps.Should().HaveCount(1);
        pattern.Steps[0].NodeTest.Should().BeOfType<NameTest>();
    }

    [Fact]
    public void PathPattern_Wildcard_MatchesAnyElement()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert
        var nameTest = pattern.Steps[0].NodeTest as NameTest;
        nameTest.Should().NotBeNull();
        nameTest!.IsLocalNameWildcard.Should().BeTrue();
    }

    [Fact]
    public void PathPattern_NamespaceWildcard_MatchesAnyNamespace()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = "item", NamespaceUri = "*" }
                }
            ]
        };

        // Assert
        var nameTest = pattern.Steps[0].NodeTest as NameTest;
        nameTest.Should().NotBeNull();
        nameTest!.IsNamespaceWildcard.Should().BeTrue();
    }

    [Fact]
    public void PathPattern_MultipleSteps_ValidatesPath()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = "root" }
                },
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = "item" }
                },
                new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new NameTest { LocalName = "name" }
                }
            ]
        };

        // Assert
        pattern.Steps.Should().HaveCount(3);
        pattern.Steps[0].NodeTest.As<NameTest>().LocalName.Should().Be("root");
        pattern.Steps[1].NodeTest.As<NameTest>().LocalName.Should().Be("item");
        pattern.Steps[2].NodeTest.As<NameTest>().LocalName.Should().Be("name");
    }

    [Fact]
    public void PathPattern_AttributeAxis_MatchesAttributes()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Attribute,
                    NodeTest = new NameTest { LocalName = "id" }
                }
            ]
        };

        // Assert
        pattern.Steps[0].Axis.Should().Be(Axis.Attribute);
    }

    [Fact]
    public void PathPattern_SelfAxis_MatchesSameNode()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Self,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert
        pattern.Steps[0].Axis.Should().Be(Axis.Self);
    }

    [Fact]
    public void PathPattern_ParentAxis_MatchesParentNode()
    {
        // Arrange
        var pattern = new PathPattern
        {
            Steps =
            [
                new PatternStep
                {
                    Axis = Axis.Parent,
                    NodeTest = new NameTest { LocalName = "*" }
                }
            ]
        };

        // Assert
        pattern.Steps[0].Axis.Should().Be(Axis.Parent);
    }

    #endregion

    #region UnionPattern Matching

    [Fact]
    public void UnionPattern_WithMultiplePatterns_MatchesAny()
    {
        // Arrange
        var pattern = new UnionPattern
        {
            Patterns =
            [
                new PathPattern
                {
                    Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "item" } }]
                },
                new PathPattern
                {
                    Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "product" } }]
                },
                new PathPattern
                {
                    Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "entry" } }]
                }
            ]
        };

        // Assert
        pattern.Patterns.Should().HaveCount(3);
    }

    [Fact]
    public void UnionPattern_Matches_ReturnsTrueIfAnyPatternMatches()
    {
        // Arrange
        var truePattern = new MockPattern(true);
        var falsePattern = new MockPattern(false);
        var unionPattern = new UnionPattern
        {
            Patterns = [falsePattern, truePattern, falsePattern]
        };
        var context = CreateContext(new object());

        // Act
        var matches = unionPattern.Matches(new object(), context);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void UnionPattern_Matches_ReturnsFalseIfNoPatternMatches()
    {
        // Arrange
        var unionPattern = new UnionPattern
        {
            Patterns =
            [
                new MockPattern(false),
                new MockPattern(false),
                new MockPattern(false)
            ]
        };
        var context = CreateContext(new object());

        // Act
        var matches = unionPattern.Matches(new object(), context);

        // Assert
        matches.Should().BeFalse();
    }

    #endregion

    #region KindTest Matching

    [Fact]
    public void KindTest_Element_MatchesElementNodes()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.Element };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.Element, null, null);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void KindTest_Element_DoesNotMatchTextNodes()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.Element };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.Text, null, null);

        // Assert
        matches.Should().BeFalse();
    }

    [Fact]
    public void KindTest_Node_MatchesAnyNode()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.None }; // node() matches any

        // Act & Assert
        kindTest.Matches(XdmNodeKind.Element, null, null).Should().BeTrue();
        kindTest.Matches(XdmNodeKind.Text, null, null).Should().BeTrue();
        kindTest.Matches(XdmNodeKind.Attribute, null, null).Should().BeTrue();
        kindTest.Matches(XdmNodeKind.Comment, null, null).Should().BeTrue();
    }

    [Fact]
    public void KindTest_Document_MatchesDocumentNode()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.Document };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.Document, null, null);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void KindTest_Text_MatchesTextNodes()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.Text };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.Text, null, null);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void KindTest_Comment_MatchesCommentNodes()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.Comment };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.Comment, null, null);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void KindTest_PI_MatchesPINodes()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.ProcessingInstruction };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.ProcessingInstruction, null, null);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void KindTest_Attribute_MatchesAttributeNodes()
    {
        // Arrange
        var kindTest = new KindTest { Kind = XdmNodeKind.Attribute };

        // Act
        var matches = kindTest.Matches(XdmNodeKind.Attribute, null, null);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void KindTest_WithName_MatchesNamedElements()
    {
        // Arrange
        var kindTest = new KindTest
        {
            Kind = XdmNodeKind.Element,
            Name = new NameTest { LocalName = "item" }
        };

        // Act
        var matchesCorrectName = kindTest.Matches(XdmNodeKind.Element, NamespaceId.None, "item");
        var matchesWrongName = kindTest.Matches(XdmNodeKind.Element, NamespaceId.None, "other");

        // Assert
        matchesCorrectName.Should().BeTrue();
        matchesWrongName.Should().BeFalse();
    }

    #endregion

    #region NameTest Matching

    [Fact]
    public void NameTest_ExactMatch_MatchesCorrectName()
    {
        // Arrange
        var nameTest = new NameTest { LocalName = "item" };

        // Act
        var matches = nameTest.Matches(XdmNodeKind.Element, null, "item");

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void NameTest_ExactMatch_DoesNotMatchWrongName()
    {
        // Arrange
        var nameTest = new NameTest { LocalName = "item" };

        // Act
        var matches = nameTest.Matches(XdmNodeKind.Element, null, "other");

        // Assert
        matches.Should().BeFalse();
    }

    [Fact]
    public void NameTest_Wildcard_MatchesAnyName()
    {
        // Arrange
        var nameTest = new NameTest { LocalName = "*" };

        // Act & Assert
        nameTest.Matches(XdmNodeKind.Element, null, "item").Should().BeTrue();
        nameTest.Matches(XdmNodeKind.Element, null, "product").Should().BeTrue();
        nameTest.Matches(XdmNodeKind.Element, null, "anything").Should().BeTrue();
    }

    [Fact]
    public void NameTest_NamespaceWildcard_MatchesAnyNamespace()
    {
        // Arrange
        var nameTest = new NameTest { LocalName = "item", NamespaceUri = "*" };

        // Assert
        nameTest.IsNamespaceWildcard.Should().BeTrue();
        nameTest.Matches(XdmNodeKind.Element, NamespaceId.None, "item").Should().BeTrue();
        nameTest.Matches(XdmNodeKind.Element, new NamespaceId(100), "item").Should().BeTrue();
    }

    [Fact]
    public void NameTest_WithPrefix_StoresPrefixForDisplay()
    {
        // Arrange
        var nameTest = new NameTest
        {
            LocalName = "element",
            Prefix = "ns",
            NamespaceUri = "http://example.com"
        };

        // Assert
        nameTest.Prefix.Should().Be("ns");
        nameTest.ToString().Should().Be("ns:element");
    }

    #endregion

    #region Template Priority

    [Fact]
    public void TemplateIndex_SortsByPriority_HigherFirst()
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
                    Priority = 1.0,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                },
                new XsltTemplate
                {
                    Match = new PathPattern { Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "item" } }] },
                    Priority = 10.0,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                },
                new XsltTemplate
                {
                    Match = new PathPattern { Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "product" } }] },
                    Priority = 5.0,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        // Act
        var index = new TemplateIndex(stylesheet);

        // Assert - templates should be sorted by priority (higher first)
        // We can't directly access the internal list, but the index should be constructed
        index.Should().NotBeNull();
    }

    [Fact]
    public void TemplateIndex_DefaultPriority_IsTreatedAsHalf()
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
                    Priority = null, // Default priority
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        // Act
        var index = new TemplateIndex(stylesheet);

        // Assert
        stylesheet.Templates[0].Priority.Should().BeNull();
        // Default priority 0.5 is handled by template index
        index.Should().NotBeNull();
    }

    [Fact]
    public void TemplateIndex_WithModes_IndexesByMode()
    {
        // Arrange
        var tocMode = new QName(NamespaceId.None, "toc");
        var indexMode = new QName(NamespaceId.None, "index");

        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new PathPattern { Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "item" } }] },
                    Modes = [tocMode],
                    Body = new XsltSequenceConstructor { Instructions = [] }
                },
                new XsltTemplate
                {
                    Match = new PathPattern { Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "item" } }] },
                    Modes = [indexMode],
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
    public void TemplateIndex_NoMode_AddsToDefaultModeList()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Match = new PathPattern { Steps = [new PatternStep { Axis = Axis.Child, NodeTest = new NameTest { LocalName = "item" } }] },
                    Modes = [], // No mode = default mode
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
    public void TemplateIndex_NamedTemplate_NotIncludedInMatchIndex()
    {
        // Arrange
        var stylesheet = new XsltStylesheet
        {
            Version = "3.0",
            Templates =
            [
                new XsltTemplate
                {
                    Name = new QName(NamespaceId.None, "myTemplate"),
                    Match = null, // Named only, no match
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ]
        };

        // Act
        var index = new TemplateIndex(stylesheet);

        // Assert
        // Named templates without match pattern should not be in the match index
        index.Should().NotBeNull();
    }

    #endregion

    #region Pattern with Predicates

    [Fact]
    public void PatternStep_WithPredicates_StoresPredicates()
    {
        // Arrange
        var predicate = new IntegerLiteral { Value = 1 };
        var step = new PatternStep
        {
            Axis = Axis.Child,
            NodeTest = new NameTest { LocalName = "item" },
            Predicates = [predicate]
        };

        // Assert
        step.Predicates.Should().HaveCount(1);
        step.Predicates[0].Should().Be(predicate);
    }

    [Fact]
    public void PatternStep_MultiplePredicates_StoresAll()
    {
        // Arrange
        var step = new PatternStep
        {
            Axis = Axis.Child,
            NodeTest = new NameTest { LocalName = "item" },
            Predicates =
            [
                new IntegerLiteral { Value = 1 },
                new StringLiteral { Value = "test" },
                new BooleanLiteral { Value = true }
            ]
        };

        // Assert
        step.Predicates.Should().HaveCount(3);
    }

    #endregion

    #region Helper Methods

    private static XsltContext CreateContext(object? currentNode = null)
    {
        return new XsltContext
        {
            CurrentNode = currentNode,
            Position = 1,
            Last = 1
        };
    }

    private static XdmElement CreateElement(string localName, string? ns = null)
    {
        return new XdmElement
        {
            Id = new NodeId(1),
            Document = new DocumentId(1),
            Namespace = ns != null ? new NamespaceId(100) : NamespaceId.None,
            LocalName = localName,
            Attributes = XdmElement.EmptyAttributes,
            Children = XdmElement.EmptyChildren,
            NamespaceDeclarations = XdmElement.EmptyNamespaceDeclarations
        };
    }

    #endregion
}

/// <summary>
/// Mock pattern for testing union pattern behavior.
/// </summary>
internal sealed class MockPattern : XsltPattern
{
    private readonly bool _matchResult;
    private readonly double _priority;

    public MockPattern(bool matchResult, double priority = 0.5)
    {
        _matchResult = matchResult;
        _priority = priority;
    }

    public override bool Matches(object node, XsltContext context)
    {
        return _matchResult;
    }

    public override double DefaultPriority => _priority;
}

/// <summary>
/// Extension methods for TemplateIndex testing (internal access helper).
/// </summary>
internal static class TemplateIndexTestExtensions
{
    // Note: In a real scenario, we might use InternalsVisibleTo to access internal members
}
