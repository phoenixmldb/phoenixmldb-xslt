using FluentAssertions;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class AnnotationPopulatorTests
{
    private static XsltSequenceConstructor Unit(string text) =>
        new() { Instructions = new XsltInstruction[] { new XsltLiteralText { Value = text } } };

    // Activating the dead side-table: after population, each unit's annotation is present and
    // equals a fresh classification (validates Set/TryGet plumbing, identity keying, and the
    // ConditionalWeakTable boxing round-trip). Per-node compositional annotation is SP-B's job.
    [Fact]
    public void Annotate_PopulatesTable_TryGetRoundTripsClassifier()
    {
        var unitA = Unit("a");
        var unitB = Unit("b");
        var ctx = new StreamingContext(Posture.Striding, InStreamedScope: true);

        AnnotationPopulator.Annotate(new XsltInstruction[] { unitA, unitB }, ctx);

        StreamabilityAnnotation.TryGet(unitA, out var storedA).Should().BeTrue();
        storedA.Should().Be(StreamabilityClassifier.Classify(unitA, ctx));

        StreamabilityAnnotation.TryGet(unitB, out var storedB).Should().BeTrue();
        storedB.Should().Be(StreamabilityClassifier.Classify(unitB, ctx));
    }

    // A unit that was never annotated must not produce a phantom hit — guards against
    // the table returning a value for the wrong key.
    [Fact]
    public void TryGet_UnannotatedUnit_ReturnsFalse()
    {
        var annotated = Unit("x");
        var unannotated = Unit("y");
        var ctx = new StreamingContext(Posture.Striding, InStreamedScope: true);

        AnnotationPopulator.Annotate(new XsltInstruction[] { annotated }, ctx);

        StreamabilityAnnotation.TryGet(unannotated, out _).Should().BeFalse();
    }
}
