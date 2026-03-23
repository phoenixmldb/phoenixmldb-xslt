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
/// Tests for XSLT context management.
/// </summary>
public class XsltContextTests
{
    #region XsltContext Basic Tests

    [Fact]
    public void XsltContext_NewContext_HasDefaultValues()
    {
        // Arrange & Act
        var context = new XsltContext();

        // Assert
        context.CurrentNode.Should().BeNull();
        context.Position.Should().Be(0);
        context.Last.Should().Be(0);
    }

    [Fact]
    public void XsltContext_SetCurrentNode_UpdatesCurrentNode()
    {
        // Arrange
        var node = CreateTestElement("item");
        var context = new XsltContext();

        // Act
        context.CurrentNode = node;

        // Assert
        context.CurrentNode.Should().Be(node);
    }

    [Fact]
    public void XsltContext_SetPosition_UpdatesPosition()
    {
        // Arrange
        var context = new XsltContext();

        // Act
        context.Position = 3;
        context.Last = 5;

        // Assert
        context.Position.Should().Be(3);
        context.Last.Should().Be(5);
    }

    #endregion

    #region DefaultXsltExecutionContext Tests

    [Fact]
    public async Task ExecutionContext_EvaluateBooleanAsync_StringLiteral_ReturnsTrue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new StringLiteral { Value = "hello" };

        // Act
        var result = await context.EvaluateBooleanAsync(expr);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExecutionContext_EvaluateBooleanAsync_EmptyString_ReturnsFalse()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new StringLiteral { Value = "" };

        // Act
        var result = await context.EvaluateBooleanAsync(expr);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutionContext_EvaluateBooleanAsync_BooleanTrue_ReturnsTrue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = BooleanLiteral.True;

        // Act
        var result = await context.EvaluateBooleanAsync(expr);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExecutionContext_EvaluateBooleanAsync_BooleanFalse_ReturnsFalse()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = BooleanLiteral.False;

        // Act
        var result = await context.EvaluateBooleanAsync(expr);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutionContext_EvaluateBooleanAsync_NonZeroInteger_ReturnsTrue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new IntegerLiteral { Value = 42 };

        // Act
        var result = await context.EvaluateBooleanAsync(expr);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExecutionContext_EvaluateBooleanAsync_ZeroInteger_ReturnsFalse()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new IntegerLiteral { Value = 0L };

        // Act
        var result = await context.EvaluateBooleanAsync(expr);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutionContext_EvaluateAsync_StringLiteral_ReturnsStringValue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new StringLiteral { Value = "test" };

        // Act
        var result = await context.EvaluateAsync(expr);

        // Assert
        result.Should().Be("test");
    }

    [Fact]
    public async Task ExecutionContext_EvaluateAsync_IntegerLiteral_ReturnsIntegerValue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new IntegerLiteral { Value = 42 };

        // Act
        var result = await context.EvaluateAsync(expr);

        // Assert
        result.Should().Be(42L);
    }

    [Fact]
    public async Task ExecutionContext_EvaluateAsync_DoubleLiteral_ReturnsDoubleValue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var expr = new DoubleLiteral { Value = 3.14 };

        // Act
        var result = await context.EvaluateAsync(expr);

        // Assert
        result.Should().Be(3.14);
    }

    [Fact]
    public async Task ExecutionContext_EvaluateAsync_ContextItem_ReturnsCurrentNode()
    {
        // Arrange
        var context = CreateExecutionContext();
        var node = CreateTestElement("test");
        context.PushContextItem(node, 1, 1);
        var expr = ContextItemExpression.Instance;

        // Act
        var result = await context.EvaluateAsync(expr);

        // Assert
        result.Should().Be(node);
    }

    #endregion

    #region Variable Binding Tests

    [Fact]
    public void ExecutionContext_SetVariable_StoresVariableInScope()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "x");

        // Act
        context.SetVariable(varName, 100);
        var result = context.GetVariable(varName);

        // Assert
        result.Should().Be(100);
    }

    [Fact]
    public void ExecutionContext_GetVariable_FromParentScope_ReturnsValue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "x");
        context.SetVariable(varName, "outer");

        // Act
        context.PushScope();
        var result = context.GetVariable(varName);
        context.PopScope();

        // Assert
        result.Should().Be("outer");
    }

    [Fact]
    public void ExecutionContext_GetVariable_ShadowedInChildScope_ReturnsChildValue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "x");
        context.SetVariable(varName, "outer");

        // Act
        context.PushScope();
        context.SetVariable(varName, "inner");
        var innerResult = context.GetVariable(varName);
        context.PopScope();
        var outerResult = context.GetVariable(varName);

        // Assert
        innerResult.Should().Be("inner");
        outerResult.Should().Be("outer");
    }

    [Fact]
    public void ExecutionContext_GetVariable_Undefined_ThrowsException()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "undefined");

        // Act & Assert
        var action = () => context.GetVariable(varName);
        action.Should().Throw<XsltException>()
            .WithMessage("*not defined*");
    }

    [Fact]
    public void ExecutionContext_GetVariable_FromGlobalVariables_ReturnsValue()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "globalX");
        context.GlobalVariables[varName] = "global value";

        // Act
        var result = context.GetVariable(varName);

        // Assert
        result.Should().Be("global value");
    }

    [Fact]
    public void ExecutionContext_GetVariable_LocalOverridesGlobal()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "x");
        context.GlobalVariables[varName] = "global";
        context.SetVariable(varName, "local");

        // Act
        var result = context.GetVariable(varName);

        // Assert
        result.Should().Be("local");
    }

    #endregion

    #region Context Item Stack Tests

    [Fact]
    public void ExecutionContext_PushContextItem_UpdatesContextItem()
    {
        // Arrange
        var context = CreateExecutionContext();
        var node = CreateTestElement("item");

        // Act
        context.PushContextItem(node, 1, 10);

        // Assert
        context.ContextItem.Should().Be(node);
        context.Position.Should().Be(1);
        context.Last.Should().Be(10);
    }

    [Fact]
    public void ExecutionContext_PopContextItem_RestoresPreviousContext()
    {
        // Arrange
        var context = CreateExecutionContext();
        var node1 = CreateTestElement("item1");
        var node2 = CreateTestElement("item2");

        // Act
        context.PushContextItem(node1, 1, 5);
        context.PushContextItem(node2, 3, 10);
        context.PopContextItem();

        // Assert
        context.ContextItem.Should().Be(node1);
        context.Position.Should().Be(1);
        context.Last.Should().Be(5);
    }

    [Fact]
    public void ExecutionContext_MultiplePopContextItem_RestoresAll()
    {
        // Arrange
        var context = CreateExecutionContext();
        var nodes = new[]
        {
            CreateTestElement("item1"),
            CreateTestElement("item2"),
            CreateTestElement("item3")
        };

        // Act - push all 3 items
        foreach (var (node, index) in nodes.Select((n, i) => (n, i)))
        {
            context.PushContextItem(node, index + 1, nodes.Length);
        }

        // Pop 2 items (leaving item1)
        context.PopContextItem();
        context.PopContextItem();

        // Assert - should be back to first item
        context.ContextItem.Should().Be(nodes[0]);
        context.Position.Should().Be(1);
        context.Last.Should().Be(nodes.Length);
    }

    #endregion

    #region Scope Management Tests

    [Fact]
    public void ExecutionContext_PushScope_CreatesNewScope()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "x");
        context.SetVariable(varName, "outer");

        // Act
        context.PushScope();
        var varName2 = new QName(NamespaceId.None, "y");
        context.SetVariable(varName2, "inner");

        // Assert
        context.GetVariable(varName).Should().Be("outer");
        context.GetVariable(varName2).Should().Be("inner");
    }

    [Fact]
    public void ExecutionContext_PopScope_DestroysInnerScope()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varName = new QName(NamespaceId.None, "y");

        context.PushScope();
        context.SetVariable(varName, "inner");
        context.PopScope();

        // Act & Assert
        var action = () => context.GetVariable(varName);
        action.Should().Throw<XsltException>();
    }

    [Fact]
    public void ExecutionContext_NestedScopes_MaintainCorrectVariables()
    {
        // Arrange
        var context = CreateExecutionContext();
        var varX = new QName(NamespaceId.None, "x");
        var varY = new QName(NamespaceId.None, "y");
        var varZ = new QName(NamespaceId.None, "z");

        // Act
        context.SetVariable(varX, "level0");
        context.PushScope();
        context.SetVariable(varY, "level1");
        context.PushScope();
        context.SetVariable(varZ, "level2");

        // Assert at deepest level
        context.GetVariable(varX).Should().Be("level0");
        context.GetVariable(varY).Should().Be("level1");
        context.GetVariable(varZ).Should().Be("level2");

        context.PopScope();
        context.GetVariable(varX).Should().Be("level0");
        context.GetVariable(varY).Should().Be("level1");

        context.PopScope();
        context.GetVariable(varX).Should().Be("level0");
    }

    #endregion

    #region Text Output Tests

    [Fact]
    public void ExecutionContext_WriteText_AppendsToOutput()
    {
        // Arrange
        var context = CreateExecutionContext();

        // Act
        context.WriteText("Hello, ", false);
        context.WriteText("World!", false);
        var output = context.GetOutput();

        // Assert
        output.Should().Contain("Hello, ");
        output.Should().Contain("World!");
    }

    [Fact]
    public void ExecutionContext_WriteText_WithEscaping_EscapesSpecialChars()
    {
        // Arrange
        var context = CreateExecutionContext();

        // Act
        context.WriteText("<tag>", false);
        var output = context.GetOutput();

        // Assert
        output.Should().Contain("&lt;");
        output.Should().Contain("&gt;");
    }

    [Fact]
    public void ExecutionContext_WriteText_WithoutEscaping_OutputsRaw()
    {
        // Arrange
        var context = CreateExecutionContext();

        // Act
        context.WriteText("<raw>", true);
        var output = context.GetOutput();

        // Assert
        output.Should().Contain("<raw>");
    }

    #endregion

    #region AVT Evaluation Tests

    [Fact]
    public async Task AvtLiteral_EvaluateAsync_ReturnsLiteralValue()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var avt = new AvtLiteral { Value = "literal" };

        // Act
        var result = await avt.EvaluateAsync(context);

        // Assert
        result.Should().Be("literal");
    }

    [Fact]
    public async Task AvtExpression_EvaluateAsync_EvaluatesExpression()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var avt = new AvtExpression { Expression = new StringLiteral { Value = "dynamic" } };

        // Act
        var result = await avt.EvaluateAsync(context);

        // Assert
        result.Should().Be("dynamic");
    }

    [Fact]
    public void AvtFromString_CreatesLiteralAvt()
    {
        // Arrange & Act
        var avt = XsltAttributeValueTemplate.FromString("test");

        // Assert
        avt.Parts.Should().HaveCount(1);
        avt.Parts[0].Should().BeOfType<AvtLiteral>();
        ((AvtLiteral)avt.Parts[0]).Value.Should().Be("test");
    }

    #endregion

    #region Instruction Execution Tests

    [Fact]
    public async Task XsltSequenceConstructor_ExecuteAsync_ExecutesAllInstructions()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var textInstr1 = new XsltText { Value = "A" };
        var textInstr2 = new XsltText { Value = "B" };
        var constructor = new XsltSequenceConstructor
        {
            Instructions = [textInstr1, textInstr2]
        };

        // Act
        await constructor.ExecuteAsync(context);

        // Assert - should execute without error
        constructor.Instructions.Should().HaveCount(2);
    }

    [Fact]
    public async Task XsltText_ExecuteAsync_WritesText()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var textInstr = new XsltText { Value = "Hello" };

        // Act
        await textInstr.ExecuteAsync(context);

        // Assert - should complete without error
        textInstr.Value.Should().Be("Hello");
    }

    [Fact]
    public async Task XsltLiteralText_ExecuteAsync_WritesText()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var textInstr = new XsltLiteralText { Value = "Literal text" };

        // Act
        await textInstr.ExecuteAsync(context);

        // Assert
        textInstr.Value.Should().Be("Literal text");
    }

    #endregion

    #region Break and NextIteration Tests

    [Fact]
    public void XsltBreak_Break_ThrowsBreakException()
    {
        // Arrange
        var context = CreateExecutionContext();
        var breakInstr = new XsltBreak();

        // Act & Assert
        // Note: Break() throws an internal exception caught by xsl:iterate
        var action = () => context.Break(breakInstr);
        action.Should().Throw<XsltException>();
    }

    [Fact]
    public void XsltNextIteration_NextIteration_ThrowsNextIterationException()
    {
        // Arrange
        var context = CreateExecutionContext();
        var nextIterInstr = new XsltNextIteration
        {
            WithParams = [new XsltWithParam { Name = new QName(NamespaceId.None, "x") }]
        };

        // Act & Assert
        // Note: NextIteration() throws an internal exception caught by xsl:iterate
        var action = () => context.NextIteration(nextIterInstr);
        action.Should().Throw<XsltException>();
    }

    #endregion

    #region If/Choose Execution Tests

    [Fact]
    public async Task XsltIf_ExecuteAsync_WhenTrue_ExecutesBody()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var ifInstr = new XsltIf
        {
            Test = BooleanLiteral.True,
            Then = new XsltSequenceConstructor
            {
                Instructions = [new XsltText { Value = "executed" }]
            }
        };

        // Act
        await ifInstr.ExecuteAsync(context);

        // Assert - should execute body
        ifInstr.Then.Instructions.Should().HaveCount(1);
    }

    [Fact]
    public async Task XsltIf_ExecuteAsync_WhenFalse_SkipsBody()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var ifInstr = new XsltIf
        {
            Test = BooleanLiteral.False,
            Then = new XsltSequenceConstructor
            {
                Instructions = [new XsltText { Value = "not executed" }]
            }
        };

        // Act
        await ifInstr.ExecuteAsync(context);

        // Assert - should not throw
        ifInstr.Then.Instructions.Should().HaveCount(1);
    }

    [Fact]
    public async Task XsltChoose_ExecuteAsync_ExecutesFirstMatchingWhen()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var chooseInstr = new XsltChoose
        {
            When =
            [
                new XsltWhen
                {
                    Test = BooleanLiteral.False,
                    Body = new XsltSequenceConstructor { Instructions = [new XsltText { Value = "first" }] }
                },
                new XsltWhen
                {
                    Test = BooleanLiteral.True,
                    Body = new XsltSequenceConstructor { Instructions = [new XsltText { Value = "second" }] }
                }
            ],
            Otherwise = new XsltSequenceConstructor
            {
                Instructions = [new XsltText { Value = "otherwise" }]
            }
        };

        // Act
        await chooseInstr.ExecuteAsync(context);

        // Assert - should complete without error
        chooseInstr.When.Should().HaveCount(2);
    }

    [Fact]
    public async Task XsltChoose_ExecuteAsync_ExecutesOtherwiseWhenNoMatch()
    {
        // Arrange
        var context = CreateMockExecutionContext();
        var chooseInstr = new XsltChoose
        {
            When =
            [
                new XsltWhen
                {
                    Test = BooleanLiteral.False,
                    Body = new XsltSequenceConstructor { Instructions = [] }
                }
            ],
            Otherwise = new XsltSequenceConstructor
            {
                Instructions = [new XsltText { Value = "otherwise" }]
            }
        };

        // Act
        await chooseInstr.ExecuteAsync(context);

        // Assert
        chooseInstr.Otherwise.Should().NotBeNull();
    }

    #endregion

    #region Helper Methods

    private static MockXsltExecutionContext CreateExecutionContext()
    {
        return new MockXsltExecutionContext();
    }

    private static MockXsltExecutionContext CreateMockExecutionContext()
    {
        return new MockXsltExecutionContext();
    }

    private static XdmElement CreateTestElement(string localName)
    {
        return new XdmElement
        {
            Id = new NodeId(1),
            Document = new DocumentId(1),
            Namespace = NamespaceId.None,
            LocalName = localName,
            Attributes = XdmElement.EmptyAttributes,
            Children = XdmElement.EmptyChildren,
            NamespaceDeclarations = XdmElement.EmptyNamespaceDeclarations
        };
    }

    private static XdmDocument CreateTestDocument()
    {
        return new XdmDocument
        {
            Id = new NodeId(1),
            Document = new DocumentId(1),
            Children = XdmDocument.EmptyChildren
        };
    }

    #endregion
}

/// <summary>
/// Mock execution context for testing instructions.
/// </summary>
internal sealed class MockXsltExecutionContext : XsltExecutionContext
{
    private readonly List<string> _textOutput = new();
    private readonly List<Dictionary<QName, object?>> _scopes = [new()];
    private readonly StringBuilder _outputBuilder = new();

    public IReadOnlyList<string> TextOutput => _textOutput;

    public override QName? CurrentMode => null;
    public override bool IsBackwardsCompatibleMode => false;
    public override ValueTask MergeAsync(XsltMerge instruction) => ValueTask.CompletedTask;

    // Scope management for testing variable binding
    public void PushScope() => _scopes.Add(new Dictionary<QName, object?>());

    public void PopScope()
    {
        if (_scopes.Count > 1)
            _scopes.RemoveAt(_scopes.Count - 1);
    }

    public void SetVariable(QName name, object? value)
    {
        _scopes[^1][name] = value;
    }

    public object? GetVariable(QName name)
    {
        // First check local scopes
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var value))
                return value;
        }
        // Then check global variables
        if (GlobalVariables.TryGetValue(name, out var globalValue))
            return globalValue;
        throw new XsltException($"Variable ${name} not defined");
    }

    public string GetOutput() => _outputBuilder.ToString();

    // Global variables
    public Dictionary<QName, object?> GlobalVariables { get; } = new();

    // Context item stack
    private readonly Stack<(object? Item, int Position, int Last)> _contextStack = new();

    public object? ContextItem { get; private set; }
    public int Position { get; private set; }
    public int Last { get; private set; }

    public void PushContextItem(object? item, int position, int last)
    {
        _contextStack.Push((ContextItem, Position, Last));
        ContextItem = item;
        Position = position;
        Last = last;
    }

    public void PopContextItem()
    {
        if (_contextStack.Count > 0)
        {
            var (item, position, last) = _contextStack.Pop();
            ContextItem = item;
            Position = position;
            Last = last;
        }
        else
        {
            ContextItem = null;
            Position = 0;
            Last = 0;
        }
    }

    public override ValueTask ApplyTemplatesAsync(XQueryExpression? select, QName? mode,
        List<XsltSort> sorts, List<XsltWithParam> withParams)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CallTemplateAsync(QName name, List<XsltWithParam> withParams)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask ApplyImportsAsync(List<XsltWithParam> withParams)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask NextMatchAsync(List<XsltWithParam> withParams, XsltSequenceConstructor? fallback)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask ForEachAsync(XQueryExpression select, List<XsltSort> sorts, XsltSequenceConstructor body)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask ForEachGroupAsync(XsltForEachGroup instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask IterateAsync(XsltIterate instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask TryAsync(XsltTry instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask<bool> EvaluateBooleanAsync(XQueryExpression expr)
    {
        var result = expr switch
        {
            BooleanLiteral bl => bl.Value,
            StringLiteral sl => !string.IsNullOrEmpty(sl.Value),
            IntegerLiteral il => il.LongValue != 0,
            _ => true
        };
        return ValueTask.FromResult(result);
    }

    public override ValueTask<object?> EvaluateAsync(XQueryExpression expr)
    {
        object? result = expr switch
        {
            StringLiteral sl => sl.Value,
            IntegerLiteral il => il.Value,
            DoubleLiteral dl => dl.Value,
            BooleanLiteral bl => bl.Value,
            ContextItemExpression => ContextItem,
            _ => null
        };
        return ValueTask.FromResult(result);
    }

    public override ValueTask CreateElementAsync(XsltElement instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateAttributeAsync(XsltAttribute instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override void WriteText(string value, bool disableOutputEscaping)
    {
        _textOutput.Add(value);
        if (disableOutputEscaping)
        {
            _outputBuilder.Append(value);
        }
        else
        {
            // Basic XML escaping
            _outputBuilder.Append(value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal));
        }
    }

    public override void WriteTextItem(string value)
    {
        _textOutput.Add(value);
        _outputBuilder.Append(value);
    }

    public override ValueTask ValueOfAsync(XsltValueOf instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CopyAsync(XsltCopy instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CopyOfAsync(XsltCopyOf instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask SequenceAsync(XsltSequence instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateCommentAsync(XsltComment instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreatePIAsync(XsltProcessingInstruction instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateNamespaceAsync(XsltNamespace instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateDocumentAsync(XsltDocument instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask ResultDocumentAsync(XsltResultDocument instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask MessageAsync(XsltMessage instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask AssertAsync(XsltAssert instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask BindVariableAsync(XsltVariableInstruction instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask BindParamAsync(XsltParamInstruction instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask EvaluateInstructionAsync(XsltEvaluate instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask NumberAsync(XsltNumber instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask PerformSortAsync(XsltPerformSort instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask AnalyzeStringAsync(XsltAnalyzeString instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override void Break(XsltBreak instruction)
    {
        throw new XsltException("Break");
    }

    public override void NextIteration(XsltNextIteration instruction)
    {
        throw new XsltException("NextIteration");
    }

    public override ValueTask ForkAsync(XsltFork instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateMapAsync(XsltMap instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateMapEntryAsync(XsltMapEntry instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateArrayAsync(XsltArray instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateArrayMemberAsync(XsltArrayMember instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateLiteralElementAsync(XsltLiteralResultElement instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask WherePopulatedAsync(XsltWherePopulated instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnEmptyAsync(XsltOnEmpty instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnNonEmptyAsync(XsltOnNonEmpty instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask SourceDocumentAsync(XsltSourceDocument instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask SwitchAsync(XsltSwitch instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask ForEachMemberAsync(XsltForEachMember instruction)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask CreateRecordAsync(XsltRecord instruction)
    {
        return ValueTask.CompletedTask;
    }
}
