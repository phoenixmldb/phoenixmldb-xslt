using FluentAssertions;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
using PhoenixmlDb.XQuery.Ast;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class ContextItemUsageTests
{
    private static XsltSequenceConstructor Body(params XsltInstruction[] insns) =>
        new() { Instructions = insns };

    // A body that copies the context item transmits it → must materialize a node.
    [Fact]
    public void CopyOfContextItem_IsTransmission()
    {
        var body = Body(new XsltCopyOf { Select = ContextItemExpression.Instance });
        UsageClassifier.ClassifyBodyContextItemUsage(body, selectAtomized: false)
            .Should().Be(Usage.Transmission);
    }

    // An explicit data()-wrapped select forces Absorption regardless of the body (si-copy-002).
    [Fact]
    public void SelectAtomized_ForcesAbsorption_EvenWhenBodyCopies()
    {
        var body = Body(new XsltCopyOf { Select = ContextItemExpression.Instance });
        UsageClassifier.ClassifyBodyContextItemUsage(body, selectAtomized: true)
            .Should().Be(Usage.Absorption);
    }

    // A value-reading body absorbs.
    [Fact]
    public void ValueOfContextItem_IsAbsorption()
    {
        var body = Body(new XsltValueOf { Select = ContextItemExpression.Instance });
        UsageClassifier.ClassifyBodyContextItemUsage(body, selectAtomized: false)
            .Should().Be(Usage.Absorption);
    }

    // Leading insignificant whitespace-only literal text is skipped when finding the effective instruction.
    [Fact]
    public void LeadingWhitespace_SkippedBeforeCopyOf()
    {
        var body = Body(
            new XsltLiteralText { Value = "\n  " },
            new XsltCopyOf { Select = ContextItemExpression.Instance });
        UsageClassifier.ClassifyBodyContextItemUsage(body, selectAtomized: false)
            .Should().Be(Usage.Transmission);
    }
}
